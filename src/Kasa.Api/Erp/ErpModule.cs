using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Kasa.Api.Banking;
using Kasa.Api.Common;
using Kasa.Api.Data;
using Kasa.Api.Domain;
using Kasa.Contracts;
using Kasa.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using SuppressInstrumentationScope = OpenTelemetry.SuppressInstrumentationScope;

namespace Kasa.Api.Erp;

public sealed class ErpOptions
{
    [Required, Url]
    public string BaseUrl { get; set; } = "";

    [Required]
    public string ApiKey { get; set; } = "";
}

public sealed class OutboxOptions
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    [Range(1, 1000)]
    public int BatchSize { get; set; } = 50;
}

public sealed record ErpCurrentAccount(string Code, string Title, string? TaxNumber, string? Iban);

/// <summary>Reads current accounts from the ERP. Writing vouchers is not here: that goes through RabbitMQ and Kasa.ErpExporter.</summary>
public sealed class ErpReader(HttpClient http, IOptions<ErpOptions> options)
{
    public async Task<IReadOnlyList<ErpCurrentAccount>> CurrentAccountsAsync(string firmCode, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/firms/{Uri.EscapeDataString(firmCode)}/current-accounts");
        request.Headers.Add("X-Api-Key", options.Value.ApiKey);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"ERP HTTP {(int)response.StatusCode}", null, response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<List<ErpCurrentAccount>>(ct) ?? [];
    }
}

public static class ErpSetup
{
    public static IServiceCollection AddKasaErp(this IServiceCollection services)
    {
        RabbitConnection.Add(services);
        services.AddOptions<ErpOptions>().BindConfiguration("Erp").ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<OutboxOptions>().BindConfiguration("Outbox").ValidateDataAnnotations();
        services.AddHttpClient<ErpReader>((sp, http) =>
        {
            http.BaseAddress = new Uri(sp.GetRequiredService<IOptions<ErpOptions>>().Value.BaseUrl.TrimEnd('/') + "/");
            http.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddScoped<Matching>();
        services.AddHostedService<OutboxPublisher>();
        services.AddHostedService<ExportResultConsumer>();
        return services;
    }
}

/// <summary>
/// Moves outbox rows to RabbitMQ. Rows are picked with UPDLOCK + READPAST, so several API instances share the work
/// without taking the same row. A row is marked sent only after the broker confirmed it; if the app dies in between,
/// it is sent again, and the exporter's idempotency makes that harmless (at-least-once delivery).
/// </summary>
public sealed class OutboxPublisher(IServiceScopeFactory scopes, RabbitConnection rabbit, IOptions<OutboxOptions> options, TimeProvider clock,
    ILogger<OutboxPublisher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IChannel? channel = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (channel is not { IsOpen: true })
                {
                    channel?.Dispose();
                    channel = await (await rabbit.GetAsync(stoppingToken)).CreateChannelAsync(RabbitConnection.Confirming, stoppingToken);
                    await rabbit.Topology.DeclareAsync(channel, stoppingToken);
                }

                // A full batch means there is probably more: go again at once.
                if (await PublishBatchAsync(channel, stoppingToken) == options.Value.BatchSize)
                {
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // RabbitMQ or SQL Server is down: the rows wait in the table, nothing is lost.
                log.LogWarning("Outbox could not publish, trying again: {Error}", ex.Message);
                channel?.Dispose();
                channel = null;
            }

            try
            {
                await Task.Delay(options.Value.PollInterval, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        channel?.Dispose();
    }

    private async Task<int> PublishBatchAsync(IChannel channel, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        List<OutboxMessage> batch;
        // The poll runs every second; its SQL would be a trace of its own each time. Only real publishing is traced.
        using (SuppressInstrumentationScope.Begin())
        {
            batch = await db.OutboxMessages
                .FromSql($"SELECT TOP ({options.Value.BatchSize}) * FROM OutboxMessages WITH (UPDLOCK, READPAST, ROWLOCK) WHERE PublishedAt IS NULL ORDER BY CreatedAt")
                .ToListAsync(ct);
        }

        Exception? failure = null;
        foreach (var message in batch)
        {
            // Continues the trace of the request that matched the movement: one trace from the click to the ERP voucher.
            ActivityContext.TryParse(message.TraceParent, null, out var parent);
            using var activity = ServiceDefaultsExtensions.Source.StartActivity("outbox publish", ActivityKind.Producer, parent);
            activity?.SetTag("kasa.outbox.id", message.Id);
            try
            {
                await channel.BasicPublishAsync(rabbit.Topology.Exchange, Topology.ExportKey, mandatory: true,
                    new BasicProperties { Persistent = true, ContentType = "application/json", MessageId = message.Id.ToString(), Type = message.Type },
                    System.Text.Encoding.UTF8.GetBytes(message.Payload), ct);
                message.PublishedAt = clock.GetUtcNow();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                message.Attempts++;
                message.LastError = ex.Message.Length <= BankConnection.ErrorMaxLength ? ex.Message : ex.Message[..BankConnection.ErrorMaxLength];
                failure = ex;
                break;
            }
        }

        // shortcut: sent rows are kept for inspection; delete those older than a few weeks with a job once the table grows.
        using (SuppressInstrumentationScope.Begin())
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        if (failure is not null)
        {
            // What was published before the failure stays marked; the rest goes again on the next round.
            throw failure;
        }

        return batch.Count;
    }
}

/// <summary>Reads erp.results and records on the movement whether the ERP took it. A result for an older export id is ignored.</summary>
public sealed class ExportResultConsumer(RabbitConnection rabbit, IServiceScopeFactory scopes, TimeProvider clock, ILogger<ExportResultConsumer> log)
    : QueueConsumer(rabbit, log)
{
    protected override string Queue => Topology.ResultsQueue;

    protected override async Task HandleAsync(IChannel channel, IChannel publisher, BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        ExportResult? result = null;
        try
        {
            result = JsonSerializer.Deserialize<ExportResult>(delivery.Body.Span, RabbitConnection.Json);
        }
        catch (JsonException)
        {
            // Handled below.
        }

        if (result is not null)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var transaction = await db.BankTransactions.IgnoreQueryFilters().SingleOrDefaultAsync(t => t.ExportId == result.ExportId, ct);
            if (transaction is not null)
            {
                scope.ServiceProvider.GetRequiredService<TenantContext>().Use(transaction.CompanyId);
                if (result.Outcome == ExportOutcome.Exported)
                {
                    (transaction.ExportStatus, transaction.ErpVoucherNo, transaction.ExportError, transaction.ExportedAt) =
                        (ExportStatus.Exported, result.VoucherNo, null, clock.GetUtcNow());
                }
                else
                {
                    (transaction.ExportStatus, transaction.ExportError) = (ExportStatus.Failed,
                        result.Error is { Length: > BankConnection.ErrorMaxLength } e ? e[..BankConnection.ErrorMaxLength] : result.Error);
                }

                await db.SaveChangesAsync(ct);
                await scope.ServiceProvider.GetRequiredService<Dashboard.CompanyEvents>().ChangedAsync(transaction.CompanyId, "exports");
            }
        }
        else
        {
            log.LogError("Unreadable export result {MessageId} dropped", delivery.BasicProperties.MessageId);
        }

        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
    }
}
