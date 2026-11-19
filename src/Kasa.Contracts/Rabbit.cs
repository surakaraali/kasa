using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Kasa.Contracts;

public sealed class RabbitOptions
{
    [Required]
    public string HostName { get; set; } = "localhost";

    public int Port { get; set; } = 5672;

    [Required]
    public string UserName { get; set; } = "";

    [Required]
    public string Password { get; set; } = "";

    public string VirtualHost { get; set; } = "/";

    /// <summary>Put before every exchange and queue name, so the tests of one run have their own.</summary>
    public string Prefix { get; set; } = "";

    /// <summary>How long a failed export waits before the next try. Both apps declare the queues, so both must agree.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>When set, a queue nobody uses for this long is deleted by RabbitMQ (x-expires). For test runs.</summary>
    public TimeSpan? QueueExpiry { get; set; }
}

/// <summary>
/// The exchange and queues, declared the same way by both apps (declaring is idempotent), so neither depends on the
/// other having started first.
/// <code>
/// kasa.erp ─voucher.export─▶ erp.export ──▶ exporter
///          ─voucher.retry──▶ erp.export.retry (waits RetryDelay, then dead-letters back to voucher.export)
///          ─voucher.dead───▶ erp.export.dead (for a person to look at)
///          ─voucher.result─▶ erp.results ──▶ api
/// </code>
/// </summary>
public sealed class Topology(RabbitOptions options)
{
    public const string ExportKey = "voucher.export";
    public const string RetryKey = "voucher.retry";
    public const string DeadKey = "voucher.dead";
    public const string ResultKey = "voucher.result";

    /// <summary>The number of the delivery attempt, on retried messages.</summary>
    public const string AttemptHeader = "x-kasa-attempt";

    public string Exchange => $"{options.Prefix}kasa.erp";
    public string ExportQueue => $"{options.Prefix}erp.export";
    public string RetryQueue => $"{options.Prefix}erp.export.retry";
    public string DeadQueue => $"{options.Prefix}erp.export.dead";
    public string ResultsQueue => $"{options.Prefix}erp.results";

    public async Task DeclareAsync(IChannel channel, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: ct);
        await DeclareQueueAsync(channel, ExportQueue, ExportKey, null, ct);
        await DeclareQueueAsync(channel, RetryQueue, RetryKey, new Dictionary<string, object?>
        {
            ["x-message-ttl"] = (int)options.RetryDelay.TotalMilliseconds,
            ["x-dead-letter-exchange"] = Exchange,
            ["x-dead-letter-routing-key"] = ExportKey,
        }, ct);
        await DeclareQueueAsync(channel, DeadQueue, DeadKey, null, ct);
        await DeclareQueueAsync(channel, ResultsQueue, ResultKey, null, ct);
    }

    private async Task DeclareQueueAsync(IChannel channel, string queue, string key, Dictionary<string, object?>? arguments, CancellationToken ct)
    {
        arguments ??= [];
        if (options.QueueExpiry is { } expiry)
        {
            arguments["x-expires"] = (int)expiry.TotalMilliseconds;
        }

        // Quorum queues are replicated and survive a broker restart; the default (classic) ones are not.
        arguments["x-queue-type"] = "quorum";
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, arguments: arguments, cancellationToken: ct);
        await channel.QueueBindAsync(queue, Exchange, key, cancellationToken: ct);
    }
}

/// <summary>One connection per app, opened on first use and opened again after it drops.</summary>
public sealed class RabbitConnection(IOptions<RabbitOptions> options) : IAsyncDisposable
{
    /// <summary>Every publish waits for the broker's confirm; a message the broker did not take throws.</summary>
    public static readonly CreateChannelOptions Confirming = new(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    public Topology Topology { get; } = new(options.Value);

    public static IServiceCollection Add(IServiceCollection services)
    {
        services.AddOptions<RabbitOptions>().BindConfiguration("RabbitMq").ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<RabbitConnection>();
        return services;
    }

    public async Task<IConnection> GetAsync(CancellationToken ct)
    {
        if (_connection is { IsOpen: true })
        {
            return _connection;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_connection is { IsOpen: true })
            {
                return _connection;
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync();
            }

            var o = options.Value;
            // Recovery is ours: a dropped connection is replaced on the next use, and consumers restart their loop.
            var factory = new ConnectionFactory
            {
                HostName = o.HostName,
                Port = o.Port,
                UserName = o.UserName,
                Password = o.Password,
                VirtualHost = o.VirtualHost,
                AutomaticRecoveryEnabled = false,
                RequestedConnectionTimeout = TimeSpan.FromSeconds(3),
            };
            _connection = await factory.CreateConnectionAsync(AppDomain.CurrentDomain.FriendlyName, ct);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    public static async Task PublishAsync<T>(IChannel channel, string exchange, string routingKey, T message, string messageId,
        IDictionary<string, object?>? headers, CancellationToken ct)
    {
        var properties = new BasicProperties
        {
            Persistent = true,
            ContentType = "application/json",
            MessageId = messageId,
            Type = typeof(T).Name,
            Headers = headers,
        };
        // mandatory: a message no queue takes comes back as an error instead of vanishing.
        await channel.BasicPublishAsync(exchange, routingKey, mandatory: true, properties, JsonSerializer.SerializeToUtf8Bytes(message, Json), ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _gate.Dispose();
    }
}

/// <summary>
/// Reads one queue with manual acks, and keeps reading: when RabbitMQ is down or the channel closes, it waits and
/// starts again. <see cref="HandleAsync"/> acks or nacks each delivery itself. Messages are handled one at a time.
/// </summary>
public abstract class QueueConsumer(RabbitConnection rabbit, ILogger log) : BackgroundService
{
    private static readonly TimeSpan RetryConnect = TimeSpan.FromSeconds(2);

    protected Topology Topology => rabbit.Topology;

    protected abstract string Queue { get; }

    /// <param name="publisher">A separate channel with publisher confirms, for messages the handler sends on.</param>
    protected abstract Task HandleAsync(IChannel channel, IChannel publisher, BasicDeliverEventArgs delivery, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var connection = await rabbit.GetAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                await using var publisher = await connection.CreateChannelAsync(RabbitConnection.Confirming, stoppingToken);
                await Topology.DeclareAsync(channel, stoppingToken);
                await channel.BasicQosAsync(0, 10, false, stoppingToken);

                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                channel.ChannelShutdownAsync += (_, _) => Closed(closed);
                publisher.ChannelShutdownAsync += (_, _) => Closed(closed);
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += async (_, delivery) =>
                {
                    try
                    {
                        await HandleAsync(channel, publisher, delivery, stoppingToken);
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        // Not the message's fault (a database or broker hiccup): give it back and slow down a little.
                        log.LogError(ex, "Handling a message from {Queue} failed; it goes back to the queue", Queue);
                        await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
                        if (channel.IsOpen)
                        {
                            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, CancellationToken.None);
                        }
                    }
                };
                await channel.BasicConsumeAsync(Queue, autoAck: false, consumer, stoppingToken);
                log.LogInformation("Consuming {Queue}", Queue);
                await closed.Task.WaitAsync(stoppingToken);
                log.LogWarning("The RabbitMQ channel for {Queue} closed; starting again", Queue);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning("RabbitMQ is not reachable for {Queue}, trying again in {Delay}: {Error}", Queue, RetryConnect, ex.Message);
                try
                {
                    await Task.Delay(RetryConnect, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private static Task Closed(TaskCompletionSource closed)
    {
        closed.TrySetResult();
        return Task.CompletedTask;
    }
}
