using Kasa.Api.Data;
using Kasa.Contracts;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kasa.Api.Infrastructure;

/// <summary>
/// SQL Server is the only dependency without which the API cannot work (unhealthy). Redis, MongoDB and RabbitMQ
/// each have a way to carry on without them (decisions 0005, 0008, 0009), so their absence is "degraded".
/// </summary>
public static class KasaHealthChecks
{
    public static IServiceCollection AddKasaHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddDbContextCheck<AppDbContext>("sqlserver")
            .AddCheck<RedisHealthCheck>("redis")
            .AddCheck<MongoHealthCheck>("mongodb")
            .Add(new HealthCheckRegistration("rabbitmq", sp => new RabbitHealthCheck(sp.GetRequiredService<RabbitConnection>(), HealthStatus.Degraded), null, null));
        return services;
    }
}

public sealed class RedisHealthCheck(KasaRedis redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await redis.Db.PingAsync();
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (KasaRedis.IsUnavailable(ex))
        {
            return HealthCheckResult.Degraded("Redis'e ulaşılamıyor: önbelleksiz ve kilitsiz çalışılıyor");
        }
    }
}

public sealed class MongoHealthCheck(IMongoClient mongo) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await mongo.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            return HealthCheckResult.Degraded("MongoDB'ye ulaşılamıyor: ham cevaplar arşivlenmiyor");
        }
    }
}
