using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Kasa.Api.Auth;
using Kasa.Api.Common;
using Kasa.Api.Data;
using Kasa.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kasa.Api.Banking;

public enum CallbackResult
{
    Connected,
    Rejected,

    /// <summary>Unknown or used state, a browser other than the one that started, or a failed code exchange.</summary>
    Failed,
}

/// <summary>Connecting a company to a bank and disconnecting it.</summary>
public sealed class BankConnections(
    AppDbContext db, TenantContext tenant, BankSecrets secrets, AcikBankClient acik, KlasikBankClient klasik, BankSync sync, Dashboard.CompanyEvents events,
    TimeProvider clock, ILogger<BankConnections> log)
{
    /// <summary>How long the customer's consent lasts at the bank. Open banking consents are limited to 180 days.</summary>
    public const int ConsentDays = 90;

    /// <summary>
    /// Step 1 of open banking: a consent at the bank, then the address of the bank's approval page. The state ties
    /// the bank's answer to this connection; PKCE ties the code to this server, so a stolen code is useless.
    /// A company has one connection per bank: connecting again (after a revoked consent) reuses it and keeps its accounts.
    /// </summary>
    public async Task<(BankConnection Connection, string State)> StartAcikBankAsync(CancellationToken ct)
    {
        var connection = await CurrentAsync(BankCode.AcikBank, ct);
        if (connection is { Status: ConnectionStatus.Active })
        {
            throw new ConnectionConflictException("AcikBank zaten bağlı.");
        }

        var now = clock.GetUtcNow();
        var expiresAt = now.AddDays(ConsentDays);
        var consentId = await acik.CreateConsentAsync(expiresAt, ct);
        var state = TokenService.NewSecret();
        var verifier = TokenService.NewSecret();
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        if (connection is null)
        {
            connection = new BankConnection { Bank = BankCode.AcikBank, CreatedAt = now };
            db.BankConnections.Add(connection);
        }

        connection.Status = ConnectionStatus.Pending;
        connection.ConsentId = consentId;
        connection.ConsentExpiresAt = expiresAt;
        connection.StateHash = TokenService.Hash(state);
        connection.CodeVerifierProtected = secrets.Protect(verifier);
        connection.AuthorizationUrl = acik.AuthorizationUrl(consentId, state, challenge);
        await SaveAsync(ct);
        return (connection, state);
    }

    /// <summary>
    /// Step 2: the customer's browser comes back from the bank. Nobody is signed in on this request (it is a redirect
    /// from the bank), so the company comes from the connection the state points to. <paramref name="browserState"/>
    /// is the cookie set when this browser started: without it, someone could start a connection for their own
    /// company and trick a victim into approving it, pulling the victim's bank data into the attacker's company.
    /// </summary>
    public async Task<CallbackResult> CompleteAcikBankAsync(string? state, string? code, string? error, string? browserState, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(state) || browserState is null
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(browserState)))
        {
            return CallbackResult.Failed;
        }

        var stateHash = TokenService.Hash(state);
        var connection = await db.BankConnections.IgnoreQueryFilters()
            .SingleOrDefaultAsync(c => c.StateHash == stateHash && c.Status == ConnectionStatus.Pending, ct);
        if (connection is null)
        {
            return CallbackResult.Failed;
        }

        tenant.Use(connection.CompanyId);
        var verifier = secrets.Unprotect(connection.CodeVerifierProtected);
        // The state works once, whatever happens next.
        connection.StateHash = null;
        connection.CodeVerifierProtected = null;
        connection.AuthorizationUrl = null;

        if (error is not null || string.IsNullOrEmpty(code))
        {
            connection.Status = ConnectionStatus.Rejected;
            await db.SaveChangesAsync(ct);
            return CallbackResult.Rejected;
        }

        try
        {
            BankSync.StoreTokens(connection, await acik.ExchangeCodeAsync(code, verifier, ct), clock.GetUtcNow(), secrets);
        }
        catch (BankApiException ex)
        {
            log.LogWarning(ex, "AcikBank code exchange failed for connection {ConnectionId}", connection.Id);
            connection.Status = ConnectionStatus.Rejected;
            await db.SaveChangesAsync(ct);
            return CallbackResult.Failed;
        }

        connection.Status = ConnectionStatus.Active;
        await db.SaveChangesAsync(ct);
        // The first sync right away, so the customer sees their movements when they land back in Kasa.
        await sync.SyncAsync(connection.Id, ct);
        return CallbackResult.Connected;
    }

    /// <summary>
    /// The SOAP bank has no consent flow: Kasa signs in with the company's internet banking user. The password is
    /// checked with the bank before anything is stored, then kept encrypted.
    /// </summary>
    public async Task<BankConnection> ConnectKlasikBankAsync(string username, string password, CancellationToken ct)
    {
        // Wrong password → BankApiException(Rejected); the caller answers 400 and nothing is stored.
        await klasik.GetAccountsAsync(username, password, tag: null, ct);

        var connection = await CurrentAsync(BankCode.KlasikBank, ct);
        if (connection is null)
        {
            connection = new BankConnection { Bank = BankCode.KlasikBank, CreatedAt = clock.GetUtcNow() };
            db.BankConnections.Add(connection);
        }

        connection.Status = ConnectionStatus.Active;
        connection.UsernameProtected = secrets.Protect(username);
        connection.PasswordProtected = secrets.Protect(password);
        await SaveAsync(ct);
        await sync.SyncAsync(connection.Id, ct);
        return connection;
    }

    /// <summary>Stops using the connection and forgets its secrets. Accounts and movements stay: they are the company's records.</summary>
    public async Task<bool> RemoveAsync(Guid id, CancellationToken ct)
    {
        var connection = await db.BankConnections.SingleOrDefaultAsync(c => c.Id == id && c.Status != ConnectionStatus.Removed, ct);
        if (connection is null)
        {
            return false;
        }

        if (connection is { Bank: BankCode.AcikBank, ConsentId: not null })
        {
            try
            {
                await acik.RevokeConsentAsync(connection.ConsentId, ct);
            }
            catch (BankApiException ex)
            {
                // Kasa forgets the tokens below, so the consent is unusable anyway; it ends at the bank when it expires.
                log.LogWarning(ex, "Could not revoke AcikBank consent {ConsentId}", connection.ConsentId);
            }
        }

        connection.Status = ConnectionStatus.Removed;
        (connection.AccessTokenProtected, connection.RefreshTokenProtected, connection.AccessTokenExpiresAt) = (null, null, null);
        (connection.UsernameProtected, connection.PasswordProtected) = (null, null);
        (connection.StateHash, connection.CodeVerifierProtected, connection.AuthorizationUrl) = (null, null, null);
        await db.SaveChangesAsync(ct);
        await events.ChangedAsync(connection.CompanyId, "connections");
        return true;
    }

    // The newest connection for the bank, a removed one too. Connecting again then reuses its accounts and what they
    // already imported; new account rows would bring the last 30 days in a second time, each with a second voucher.
    private Task<BankConnection?> CurrentAsync(BankCode bank, CancellationToken ct) =>
        db.BankConnections.Where(c => c.Bank == bank).OrderByDescending(c => c.CreatedAt).FirstOrDefaultAsync(ct);

    /// <summary>Two "connect" clicks at once both pass the check above; the filtered unique index lets only one through.</summary>
    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (BankSync.IsUniqueViolation(ex))
        {
            throw new ConnectionConflictException("Bu banka için başka bir bağlantı işlemi az önce başladı. Sayfayı yenileyin.");
        }
    }
}

public sealed class ConnectionConflictException(string message) : Exception(message);
