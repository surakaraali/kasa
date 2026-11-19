using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Kasa.MockBanks;

public enum ConsentStatus
{
    AwaitingAuthorization,
    Authorized,
    Rejected,
    Revoked,
}

public sealed class Consent
{
    public required string Id { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public ConsentStatus Status { get; set; }
    public string? RedirectUri { get; set; }
    public string? State { get; set; }
    public string? CodeChallenge { get; set; }
}

/// <summary>AcikBank's OAuth 2.0 side: consents, one-time codes with PKCE, short access tokens, rotating refresh tokens.</summary>
public sealed class AcikBankAuth
{
    private sealed record Code(string ConsentId, string RedirectUri, string Challenge, DateTimeOffset ExpiresAt);

    private sealed record AccessToken(string ConsentId, DateTimeOffset ExpiresAt);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Consent> _consents = [];
    private readonly Dictionary<string, Code> _codes = [];
    private readonly Dictionary<string, AccessToken> _access = [];
    private readonly Dictionary<string, string> _refresh = [];

    public static string NewSecret() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static string Challenge(string verifier) => Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public Consent CreateConsent(DateTimeOffset expiresAt)
    {
        var consent = new Consent { Id = $"ac_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}", ExpiresAt = expiresAt };
        lock (_gate)
        {
            _consents[consent.Id] = consent;
        }

        return consent;
    }

    public Consent? Find(string id)
    {
        lock (_gate)
        {
            return _consents.GetValueOrDefault(id);
        }
    }

    /// <summary>The customer's answer. Returns the code to send back, or null when the customer said no.</summary>
    public string? Answer(Consent consent, bool approved, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!approved)
            {
                consent.Status = ConsentStatus.Rejected;
                return null;
            }

            consent.Status = ConsentStatus.Authorized;
            var code = NewSecret();
            _codes[code] = new Code(consent.Id, consent.RedirectUri!, consent.CodeChallenge!, now.AddMinutes(1));
            return code;
        }
    }

    /// <summary>A code works once: it is removed on the first try, right or wrong.</summary>
    public (string? Access, string? Refresh, string? Error) ExchangeCode(string code, string verifier, string redirectUri, DateTimeOffset now, TimeSpan lifetime)
    {
        lock (_gate)
        {
            if (!_codes.Remove(code, out var issued) || issued.ExpiresAt < now)
            {
                return (null, null, "unknown, used or expired code");
            }

            if (issued.RedirectUri != redirectUri || Challenge(verifier) != issued.Challenge)
            {
                return (null, null, "redirect_uri or code_verifier does not match");
            }

            return Issue(issued.ConsentId, now, lifetime);
        }
    }

    /// <summary>Rotation: a refresh token works once; the next one comes with the new access token.</summary>
    public (string? Access, string? Refresh, string? Error) Refresh(string refresh, DateTimeOffset now, TimeSpan lifetime)
    {
        lock (_gate)
        {
            if (!_refresh.Remove(refresh, out var consentId))
            {
                return (null, null, "unknown or used refresh token");
            }

            var consent = _consents[consentId];
            if (consent.Status == ConsentStatus.Revoked)
            {
                return (null, null, "consent_revoked");
            }

            return consent.ExpiresAt <= now ? (null, null, "consent_expired") : Issue(consentId, now, lifetime);
        }
    }

    /// <summary>The consent behind a bearer token, or why there is none.</summary>
    public (Consent? Consent, int Status, string? Error) Authorize(string? header, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (header is null || !header.StartsWith("Bearer ", StringComparison.Ordinal)
                || !_access.TryGetValue(header["Bearer ".Length..], out var token) || token.ExpiresAt <= now)
            {
                return (null, 401, "invalid_token");
            }

            var consent = _consents[token.ConsentId];
            if (consent.Status == ConsentStatus.Revoked)
            {
                return (null, 403, "consent_revoked");
            }

            return consent.ExpiresAt <= now ? (null, 403, "consent_expired") : (consent, 200, null);
        }
    }

    public bool Revoke(string consentId)
    {
        lock (_gate)
        {
            if (!_consents.TryGetValue(consentId, out var consent))
            {
                return false;
            }

            consent.Status = ConsentStatus.Revoked;
            return true;
        }
    }

    /// <summary>Every access token stops working now; refresh tokens keep working.</summary>
    public void ExpireAccessTokens(DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var key in _access.Keys.ToList())
            {
                _access[key] = _access[key] with { ExpiresAt = now };
            }
        }
    }

    private (string?, string?, string?) Issue(string consentId, DateTimeOffset now, TimeSpan lifetime)
    {
        var access = NewSecret();
        var refresh = NewSecret();
        _access[access] = new AccessToken(consentId, now + lifetime);
        _refresh[refresh] = consentId;
        return (access, refresh, null);
    }
}
