namespace Kasa.Contracts;

/// <summary>
/// Kasa.Api → Kasa.ErpExporter: write this bank movement to the ERP as a voucher. <see cref="ExportId"/> is also the
/// ERP's Idempotency-Key, so the same message delivered twice makes one voucher.
/// </summary>
/// <param name="Amount">Signed: positive is money in (a collection voucher), negative money out (a payment voucher).</param>
public sealed record ExportVoucher(
    Guid ExportId, Guid CompanyId, string ErpFirmCode, string CurrentAccountCode,
    string BankIban, DateOnly Date, decimal Amount, string Description, string BankReference);

public enum ExportOutcome
{
    Exported,

    /// <summary>The ERP refused it, or could not be reached after every retry. The message is in the dead-letter queue.</summary>
    Failed,
}

/// <summary>Kasa.ErpExporter → Kasa.Api: how an <see cref="ExportVoucher"/> ended.</summary>
public sealed record ExportResult(Guid ExportId, Guid CompanyId, ExportOutcome Outcome, string? VoucherNo, string? Error);
