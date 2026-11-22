using System.ComponentModel.DataAnnotations;
using Kasa.Api.Infrastructure;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Kasa.Api.Auth;

public sealed class LoginRateLimitOptions
{
    [Range(1, 100_000)]
    public int AttemptsPerMinute { get; set; } = 10;
}

/// <summary>
/// At most N sign-in attempts per minute from one IP address, counted in Redis so the limit holds across servers.
/// If Redis is down the limit is skipped, not the sign-in: Identity's per-account lockout still stops password
/// guessing, and a login page that fails whenever the cache fails would be worse.
/// </summary>
public sealed class LoginRateLimiter(KasaRedis redis, IOptions<LoginRateLimitOptions> options, ILogger<LoginRateLimiter> logger)
{
    // INCR and the first PEXPIRE in one step; without the script a crash between them would leave a key that never expires.
    private const string Script = """
        local count = redis.call('INCR', KEYS[1])
        if count == 1 then redis.call('PEXPIRE', KEYS[1], ARGV[1]) end
        return { count, redis.call('PTTL', KEYS[1]) }
        """;

    /// <returns>Null when allowed; otherwise how long to wait.</returns>
    public Task<TimeSpan?> CheckAsync(string clientKey) => CheckAsync($"login:{clientKey}", options.Value.AttemptsPerMinute, TimeSpan.FromMinutes(1));

    /// <summary>The same count for any key: at most <paramref name="limit"/> per <paramref name="window"/>, from the first use.</summary>
    public async Task<TimeSpan?> CheckAsync(string key, int limit, TimeSpan window)
    {
        try
        {
            var result = (RedisResult[])(await redis.Db.ScriptEvaluateAsync(Script, [$"ratelimit:{key}"], [(long)window.TotalMilliseconds]))!;
            var count = (long)result[0];
            var ttl = (long)result[1];
            return count > limit ? TimeSpan.FromMilliseconds(Math.Max(ttl, 1000)) : null;
        }
        catch (Exception ex) when (KasaRedis.IsUnavailable(ex))
        {
            logger.LogWarning("Redis unavailable, rate limit {Key} skipped: {Reason}", key, ex.Message);
            return null;
        }
    }
}
