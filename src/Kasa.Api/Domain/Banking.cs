namespace Kasa.Api.Domain;

public enum BankCode
{
    /// <summary>Open banking over REST: the customer gives consent at the bank.</summary>
    AcikBank,

    /// <summary>An older bank over SOAP: Kasa signs in with the company's internet banking user.</summary>
    KlasikBank,
}

public enum ConnectionStatus
{
    /// <summary>Waiting for the customer to approve at the bank.</summary>
    Pending,
    Active,

    /// <summary>The consent was revoked or has expired: the customer has to connect again.</summary>
    ReauthRequired,
    Rejected,
    Removed,
}

public enum SyncOutcome
{
    Succeeded,
    Failed,
}

/// <summary>
/// One company's link to one bank. Tokens and passwords are encrypted with ASP.NET Core Data Protection before they
/// reach the database; someone who reads the table gets nothing usable.
/// </summary>
public sealed class BankConnection : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public BankCode Bank { get; set; }
    public ConnectionStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    // Open banking: the consent at the bank and its tokens.
    public string? ConsentId { get; set; }
    public DateTimeOffset? ConsentExpiresAt { get; set; }
    public string? StateHash { get; set; }
    public string? CodeVerifierProtected { get; set; }
    public string? AuthorizationUrl { get; set; }
    public string? AccessTokenProtected { get; set; }
    public DateTimeOffset? AccessTokenExpiresAt { get; set; }
    public string? RefreshTokenProtected { get; set; }

    // SOAP bank: the company's internet banking user.
    public string? UsernameProtected { get; set; }
    public string? PasswordProtected { get; set; }

    public DateTimeOffset? LastSyncAt { get; set; }
    public SyncOutcome? LastSyncOutcome { get; set; }
    public string? LastSyncError { get; set; }

    public List<BankAccount> Accounts { get; set; } = [];

    public const int ErrorMaxLength = 500;
}

public sealed class BankAccount : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BankConnectionId { get; set; }
    public BankConnection? Connection { get; set; }

    /// <summary>The bank's own id for the account.</summary>
    public required string ExternalId { get; set; }

    public required string Iban { get; set; }
    public required string Name { get; set; }
    public required string Currency { get; set; }
    public decimal? Balance { get; set; }
    public DateTimeOffset? BalanceAt { get; set; }

    /// <summary>The last day fetched completely. The next sync starts two days before it, for late bookings.</summary>
    public DateOnly? ImportedThrough { get; set; }
}

/// <summary>A movement on a bank account. The bank's reference is unique per account, which is what stops duplicates.</summary>
public sealed class BankTransaction : ITenantEntity
{
    public long Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BankAccountId { get; set; }
    public BankAccount? Account { get; set; }

    public required string BankReference { get; set; }
    public DateTimeOffset BookedAt { get; set; }

    /// <summary>Signed: positive is money coming in.</summary>
    public decimal Amount { get; set; }

    public required string Description { get; set; }
    public string? CounterpartyName { get; set; }
    public string? CounterpartyIban { get; set; }
    public string? CounterpartyTaxNumber { get; set; }
    public DateTimeOffset ImportedAt { get; set; }

    public MatchStatus MatchStatus { get; set; }
    public MatchRule? MatchRule { get; set; }
    public Guid? CurrentAccountId { get; set; }
    public CurrentAccount? CurrentAccount { get; set; }
    public DateTimeOffset? MatchedAt { get; set; }

    public ExportStatus ExportStatus { get; set; }

    /// <summary>The current export's id, and the ERP's Idempotency-Key. A result for an older id is ignored.</summary>
    public Guid? ExportId { get; set; }

    public string? ErpVoucherNo { get; set; }
    public string? ExportError { get; set; }
    public DateTimeOffset? ExportedAt { get; set; }

    /// <summary>SQL Server rowversion: two people changing the same movement at once cannot both win.</summary>
    public byte[] RowVersion { get; set; } = [];

    public const int BankReferenceMaxLength = 64;
    public const int DescriptionMaxLength = 500;
    public const int CounterpartyNameMaxLength = 200;
}
