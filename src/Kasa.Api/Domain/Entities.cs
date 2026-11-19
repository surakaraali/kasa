using Microsoft.AspNetCore.Identity;

namespace Kasa.Api.Domain;

/// <summary>Data that belongs to one company. A query filter hides other companies' rows from every query.</summary>
public interface ITenantEntity
{
    Guid CompanyId { get; set; }
}

public sealed class Company
{
    public Guid Id { get; set; }
    public required string Name { get; set; }

    /// <summary>VKN, 10 digits. Optional at sign-up.</summary>
    public string? TaxNumber { get; set; }

    /// <summary>The firm number in the ERP, set when the company connects its ERP.</summary>
    public string? ErpFirmCode { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public const int NameMaxLength = 200;
}

public enum UserRole
{
    /// <summary>Manages users, banks and the ERP connection. Can do everything an accountant can.</summary>
    Admin,

    /// <summary>Matches movements, starts syncs and ERP exports.</summary>
    Accountant,

    /// <summary>Reads only.</summary>
    Viewer,
}

/// <summary>
/// A user belongs to exactly one company. Identity stores the password hash, lockout and the unique e-mail.
/// No query filter here: signing in has to find a user before anyone knows the company.
/// </summary>
public sealed class AppUser : IdentityUser<Guid>
{
    public Guid CompanyId { get; set; }
    public Company? Company { get; set; }
    public required string FullName { get; set; }
    public UserRole Role { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The profile photo, a small square the web app has already cut and shrunk. Null when there is none.</summary>
    public byte[]? Avatar { get; set; }

    public string? AvatarType { get; set; }

    /// <summary>Goes up with every new photo, so the photo's address changes and browsers never show an old one.</summary>
    public int AvatarVersion { get; set; }

    public const int FullNameMaxLength = 200;
    public const int AvatarMaxBytes = 512 * 1024;
}

/// <summary>
/// One refresh token. Only its SHA-256 is stored. A token works once: using it issues the next one in the same
/// family. If a used token comes back, someone holds a copy, so the whole family is revoked.
/// </summary>
public sealed class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public AppUser? User { get; set; }

    /// <summary>Every token of one sign-in shares this id; revoking a family signs that session out.</summary>
    public Guid FamilyId { get; set; }

    public required string TokenHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>An invitation to join a company with a role. The link carries the token; only its hash is stored.</summary>
public sealed class Invitation : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public required string Email { get; set; }
    public UserRole Role { get; set; }
    public required string TokenHash { get; set; }
    public Guid InvitedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
}
