using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Kasa.Api.Auth;
using Kasa.Api.Banking;
using Kasa.Api.Common;
using Kasa.Api.Data;
using Kasa.Api.Domain;
using Kasa.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kasa.Api.Dashboard;

/// <summary>
/// Live updates to the web app. Each connection joins its company's group, taken from the token, so a company only
/// ever hears about itself. The token comes in the query string here (browsers cannot set headers on WebSockets).
/// shortcut: groups live in this process; with several API instances add the Redis backplane (AddStackExchangeRedis).
/// </summary>
[Authorize]
public sealed class KasaHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        if (Guid.TryParse(Context.User?.FindFirstValue(KasaClaims.CompanyId), out var companyId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, Group(companyId));
        }

        await base.OnConnectedAsync();
    }

    public static string Group(Guid companyId) => $"company:{companyId}";
}

/// <summary>
/// "Something changed for this company": the dashboard cache is dropped (by moving its version on) and the web app
/// is told to refresh. Both are best effort: a missed message only means the screen refreshes a little later.
/// </summary>
public sealed class CompanyEvents(IHubContext<KasaHub> hub, KasaRedis redis, ILogger<CompanyEvents> log)
{
    public async Task ChangedAsync(Guid companyId, string what)
    {
        try
        {
            var key = DashboardController.VersionKey(companyId);
            await redis.Db.StringIncrementAsync(key);
            await redis.Db.KeyExpireAsync(key, TimeSpan.FromDays(7));
        }
        catch (Exception ex) when (KasaRedis.IsUnavailable(ex))
        {
            // The cache entry expires within minutes anyway.
            log.LogWarning("Redis is not reachable; the dashboard cache of {CompanyId} was not invalidated", companyId);
        }

        await hub.Clients.Group(KasaHub.Group(companyId)).SendAsync("changed", what);
    }
}

public sealed record DailyFlow(DateOnly Date, decimal In, decimal Out);

public sealed record DashboardDto(
    decimal TotalBalance, IReadOnlyList<AccountDto> Accounts, decimal TodayIn, decimal TodayOut, IReadOnlyList<DailyFlow> Daily,
    int Matched, int Unmatched, int Ignored, int Exported, int ExportPending, int ExportFailed, DateTimeOffset? LastSyncAt);

[ApiController]
[Route("api/dashboard")]
public sealed class DashboardController(
    AppDbContext db, TenantContext tenant, KasaRedis redis, IOptions<JsonOptions> json, TimeProvider clock, ILogger<DashboardController> log)
    : ControllerBase
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    public static string VersionKey(Guid companyId) => $"dashboard-version:{companyId}";

    /// <summary>
    /// Cached in Redis per company. Any change moves the company's version on, so a cached answer is never stale;
    /// the key includes the version and old ones simply expire. X-Cache says hit or miss, for measuring.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<DashboardDto>> Get([FromQuery] int days = 30, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 7, 90);
        var companyId = tenant.CompanyId!.Value;
        string? cacheKey = null;
        try
        {
            var version = (long?)await redis.Db.StringGetAsync(VersionKey(companyId)) ?? 0;
            cacheKey = $"dashboard:{companyId}:v{version}:{days}";
            var cached = await redis.Db.StringGetAsync(cacheKey);
            if (cached.HasValue)
            {
                Response.Headers["X-Cache"] = "hit";
                return Content(cached.ToString(), "application/json");
            }
        }
        catch (Exception ex) when (KasaRedis.IsUnavailable(ex))
        {
            log.LogWarning("Redis is not reachable; the dashboard is computed without the cache");
            cacheKey = null;
        }

        var watch = Stopwatch.StartNew();
        var dashboard = await ComputeAsync(days, ct);
        Response.Headers["X-Cache"] = "miss";
        Response.Headers["X-Compute-Ms"] = watch.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (cacheKey is not null)
        {
            try
            {
                await redis.Db.StringSetAsync(cacheKey, JsonSerializer.Serialize(dashboard, json.Value.JsonSerializerOptions), CacheFor);
            }
            catch (Exception ex) when (KasaRedis.IsUnavailable(ex))
            {
                // Served without caching.
            }
        }

        return dashboard;
    }

    private async Task<DashboardDto> ComputeAsync(int days, CancellationToken ct)
    {
        var accounts = await db.BankAccounts.AsNoTracking().Where(a => a.Connection!.Status != ConnectionStatus.Removed).OrderBy(a => a.Name)
            .Select(a => new AccountDto(a.Id, a.BankConnectionId, a.Connection!.Bank, a.Iban, a.Name, a.Currency, a.Balance, a.BalanceAt)).ToListAsync(ct);

        var today = BankData.TodayInTurkey(clock.GetUtcNow());
        var first = today.AddDays(-(days - 1));
        var since = new DateTimeOffset(first.ToDateTime(TimeOnly.MinValue), BankData.Turkey.GetUtcOffset(first.ToDateTime(TimeOnly.MinValue)));
        // Grouped by the Turkish calendar day in SQL Server, whatever offset the bank sent.
        var flows = await db.BankTransactions.Where(t => t.BookedAt >= since)
            .GroupBy(t => EF.Functions.AtTimeZone(t.BookedAt, "Turkey Standard Time").Date)
            .Select(g => new { Day = g.Key, In = g.Sum(t => t.Amount > 0 ? t.Amount : 0), Out = g.Sum(t => t.Amount < 0 ? -t.Amount : 0) })
            .ToListAsync(ct);
        var byDay = flows.ToDictionary(f => DateOnly.FromDateTime(f.Day));
        var daily = Enumerable.Range(0, days).Select(i => first.AddDays(i))
            .Select(d => byDay.TryGetValue(d, out var f) ? new DailyFlow(d, f.In, f.Out) : new DailyFlow(d, 0, 0)).ToList();

        var match = await db.BankTransactions.GroupBy(t => t.MatchStatus).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var export = await db.BankTransactions.GroupBy(t => t.ExportStatus).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var lastSync = await db.BankConnections.Where(c => c.Status != ConnectionStatus.Removed).MaxAsync(c => c.LastSyncAt, ct);

        return new DashboardDto(accounts.Sum(a => a.Balance ?? 0), accounts, daily[^1].In, daily[^1].Out, daily,
            match.GetValueOrDefault(MatchStatus.Matched), match.GetValueOrDefault(MatchStatus.Unmatched), match.GetValueOrDefault(MatchStatus.Ignored),
            export.GetValueOrDefault(ExportStatus.Exported), export.GetValueOrDefault(ExportStatus.Pending), export.GetValueOrDefault(ExportStatus.Failed), lastSync);
    }
}
