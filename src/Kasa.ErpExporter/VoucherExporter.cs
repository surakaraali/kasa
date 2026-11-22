using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kasa.Contracts;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Kasa.ErpExporter;

public sealed class ErpOptions
{
    [Required, Url]
    public string BaseUrl { get; set; } = "";

    [Required]
    public string ApiKey { get; set; } = "";

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Tries before a message goes to the dead-letter queue.</summary>
    [Range(1, 50)]
    public int MaxAttempts { get; set; } = 5;
}

public enum ErpCallOutcome
{
    Created,

    /// <summary>Down, slow, 5xx: worth trying again later.</summary>
    Transient,

    /// <summary>The ERP refused the voucher (unknown current account, bad data): trying again gives the same answer.</summary>
    Permanent,
}

public sealed record ErpCallResult(ErpCallOutcome Outcome, string? VoucherNo, string? Error);

public sealed class ErpClient(HttpClient http, IOptions<ErpOptions> options)
{
    public async Task<ErpCallResult> CreateVoucherAsync(ExportVoucher message, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/firms/{Uri.EscapeDataString(message.ErpFirmCode)}/vouchers")
        {
            Content = JsonContent.Create(new
            {
                type = message.Amount > 0 ? "Tahsilat" : "Tediye",
                date = message.Date,
                amount = Math.Abs(message.Amount),
                currentAccountCode = message.CurrentAccountCode,
                bankIban = message.BankIban,
                description = message.Description,
                reference = message.BankReference,
            }),
        };
        request.Headers.Add("X-Api-Key", options.Value.ApiKey);
        // The same export id every time this message is delivered: the ERP makes one voucher however often we ask.
        request.Headers.Add("Idempotency-Key", message.ExportId.ToString());

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            return new ErpCallResult(ErpCallOutcome.Transient, null, $"ERP'ye ulaşılamadı: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ErpCallResult(ErpCallOutcome.Transient, null, "ERP zamanında cevap vermedi");
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                // A 2xx without a readable voucher number is retried with the same key: the ERP replays what it wrote.
                try
                {
                    var voucher = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                    if (voucher.ValueKind == JsonValueKind.Object && voucher.TryGetProperty("voucherNo", out var no) && no.GetString() is { Length: > 0 } voucherNo)
                    {
                        return new ErpCallResult(ErpCallOutcome.Created, voucherNo, null);
                    }
                }
                catch (JsonException)
                {
                    // Falls through to the retry below.
                }

                return new ErpCallResult(ErpCallOutcome.Transient, null, "ERP okunamayan bir cevap verdi");
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var status = (int)response.StatusCode;
            var transient = status >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
            return new ErpCallResult(transient ? ErpCallOutcome.Transient : ErpCallOutcome.Permanent, null,
                $"ERP HTTP {status}: {(body.Length > 300 ? body[..300] : body)}");
        }
    }
}

/// <summary>
/// Reads erp.export and writes each message to the ERP as a voucher. A transient failure goes to the retry queue,
/// which hands it back after a delay; after MaxAttempts, or on a refusal, it goes to the dead-letter queue and Kasa
/// is told it failed. The result is published before the ack: if the process dies in between, the message comes
/// again and the ERP's Idempotency-Key returns the voucher it already made.
/// </summary>
public sealed class VoucherExporter(RabbitConnection rabbit, ErpClient erp, IOptions<ErpOptions> options, ILogger<VoucherExporter> log)
    : QueueConsumer(rabbit, log)
{
    protected override string Queue => Topology.ExportQueue;

    protected override async Task HandleAsync(IChannel channel, IChannel publisher, BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        ExportVoucher? message;
        try
        {
            message = JsonSerializer.Deserialize<ExportVoucher>(delivery.Body.Span, RabbitConnection.Json);
        }
        catch (JsonException)
        {
            message = null;
        }

        if (message is null)
        {
            // Unreadable: no retry will fix it, and there is no export id to report back.
            log.LogError("Unreadable message {MessageId} sent to the dead-letter queue", delivery.BasicProperties.MessageId);
            await publisher.BasicPublishAsync(Topology.Exchange, Topology.DeadKey, mandatory: true, new BasicProperties(delivery.BasicProperties), delivery.Body, ct);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
            return;
        }

        var attempt = Attempt(delivery);
        var result = await erp.CreateVoucherAsync(message, ct);
        switch (result.Outcome)
        {
            case ErpCallOutcome.Created:
                log.LogInformation("Export {ExportId} written to the ERP as {VoucherNo}", message.ExportId, result.VoucherNo);
                await ReportAsync(publisher, message, ExportOutcome.Exported, result.VoucherNo, null, ct);
                break;

            case ErpCallOutcome.Transient when attempt < options.Value.MaxAttempts:
                log.LogWarning("Export {ExportId} attempt {Attempt} failed, trying again later: {Error}", message.ExportId, attempt, result.Error);
                await RabbitConnection.PublishAsync(publisher, Topology.Exchange, Topology.RetryKey, message, message.ExportId.ToString(),
                    new Dictionary<string, object?> { [Topology.AttemptHeader] = attempt + 1 }, ct);
                break;

            default:
                var error = result.Outcome == ErpCallOutcome.Transient ? $"{attempt} denemede aktarılamadı. Son hata: {result.Error}" : result.Error;
                log.LogError("Export {ExportId} failed for good after {Attempt} attempt(s): {Error}", message.ExportId, attempt, error);
                await RabbitConnection.PublishAsync(publisher, Topology.Exchange, Topology.DeadKey, message, message.ExportId.ToString(),
                    new Dictionary<string, object?> { [Topology.AttemptHeader] = attempt, ["x-kasa-error"] = error }, ct);
                await ReportAsync(publisher, message, ExportOutcome.Failed, null, error, ct);
                break;
        }

        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
    }

    private Task ReportAsync(IChannel publisher, ExportVoucher message, ExportOutcome outcome, string? voucherNo, string? error, CancellationToken ct) =>
        RabbitConnection.PublishAsync(publisher, Topology.Exchange, Topology.ResultKey,
            new ExportResult(message.ExportId, message.CompanyId, outcome, voucherNo, error), message.ExportId.ToString(), null, ct);

    private static int Attempt(BasicDeliverEventArgs delivery) =>
        delivery.BasicProperties.Headers?.TryGetValue(Topology.AttemptHeader, out var value) == true
            ? Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture)
            : 1;
}

public sealed class ErpHealthCheck(IHttpClientFactory http, IOptions<ErpOptions> options) : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    public async Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            using var client = http.CreateClient(nameof(ErpClient));
            using var response = await client.GetAsync(new Uri(new Uri(options.Value.BaseUrl), "/health"), ct);
            return response.IsSuccessStatusCode
                ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy()
                : Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Degraded($"ERP HTTP {(int)response.StatusCode}: aktarımlar kuyrukta bekliyor");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Degraded("ERP'ye ulaşılamıyor: aktarımlar kuyrukta bekliyor");
        }
    }
}
