using System.Text.Json;
using Kasa.Api.Banking;
using Kasa.Api.Data;
using Kasa.Api.Domain;
using Kasa.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Kasa.Api.Erp;

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

        // A new id per export: a re-match after a failed export is a new voucher request, not a replay of the old one.
        var exportId = Guid.NewGuid();
        (transaction.ExportStatus, transaction.ExportId, transaction.ExportError) = (ExportStatus.Pending, exportId, null);
        var iban = transaction.Account?.Iban ?? throw new InvalidOperationException("The movement's bank account must be loaded to export it.");
        var local = TimeZoneInfo.ConvertTime(transaction.BookedAt, BankData.Turkey);
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = exportId,
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
