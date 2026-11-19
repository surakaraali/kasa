using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;

namespace Kasa.Api.Infrastructure;

public sealed class RedisOptions
{
    [Required]
    public string Configuration { get; set; } = "";

    /// <summary>Every key starts with this, so tests and several apps can share one Redis.</summary>
    [Required]
    public string KeyPrefix { get; set; } = "kasa:";
}

/// <summary>
/// Redis speeds things up and coordinates, but nothing depends on it being up: every caller has a way to carry on
/// without it. So the connection never blocks start-up, and commands give up after a second.
/// </summary>
public sealed class KasaRedis(IConnectionMultiplexer connection, IOptions<RedisOptions> options, ILogger<KasaRedis> log)
{
    // Deletes the lock only if it is still ours: after a timeout it may already belong to someone else.
    private const string ReleaseScript = "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";

    public IDatabase Db { get; } = connection.GetDatabase().WithKeyPrefix(options.Value.KeyPrefix);

    public bool IsConnected => connection.IsConnected;

    public static IServiceCollection Add(IServiceCollection services)
    {
        services.AddOptions<RedisOptions>().BindConfiguration("Redis").ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var config = ConfigurationOptions.Parse(sp.GetRequiredService<IOptions<RedisOptions>>().Value.Configuration);
            config.AbortOnConnectFail = false;
            config.ConnectTimeout = 1000;
            config.SyncTimeout = 1000;
            config.AsyncTimeout = 1000;
            return ConnectionMultiplexer.Connect(config);
        });
        services.AddSingleton<KasaRedis>();
        return services;
    }

    /// <summary>
    /// A lock that only one caller across all API instances holds at a time (SET NX with an expiry). Returns null
    /// when someone else holds it. When Redis is down it returns a lock that guards nothing: the caller carries on,
    /// and whatever it protects must survive running twice (for the bank sync, the unique index does).
    /// </summary>
    public async Task<IAsyncDisposable?> TryLockAsync(string key, TimeSpan expiry)
    {
        var token = Guid.NewGuid().ToString("N");
        try
        {
            return await Db.StringSetAsync(key, token, expiry, When.NotExists) ? new Lock(Db, log, key, token) : null;
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            log.LogWarning("Redis is not reachable; carrying on without the lock {Key}: {Error}", key, ex.Message);
            return new Lock(null, log, key, token);
        }
    }

    private sealed class Lock(IDatabase? db, ILogger log, string key, string token) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (db is null)
            {
                return;
            }

            try
            {
                await db.ScriptEvaluateAsync(ReleaseScript, [key], [token]);
            }
            catch (Exception ex) when (IsUnavailable(ex))
            {
                // It expires on its own.
                log.LogWarning("Redis is not reachable; the lock {Key} will expire by itself: {Error}", key, ex.Message);
            }
        }
    }

    /// <summary>The errors that mean "Redis is not there right now". Callers catch these and carry on.</summary>
    public static bool IsUnavailable(Exception ex) => ex is RedisException or RedisTimeoutException or RedisConnectionException;
}
