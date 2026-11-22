using System.Globalization;
using System.Text.Json;
using Kasa.Api.Banking;
using Kasa.Api.Data;
using Kasa.Api.Domain;
using Kasa.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Kasa.Api.Erp;

public enum HintKind
{
    /// <summary>No name, IBAN or tax number: usually a bank's own fee.</summary>
    NoCounterparty,

    /// <summary>One current account fits; it arrived before the rules could run on it.</summary>
    One,

    /// <summary>The IBAN and the tax number point at different current accounts, or one of them at several.</summary>
    Conflict,

    /// <summary>No identifier fits, but one current account has exactly the sender's name.</summary>
    SameName,

    /// <summary>Nothing fits: a new customer, or someone without a current account.</summary>
    NoAccount,
}

/// <param name="By">Iban, TaxNumber or Name: what made this current account a candidate.</param>
public sealed record HintCandidate(Guid Id, string Code, string Title, string By);

public sealed record MatchHint(long TransactionId, HintKind Kind, Guid? SuggestedId, IReadOnlyList<HintCandidate> Candidates);

/// <summary>
/// Decides which current account a bank movement belongs to, and queues the voucher for the ERP when it does.
/// Nothing here saves: the caller saves once, so the match, its audit row and its outbox message are one transaction.
/// </summary>
public sealed class Matching(AppDbContext db, TimeProvider clock)
{
    /// <summary>
    /// A rule decides only when exactly one current account fits. In any doubt the movement stays unmatched for a
    /// person: money booked to the wrong customer is worse than money not booked yet.
    /// </summary>
    public static (CurrentAccount? Account, MatchRule? Rule) Decide(string? iban, string? taxNumber, IReadOnlyCollection<CurrentAccount> candidates)
    {
        var byIban = OnlyOne(candidates.Where(c => iban is not null && c.Iban == iban));
        var byTaxNumber = OnlyOne(candidates.Where(c => taxNumber is not null && c.TaxNumber == taxNumber));

        // The two hints point at different current accounts, so one of them is wrong.
        if (byIban is not null && byTaxNumber is not null && byIban.Id != byTaxNumber.Id)
        {
            return (null, null);
        }

        if (byIban is not null)
        {
            return (byIban, MatchRule.Iban);
        }

        return byTaxNumber is not null ? (byTaxNumber, MatchRule.TaxNumber) : (null, null);
    }

    /// <summary>
    /// Why the rules left a movement to a person, with the same comparisons as <see cref="Decide"/>, and the current
    /// account a person would most likely pick. Only a suggestion: the person still chooses.
    /// </summary>
    public static MatchHint Hint(long transactionId, string? name, string? iban, string? taxNumber, IReadOnlyCollection<CurrentAccount> accounts)
    {
        if (string.IsNullOrWhiteSpace(name) && iban is null && taxNumber is null)
        {
            return new(transactionId, HintKind.NoCounterparty, null, []);
        }

        List<HintCandidate> found =
        [
            .. accounts.Where(c => iban is not null && c.Iban == iban).Select(c => Candidate(c, "Iban")),
            .. accounts.Where(c => taxNumber is not null && c.TaxNumber == taxNumber).Select(c => Candidate(c, "TaxNumber")),
        ];
        switch (found.Select(c => c.Id).Distinct().Count())
        {
            case 1:
                return new(transactionId, HintKind.One, found[0].Id, found);
            case > 1:
                return new(transactionId, HintKind.Conflict, null, found);
        }

        // A name is weak evidence (two firms can share one), so it never matches on its own; it only pre-selects.
        var sameName = string.IsNullOrWhiteSpace(name) ? [] : accounts.Where(c => SameName(c.Title, name)).Select(c => Candidate(c, "Name")).ToList();
        return sameName.Count == 1
            ? new(transactionId, HintKind.SameName, sameName[0].Id, sameName)
            : new(transactionId, HintKind.NoAccount, null, sameName);
    }

    private static HintCandidate Candidate(CurrentAccount c, string by) => new(c.Id, c.Code, c.Title, by);

    // Turkish casing: "deniz" and "DENİZ" are the same name, "ışık" and "IŞIK" too.
    private static bool SameName(string title, string name) =>
        string.Compare(title.Trim(), name.Trim(), CultureInfo.GetCultureInfo("tr-TR"), CompareOptions.IgnoreCase) == 0;

    /// <summary>Runs the rules on unmatched movements; each one that matches is queued for export. Returns how many matched.</summary>
    public async Task<int> AutoMatchAsync(IReadOnlyCollection<BankTransaction> transactions, Guid companyId, CancellationToken ct)
    {
        var open = transactions.Where(t => t.MatchStatus == MatchStatus.Unmatched && (t.CounterpartyIban is not null || t.CounterpartyTaxNumber is not null)).ToList();
        if (open.Count == 0)
        {
            return 0;
        }

        // Every candidate in one query, not one per movement.
        var ibans = open.Select(t => t.CounterpartyIban).OfType<string>().Distinct().ToList();
        var taxNumbers = open.Select(t => t.CounterpartyTaxNumber).OfType<string>().Distinct().ToList();
        var candidates = await db.CurrentAccounts.Where(c => ibans.Contains(c.Iban!) || taxNumbers.Contains(c.TaxNumber!)).ToListAsync(ct);
        if (candidates.Count == 0)
        {
            return 0;
        }

        var firmCode = await FirmCodeAsync(companyId, ct);
        var matched = 0;
        foreach (var transaction in open)
        {
            var (account, rule) = Decide(transaction.CounterpartyIban, transaction.CounterpartyTaxNumber, candidates);
            if (account is not null)
            {
                Apply(transaction, account, rule!.Value, userId: null, firmCode);
                matched++;
            }
        }

        return matched;
    }

    /// <summary>A person's decision: this current account (and export it), or none (it does not go to the ERP).</summary>
    public async Task ManualAsync(BankTransaction transaction, CurrentAccount? account, Guid userId, CancellationToken ct)
    {
        if (account is null)
        {
            transaction.MatchStatus = MatchStatus.Ignored;
            transaction.MatchRule = MatchRule.Manual;
            transaction.CurrentAccountId = null;
            transaction.MatchedAt = clock.GetUtcNow();
            (transaction.ExportStatus, transaction.ExportId, transaction.ExportError) = (ExportStatus.None, null, null);
            Audit(transaction, userId);
            return;
        }

        Apply(transaction, account, MatchRule.Manual, userId, await FirmCodeAsync(transaction.CompanyId, ct));
    }

    private void Apply(BankTransaction transaction, CurrentAccount account, MatchRule rule, Guid? userId, string? firmCode)
    {
        var now = clock.GetUtcNow();
        var sameAccount = transaction.CurrentAccountId == account.Id;
        transaction.MatchStatus = MatchStatus.Matched;
        transaction.MatchRule = rule;
        transaction.CurrentAccount = account;
        transaction.MatchedAt = now;
        Audit(transaction, userId);

        if (firmCode is null)
        {
            // No ERP firm set for the company: matched, but there is nowhere to export it.
            (transaction.ExportStatus, transaction.ExportId) = (ExportStatus.None, null);
            return;
        }

        // The export id is the ERP's Idempotency-Key. "Aktarılamadı" does not prove the ERP wrote nothing (it may have
        // written the voucher and lost the answer), so a resend to the same account keeps the key and the ERP replays
        // that voucher. Another account is a different request and gets a new key.
        var exportId = sameAccount && transaction.ExportId is { } previous ? previous : Guid.NewGuid();
        (transaction.ExportStatus, transaction.ExportId, transaction.ExportError) = (ExportStatus.Pending, exportId, null);
        var iban = transaction.Account?.Iban ?? throw new InvalidOperationException("The movement's bank account must be loaded to export it.");
        var local = TimeZoneInfo.ConvertTime(transaction.BookedAt, BankData.Turkey);
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            CompanyId = transaction.CompanyId == Guid.Empty ? account.CompanyId : transaction.CompanyId,
            Type = nameof(ExportVoucher),
            Payload = JsonSerializer.Serialize(new ExportVoucher(exportId, account.CompanyId, firmCode, account.Code, iban,
                DateOnly.FromDateTime(local.DateTime), transaction.Amount, transaction.Description, transaction.BankReference), RabbitConnection.Json),
            CreatedAt = now,
            TraceParent = System.Diagnostics.Activity.Current?.Id,
        });
    }

    private void Audit(BankTransaction transaction, Guid? userId) => db.MatchAudits.Add(new MatchAudit
    {
        BankTransaction = transaction,
        UserId = userId,
        Status = transaction.MatchStatus,
        Rule = transaction.MatchRule,
        CurrentAccountId = transaction.CurrentAccount?.Id ?? transaction.CurrentAccountId,
        At = clock.GetUtcNow(),
    });

    private Task<string?> FirmCodeAsync(Guid companyId, CancellationToken ct) =>
        db.Companies.Where(c => c.Id == companyId).Select(c => c.ErpFirmCode).SingleAsync(ct);

    private static CurrentAccount? OnlyOne(IEnumerable<CurrentAccount> matches)
    {
        var firstTwo = matches.Take(2).ToList();
        return firstTwo.Count == 1 ? firstTwo[0] : null;
    }
}
