using Kasa.Api.Data;
using Kasa.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kasa.Api.Auth;

public enum RefreshOutcome
{
    Refreshed,
    Invalid,

    /// <summary>A token that was already used came back: the whole sign-in was revoked.</summary>
    ReuseDetected,
}

/// <summary>Issues, rotates and revokes refresh tokens.</summary>
public sealed class RefreshTokens(AppDbContext db, IOptions<JwtOptions> options, TimeProvider clock, ILogger<RefreshTokens> logger)
{
    /// <summary>Starts a new family for a new sign-in, or continues one when rotating.</summary>
    public async Task<string> IssueAsync(Guid userId, Guid? familyId, CancellationToken ct)
    {
        var secret = TokenService.NewSecret();
        var now = clock.GetUtcNow();
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            FamilyId = familyId ?? Guid.NewGuid(),
            TokenHash = TokenService.Hash(secret),
            CreatedAt = now,
            ExpiresAt = now.AddDays(options.Value.RefreshTokenDays),
        });
        await db.SaveChangesAsync(ct);
        return secret;
    }

    public async Task<(RefreshOutcome Outcome, AppUser? User, string? NewToken)> RotateAsync(string? secret, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return (RefreshOutcome.Invalid, null, null);
        }

        var hash = TokenService.Hash(secret);
        var token = await db.RefreshTokens.Include(t => t.User).SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        var now = clock.GetUtcNow();
        if (token?.User is null || token.RevokedAt is not null || token.ExpiresAt <= now)
        {
            return (RefreshOutcome.Invalid, null, null);
        }

        // Claimed in one UPDATE, so of two requests with the same token only one can win. The loser is treated as
        // reuse: the web client sends one refresh at a time, so a second use means a copy of the token exists.
        var claimed = await db.RefreshTokens
            .Where(t => t.Id == token.Id && t.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);
        if (claimed == 0)
        {
            await RevokeFamilyAsync(token.FamilyId, ct);
            logger.LogWarning("Refresh token reuse for user {UserId}; family {FamilyId} revoked", token.UserId, token.FamilyId);
            return (RefreshOutcome.ReuseDetected, null, null);
        }

        var next = await IssueAsync(token.UserId, token.FamilyId, ct);
        return (RefreshOutcome.Refreshed, token.User, next);
    }

    /// <summary>Signs one session out: every token of the family stops working.</summary>
    public async Task RevokeAsync(string? secret, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return;
        }

        var hash = TokenService.Hash(secret);
        var familyId = await db.RefreshTokens.Where(t => t.TokenHash == hash).Select(t => (Guid?)t.FamilyId).SingleOrDefaultAsync(ct);
        if (familyId is { } id)
        {
            await RevokeFamilyAsync(id, ct);
        }
    }

    private Task<int> RevokeFamilyAsync(Guid familyId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }
}

/// <summary>
/// The refresh token travels only in an httpOnly cookie: page scripts cannot read it, so a script injected into the
/// page cannot steal it. SameSite=Strict keeps other sites from sending it, and the path limits it to /api/auth.
/// </summary>
public static class RefreshCookie
{
    public const string Name = "kasa_refresh";

    public static void Set(HttpContext http, string token, int days) =>
        http.Response.Cookies.Append(Name, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/api/auth",
            MaxAge = TimeSpan.FromDays(days),
        });

    public static string? Read(HttpContext http) => http.Request.Cookies[Name];

    public static void Clear(HttpContext http) =>
        http.Response.Cookies.Delete(Name, new CookieOptions { Path = "/api/auth", Secure = http.Request.IsHttps, SameSite = SameSiteMode.Strict });
}
