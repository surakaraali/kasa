using Kasa.Api.Common;
using Kasa.Api.Data;
using Kasa.Api.Domain;
using Kasa.Api.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Quartz;

namespace Kasa.Api.Banking;

public static class BankingSetup
{
    public static IServiceCollection AddKasaBanking(this IServiceCollection services, IConfiguration config)
    {
        // shortcut: the key ring sits unencrypted in SQL Server next to the data it protects; in production wrap it
        // with ProtectKeysWithCertificate or a key vault, so a database dump alone does not reveal the bank secrets.
        services.AddDataProtection().SetApplicationName("kasa").PersistKeysToDbContext<AppDbContext>();
        services.AddSingleton<BankSecrets>();
        RawResponseArchive.Add(services);

        services.AddOptions<AcikBankOptions>().BindConfiguration("Banks:AcikBank").ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<KlasikBankOptions>().BindConfiguration("Banks:KlasikBank").ValidateDataAnnotations().ValidateOnStart();
        // A bank that takes longer than this is treated as down for this run; the next run tries again.
        var timeout = config.GetValue("Banks:Timeout", TimeSpan.FromSeconds(10));
        services.AddHttpClient<AcikBankClient>((sp, http) =>
        {
            http.BaseAddress = new Uri(sp.GetRequiredService<IOptions<AcikBankOptions>>().Value.BaseUrl.TrimEnd('/') + "/");
            http.Timeout = timeout;
        }).AddHttpMessageHandler<RawResponseHandler>();
        services.AddHttpClient<KlasikBankClient>((sp, http) =>
        {
            http.BaseAddress = new Uri(sp.GetRequiredService<IOptions<KlasikBankOptions>>().Value.BaseUrl.TrimEnd('/') + "/");
            http.Timeout = timeout;
        }).AddHttpMessageHandler<RawResponseHandler>();

        services.AddScoped<BankSync>();
        services.AddScoped<BankConnections>();

        // Cron in Turkish time, e.g. "0 0/15 * * * ?" for every 15 minutes. Empty turns the job off (the tests do).
        var schedule = config["BankSync:Schedule"];
        services.AddQuartz(q =>
        {
            if (!string.IsNullOrWhiteSpace(schedule))
            {
                q.ScheduleJob<SyncAllBanksJob>(t => t.WithIdentity("sync-all-banks").WithCronSchedule(schedule, c => c.InTimeZone(BankData.Turkey)));
            }
        });
        services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);
        return services;
    }
}

/// <summary>
/// Syncs every active connection of every company. In memory: with several API instances each runs it, and the
/// per-connection Redis lock makes the second run of a connection step aside.
/// </summary>
[DisallowConcurrentExecution]
public sealed class SyncAllBanksJob(IServiceScopeFactory scopes, ILogger<SyncAllBanksJob> log) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken ct)
    {
        List<(Guid Id, Guid CompanyId)> active;
        await using (var scope = scopes.CreateAsyncScope())
        {
            active = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().BankConnections.IgnoreQueryFilters()
                .Where(c => c.Status == ConnectionStatus.Active).Select(c => new { c.Id, c.CompanyId }).ToListAsync(ct))
                .Select(c => (c.Id, c.CompanyId)).ToList();
        }

        foreach (var (id, companyId) in active)
        {
            // One scope per connection: its own DbContext and the company it works for.
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().Use(companyId);
            try
            {
                await scope.ServiceProvider.GetRequiredService<BankSync>().SyncAsync(id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One broken connection must not stop the others.
                log.LogError(ex, "Scheduled sync of connection {ConnectionId} failed", id);
            }
        }
    }
}
