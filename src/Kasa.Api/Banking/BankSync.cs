using Kasa.Api.Data;
using Kasa.Api.Domain;
using Kasa.Api.Erp;
using Kasa.Api.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Kasa.Api.Banking;

public enum SyncRunOutcome
{
    Succeeded,

    /// <summary>The bank was down, too slow or sent data we rejected. Nothing half-done is stored; the next run tries again.</summary>
    Failed,

    /// <summary>The bank refused us for good (revoked consent, changed password): the customer has to connect again.</summary>
    ReauthRequired,

    /// <summary>Another sync of the same connection is running right now.</summary>
    AlreadyRunning,

    NotActive,
}

public sealed record SyncResult(Guid ConnectionId, SyncRunOutcome Outcome, int Imported, string? Error);

/// <summary>Encrypts what Kasa keeps to reach a bank: tokens and the SOAP bank's password.</summary>
public sealed class BankSecrets(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("Kasa.BankConnections.v1");

    public string Protect(string value) => _protector.Protect(value);

    public string Unprotect(string? value) =>
        _protector.Unprotect(value ?? throw new InvalidOperationException("A bank secret that should be stored is missing."));
}

/// <summary>
/// Fetches accounts and movements from one bank connection and stores the movements it has not seen. Safe to run
/// any number of times and from several places at once: a Redis lock keeps two runs of one connection apart, and
/// when Redis is down the unique index on (account, bank reference) still stores each movement once.
/// </summary>
public sealed class BankSync(
    AppDbContext db, KasaRedis redis, BankSecrets secrets, AcikBankClient acik, KlasikBankClient klasik, Matching matching, Dashboard.CompanyEvents events,
    TimeProvider clock, ILogger<BankSync> log)
{
    /// <summary>How far back the first sync of a new connection reaches.</summary>
    public const int InitialDays = 30;

    /// <summary>Banks sometimes book a movement a day or two late, so every sync re-reads the last two days.</summary>
    public const int OverlapDays = 2;

    // Longer than any sync takes. If one ever does, a second run may start; the unique index keeps that harmless.
    private static readonly TimeSpan LockExpiry = TimeSpan.FromMinutes(5);

    public async Task<SyncResult> SyncAsync(Guid connectionId, CancellationToken ct)
    {
        await using var held = await redis.TryLockAsync($"lock:sync:{connectionId}", LockExpiry);
        if (held is null)
        {
            return new SyncResult(connectionId, SyncRunOutcome.AlreadyRunning, 0, null);
        }

        var connection = await db.BankConnections.SingleOrDefaultAsync(c => c.Id == connectionId, ct)
            ?? throw new KeyNotFoundException($"Bank connection {connectionId} not found for this company.");
        if (connection.Status != ConnectionStatus.Active)
        {
            return new SyncResult(connectionId, SyncRunOutcome.NotActive, 0, null);
        }

        var outcome = SyncRunOutcome.Succeeded;
        var imported = 0;
        try
        {
            imported = connection.Bank == BankCode.AcikBank
                ? await SyncAcikBankAsync(connection, ct)
                : await SyncKlasikBankAsync(connection, ct);
            connection.LastSyncError = null;
        }
        catch (BankApiException ex)
        {
            // Movements of accounts finished before the error are kept; the failing account stored nothing.
            outcome = ex.Kind == BankErrorKind.Rejected ? SyncRunOutcome.ReauthRequired : SyncRunOutcome.Failed;
            if (outcome == SyncRunOutcome.ReauthRequired)
            {
                connection.Status = ConnectionStatus.ReauthRequired;
            }

            connection.LastSyncError = ex.Message.Length <= BankConnection.ErrorMaxLength ? ex.Message : ex.Message[..BankConnection.ErrorMaxLength];
            log.LogWarning(ex, "Sync of {Bank} connection {ConnectionId} failed ({Kind})", connection.Bank, connection.Id, ex.Kind);
        }

        connection.LastSyncAt = clock.GetUtcNow();
        connection.LastSyncOutcome = outcome == SyncRunOutcome.Succeeded ? SyncOutcome.Succeeded : SyncOutcome.Failed;
        await db.SaveChangesAsync(ct);

        await events.ChangedAsync(connection.CompanyId, "sync");
        if (imported > 0)
        {
            log.LogInformation("Imported {Count} movements from {Bank} for company {CompanyId}", imported, connection.Bank, connection.CompanyId);
        }

        return new SyncResult(connectionId, outcome, imported, connection.LastSyncError);
    }

    private async Task<int> SyncAcikBankAsync(BankConnection connection, CancellationToken ct)
    {
        var infos = await WithAccessTokenAsync(connection, token => acik.GetAccountsAsync(token, Tag(connection, "accounts"), ct), ct);
        var imported = 0;
        foreach (var account in await UpsertAccountsAsync(connection, infos, ct))
        {
            var (from, to) = Window(account);
            var lines = await WithAccessTokenAsync(connection,
                token => acik.GetTransactionsAsync(token, account.ExternalId, from, to, Tag(connection, "transactions"), ct), ct);
            imported += await ImportAsync(account, lines, to, ct);
        }

        return imported;
    }

    private async Task<int> SyncKlasikBankAsync(BankConnection connection, CancellationToken ct)
    {
        var (username, password) = (secrets.Unprotect(connection.UsernameProtected), secrets.Unprotect(connection.PasswordProtected));
        var infos = await klasik.GetAccountsAsync(username, password, Tag(connection, "HesapListesi"), ct);
        var imported = 0;
        foreach (var account in await UpsertAccountsAsync(connection, infos, ct))
        {
            var (from, to) = Window(account);
            var lines = await klasik.GetTransactionsAsync(username, password, account.Iban, from, to, Tag(connection, "HesapHareketleriSorgula"), ct);
            imported += await ImportAsync(account, lines, to, ct);
        }

        return imported;
    }

    /// <summary>Calls the bank with a valid access token; when the bank says the token is no longer valid, refreshes once and retries.</summary>
    private async Task<T> WithAccessTokenAsync<T>(BankConnection connection, Func<string, Task<T>> call, CancellationToken ct)
    {
        try
        {
            return await call(await AccessTokenAsync(connection, forceRefresh: false, ct));
        }
        catch (BankApiException ex) when (ex.Status == StatusCodes.Status401Unauthorized)
        {
            return await call(await AccessTokenAsync(connection, forceRefresh: true, ct));
        }
    }

    private async Task<string> AccessTokenAsync(BankConnection connection, bool forceRefresh, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (!forceRefresh && connection.AccessTokenExpiresAt > now.AddMinutes(1))
        {
            return secrets.Unprotect(connection.AccessTokenProtected);
        }

        var used = connection.RefreshTokenProtected;
        BankTokens tokens;
        try
        {
            tokens = await acik.RefreshAsync(secrets.Unprotect(used), ct);
        }
        catch (BankApiException ex) when (ex.Kind == BankErrorKind.Rejected)
        {
            // Refresh tokens work once. Without the Redis lock another run may have just used ours: take its result.
            var latest = await db.BankConnections.AsNoTracking().Where(c => c.Id == connection.Id)
                .Select(c => new { c.RefreshTokenProtected, c.AccessTokenProtected, c.AccessTokenExpiresAt }).SingleAsync(ct);
            if (latest.RefreshTokenProtected == used || latest.AccessTokenExpiresAt <= now)
            {
                throw;
            }

            (connection.RefreshTokenProtected, connection.AccessTokenProtected, connection.AccessTokenExpiresAt) =
                (latest.RefreshTokenProtected, latest.AccessTokenProtected, latest.AccessTokenExpiresAt);
            return secrets.Unprotect(latest.AccessTokenProtected);
        }

        StoreTokens(connection, tokens, now, secrets);
        // Saved at once: the bank has already retired the old refresh token, so losing this one would lose the connection.
        await db.SaveChangesAsync(ct);
        return tokens.AccessToken;
    }

    public static void StoreTokens(BankConnection connection, BankTokens tokens, DateTimeOffset now, BankSecrets secrets)
    {
        connection.AccessTokenProtected = secrets.Protect(tokens.AccessToken);
        connection.AccessTokenExpiresAt = now.AddSeconds(tokens.ExpiresIn);
        if (tokens.RefreshToken is not null)
        {
            connection.RefreshTokenProtected = secrets.Protect(tokens.RefreshToken);
        }
    }

    private async Task<List<BankAccount>> UpsertAccountsAsync(BankConnection connection, IReadOnlyList<AccountInfo> infos, CancellationToken ct)
    {
        var known = await db.BankAccounts.Where(a => a.BankConnectionId == connection.Id).ToListAsync(ct);
        var now = clock.GetUtcNow();
        var current = new List<BankAccount>();
        foreach (var info in infos)
        {
            var account = known.FirstOrDefault(a => a.ExternalId == info.ExternalId);
            if (account is null)
            {
                account = new BankAccount { CompanyId = connection.CompanyId, BankConnectionId = connection.Id, ExternalId = info.ExternalId, Iban = info.Iban, Name = info.Name, Currency = info.Currency };
                db.BankAccounts.Add(account);
            }

            (account.Iban, account.Name, account.Currency) = (info.Iban, info.Name, info.Currency);
            if (info.Balance is not null)
            {
                (account.Balance, account.BalanceAt) = (info.Balance, now);
            }

            current.Add(account);
        }

        // An account the bank no longer lists keeps its history but is not fetched.
        return current;
    }

    private (DateOnly From, DateOnly To) Window(BankAccount account)
    {
        var today = BankData.TodayInTurkey(clock.GetUtcNow());
        return (account.ImportedThrough?.AddDays(-OverlapDays) ?? today.AddDays(-InitialDays), today);
    }

    /// <summary>Stores the movements this account does not have yet, together with the account's new state, in one save.</summary>
    private async Task<int> ImportAsync(BankAccount account, IReadOnlyList<StatementLine> lines, DateOnly through, CancellationToken ct)
    {
        // A bank may repeat a movement within one answer; the first one counts.
        var unique = lines.DistinctBy(l => l.Reference, StringComparer.Ordinal).ToList();
        account.ImportedThrough = through;
        for (var attempt = 1; ; attempt++)
        {
            var references = unique.Select(l => l.Reference).ToList();
            var known = (await db.BankTransactions
                .Where(t => t.BankAccountId == account.Id && references.Contains(t.BankReference))
                .Select(t => t.BankReference).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
            var now = clock.GetUtcNow();
            var fresh = unique.Where(l => !known.Contains(l.Reference)).Select(l => new BankTransaction
            {
                CompanyId = account.CompanyId,
                Account = account,
                BankReference = l.Reference,
                BookedAt = l.BookedAt,
                Amount = l.Amount,
                Description = l.Description,
                CounterpartyName = l.CounterpartyName,
                CounterpartyIban = l.CounterpartyIban,
                CounterpartyTaxNumber = l.CounterpartyTaxNumber,
                ImportedAt = now,
            }).ToList();
            db.BankTransactions.AddRange(fresh);
            // Matched in the same save: a stored movement and its export message exist together or not at all.
            await matching.AutoMatchAsync(fresh, account.CompanyId, ct);
            try
            {
                await db.SaveChangesAsync(ct);
                return fresh.Count;
            }
            catch (DbUpdateException ex) when (attempt < 3 && IsUniqueViolation(ex))
            {
                // Another run stored some of these between our read and our write; read again and store the rest.
                foreach (var entry in db.ChangeTracker.Entries()
                             .Where(e => e is { State: EntityState.Added, Entity: BankTransaction or MatchAudit or OutboxMessage }).ToList())
                {
                    entry.State = EntityState.Detached;
                }
            }
        }
    }

    public static bool IsUniqueViolation(DbUpdateException ex) => ex.InnerException is SqlException { Number: 2601 or 2627 };

    private static RawResponseTag Tag(BankConnection connection, string operation) =>
        new(connection.CompanyId, connection.Id, connection.Bank, operation);
}
