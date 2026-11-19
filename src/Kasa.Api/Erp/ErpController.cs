using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Kasa.Api.Auth;
using Kasa.Api.Banking;
using Kasa.Api.Common;
using Kasa.Api.Data;
using Kasa.Api.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Kasa.Api.Erp;

public sealed record ErpSettingsRequest(
    [Required(ErrorMessage = "ERP firma kodu gerekli."), RegularExpression("^[0-9A-Za-z]{1,10}$", ErrorMessage = "Firma kodu en fazla 10 harf ya da rakam.")] string FirmCode);

public sealed record CurrentAccountDto(Guid Id, string Code, string Title, string? TaxNumber, string? Iban);

public sealed record ImportResult(int Added, int Updated, int Matched);

/// <param name="CurrentAccountId">Null means "this movement does not go to the ERP".</param>
/// <param name="RowVersion">The version the person saw. If someone changed the movement since, the answer is 409.</param>
public sealed record MatchRequest(Guid? CurrentAccountId, [Required] byte[] RowVersion);

public sealed record MatchHistoryDto(DateTimeOffset At, MatchStatus Status, MatchRule? Rule, Guid? CurrentAccountId, string? CurrentAccountTitle, Guid? UserId, string? UserName);

[ApiController]
[Route("api")]
public sealed class ErpController(AppDbContext db, TenantContext tenant, ErpReader erp, Matching matching, TimeProvider clock, ILogger<ErpController> log)
    : ControllerBase
{
    [HttpPut("company/erp")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> SetErp(ErpSettingsRequest request, CancellationToken ct)
    {
        var company = await db.Companies.SingleAsync(c => c.Id == tenant.CompanyId, ct);
        company.ErpFirmCode = request.FirmCode;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("current-accounts")]
    public async Task<IReadOnlyList<CurrentAccountDto>> CurrentAccounts(CancellationToken ct) =>
        await db.CurrentAccounts.AsNoTracking().OrderBy(c => c.Code)
            .Select(c => new CurrentAccountDto(c.Id, c.Code, c.Title, c.TaxNumber, c.Iban)).ToListAsync(ct);

    /// <summary>Copies the current accounts from the ERP, then runs the rules again on every unmatched movement.</summary>
    [HttpPost("current-accounts/import")]
    [Authorize(Policy = Policies.CanEdit)]
    public async Task<ActionResult<ImportResult>> Import(CancellationToken ct)
    {
        var companyId = tenant.CompanyId!.Value;
        var firmCode = await db.Companies.Where(c => c.Id == companyId).Select(c => c.ErpFirmCode).SingleAsync(ct);
        if (firmCode is null)
        {
            return Problem("Önce ERP firma kodunu girin.", statusCode: StatusCodes.Status400BadRequest);
        }

        IReadOnlyList<ErpCurrentAccount> fromErp;
        try
        {
            fromErp = await erp.CurrentAccountsAsync(firmCode, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Reading current accounts from the ERP failed");
            return Problem("ERP'ye şu anda ulaşılamıyor.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var known = await db.CurrentAccounts.ToDictionaryAsync(c => c.Code, ct);
        var (added, updated) = (0, 0);
        var now = clock.GetUtcNow();
        foreach (var item in fromErp.Where(i => !string.IsNullOrWhiteSpace(i.Code)).DistinctBy(i => i.Code))
        {
            if (!known.TryGetValue(item.Code, out var account))
            {
                account = new CurrentAccount { Code = item.Code, Title = item.Title };
                db.CurrentAccounts.Add(account);
                added++;
            }
            else
            {
                updated++;
            }

            // Stored the way the bank clients store counterparties, so the rules compare like with like.
            account.Title = item.Title.Length <= 200 ? item.Title : item.Title[..200];
            account.Iban = BankData.Iban(item.Iban);
            account.TaxNumber = BankData.TaxNumber(item.TaxNumber);
            account.ImportedAt = now;
        }

        await db.SaveChangesAsync(ct);

        // shortcut: loads every unmatched movement at once; page through them when a company has tens of thousands.
        var unmatched = await db.BankTransactions.Include(t => t.Account).Where(t => t.MatchStatus == MatchStatus.Unmatched).ToListAsync(ct);
        var matched = await matching.AutoMatchAsync(unmatched, companyId, ct);
        await db.SaveChangesAsync(ct);
        return new ImportResult(added, updated, matched);
    }

    /// <summary>A person's matching decision. Optimistic concurrency: two people deciding the same movement cannot both win silently.</summary>
    [HttpPut("transactions/{id:long}/match")]
    [Authorize(Policy = Policies.CanEdit)]
    public async Task<IActionResult> Match(long id, MatchRequest request, CancellationToken ct)
    {
        var transaction = await db.BankTransactions.Include(t => t.Account).SingleOrDefaultAsync(t => t.Id == id, ct);
        if (transaction is null)
        {
            return NotFound();
        }

        // The check runs against the version the person saw, not the one just loaded.
        db.Entry(transaction).Property(t => t.RowVersion).OriginalValue = request.RowVersion;
        if (!transaction.RowVersion.AsSpan().SequenceEqual(request.RowVersion))
        {
            return Conflict409();
        }

        if (transaction.ExportStatus is ExportStatus.Exported or ExportStatus.Pending)
        {
            return Problem(transaction.ExportStatus == ExportStatus.Exported
                ? "Bu hareket ERP'ye aktarıldı; eşleşmesi değiştirilemez. Düzeltme ERP'de yapılmalı."
                : "Bu hareket şu anda ERP'ye aktarılıyor.", statusCode: StatusCodes.Status409Conflict);
        }

        CurrentAccount? account = null;
        if (request.CurrentAccountId is { } accountId)
        {
            account = await db.CurrentAccounts.SingleOrDefaultAsync(c => c.Id == accountId, ct);
            if (account is null)
            {
                return Problem("Cari kart bulunamadı.", statusCode: StatusCodes.Status400BadRequest);
            }
        }

        await matching.ManualAsync(transaction, account, Guid.Parse(User.FindFirstValue(KasaClaims.UserId)!), ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict409();
        }

        return NoContent();
    }

    [HttpGet("transactions/{id:long}/history")]
    public async Task<ActionResult<IReadOnlyList<MatchHistoryDto>>> History(long id, CancellationToken ct)
    {
        if (!await db.BankTransactions.AnyAsync(t => t.Id == id, ct))
        {
            return NotFound();
        }

        var rows = await db.MatchAudits.AsNoTracking().Where(a => a.BankTransactionId == id).OrderBy(a => a.Id).ToListAsync(ct);
        var accountIds = rows.Select(r => r.CurrentAccountId).OfType<Guid>().Distinct().ToList();
        var userIds = rows.Select(r => r.UserId).OfType<Guid>().Distinct().ToList();
        var titles = await db.CurrentAccounts.Where(c => accountIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Title, ct);
        var names = await db.Users.Where(u => userIds.Contains(u.Id) && u.CompanyId == tenant.CompanyId).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        return rows.Select(r => new MatchHistoryDto(r.At, r.Status, r.Rule, r.CurrentAccountId,
            r.CurrentAccountId is { } a ? titles.GetValueOrDefault(a) : null, r.UserId, r.UserId is { } u ? names.GetValueOrDefault(u) : null)).ToList();
    }

    private ObjectResult Conflict409() =>
        Problem("Bu hareket siz bakarken başka biri tarafından değiştirildi. Sayfayı yenileyip tekrar deneyin.", statusCode: StatusCodes.Status409Conflict);
}
