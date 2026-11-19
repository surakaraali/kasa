using System.Buffers.Text;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Kasa.Api.Domain;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Kasa.Api.Auth;

public static class KasaClaims
{
    public const string UserId = JwtRegisteredClaimNames.Sub;
    public const string CompanyId = "company_id";
    public const string Role = "role";
    public const string Name = "name";
    public const string Email = JwtRegisteredClaimNames.Email;
}

public static class Policies
{
    public const string Admin = "Admin";

    /// <summary>Matching, syncing and exporting: admins and accountants.</summary>
    public const string CanEdit = "CanEdit";
}

public sealed class JwtOptions
{
    [Required]
    public string Issuer { get; set; } = "";

    [Required]
    public string Audience { get; set; } = "";

    /// <summary>HMAC-SHA256 needs at least 256 bits, so at least 32 characters.</summary>
    [Required, MinLength(32)]
    public string SigningKey { get; set; } = "";

    [Range(1, 60)]
    public int AccessTokenMinutes { get; set; } = 15;

    [Range(1, 90)]
    public int RefreshTokenDays { get; set; } = 14;

    public SymmetricSecurityKey Key() => new(Encoding.UTF8.GetBytes(SigningKey));
}

/// <param name="ExpiresIn">Seconds.</param>
public sealed record AccessTokenResult(string AccessToken, int ExpiresIn);

public sealed class TokenService(IOptions<JwtOptions> options, TimeProvider clock)
{
    public AccessTokenResult CreateAccessToken(AppUser user)
    {
        var jwt = options.Value;
        var now = clock.GetUtcNow();
        var lifetime = TimeSpan.FromMinutes(jwt.AccessTokenMinutes);
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            Subject = new ClaimsIdentity(
            [
                new Claim(KasaClaims.UserId, user.Id.ToString()),
                new Claim(KasaClaims.CompanyId, user.CompanyId.ToString()),
                new Claim(KasaClaims.Role, user.Role.ToString()),
                new Claim(KasaClaims.Name, user.FullName),
                new Claim(KasaClaims.Email, user.Email ?? ""),
            ]),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = (now + lifetime).UtcDateTime,
            SigningCredentials = new SigningCredentials(jwt.Key(), SecurityAlgorithms.HmacSha256),
        });
        return new AccessTokenResult(token, (int)lifetime.TotalSeconds);
    }

    /// <summary>32 random bytes: a refresh token, an invitation token. Never stored, only its hash.</summary>
    public static string NewSecret() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// SHA-256 is enough here, unlike for passwords: the input is 256 random bits, so there is nothing to guess.
    /// A slow hash only matters for low-entropy secrets that people choose.
    /// </summary>
    public static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}
