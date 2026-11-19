namespace Kasa.Api.Domain;

/// <summary>A current account (cari kart) copied from the company's ERP. Matching decides which one a bank movement belongs to.</summary>
public sealed class CurrentAccount : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }

    /// <summary>The ERP's own code, e.g. 120.01.001. Vouchers are written against it.</summary>
    public required string Code { get; set; }

    public required string Title { get; set; }
    public string? TaxNumber { get; set; }
    public string? Iban { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
}

public enum MatchStatus
{
    Unmatched,
    Matched,

    /// <summary>A person decided this movement does not go to the ERP (a bank fee, for example).</summary>
    Ignored,
}

public enum MatchRule
{
    Iban,
    TaxNumber,
    Manual,
}

public enum ExportStatus
{
    None,

    /// <summary>In the outbox or on its way through RabbitMQ.</summary>
    Pending,
    Exported,
    Failed,
}

/// <summary>Every matching decision, automatic or by a person: who, when, to which current account.</summary>
public sealed class MatchAudit : ITenantEntity
{
    public long Id { get; set; }
    public Guid CompanyId { get; set; }
    public long BankTransactionId { get; set; }
    public BankTransaction? BankTransaction { get; set; }

    /// <summary>Null when the rules decided.</summary>
    public Guid? UserId { get; set; }

    public MatchStatus Status { get; set; }
    public MatchRule? Rule { get; set; }
    public Guid? CurrentAccountId { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>
/// A message waiting to go to RabbitMQ. It is written in the same database transaction as the change it announces,
/// so there is never a match without its export message, or an export message without its match.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public required string Type { get; set; }
    public required string Payload { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}
