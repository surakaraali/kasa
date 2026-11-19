using System.Buffers.Text;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using Kasa.Api.Auth;
using Kasa.Api.Data;
using Kasa.Api.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Kasa.Api.Banking;

public sealed record AccountDto(Guid Id, Guid ConnectionId, BankCode Bank, string Iban, string Name, string Currency, decimal? Balance, DateTimeOffset? BalanceAt);

public sealed record ConnectionDto(
    Guid Id, BankCode Bank, ConnectionStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? ConsentExpiresAt,
    DateTimeOffset? LastSyncAt, SyncOutcome? LastSyncOutcome, string? LastSyncError, IReadOnlyList<AccountDto> Accounts);

public sealed record StartAcikBankResponse(Guid ConnectionId, string AuthorizationUrl);

public sealed record KlasikBankRequest(
    [Required(ErrorMessage = "Kullanıcı adı gerekli."), MaxLength(100)] string Username,
    [Required(ErrorMessage = "Şifre gerekli."), MaxLength(100)] string Password);

public sealed record TransactionDto(
    long Id, Guid AccountId, BankCode Bank, string AccountName, string BankReference, DateTimeOffset BookedAt, decimal Amount,
    string Description, string? CounterpartyName, string? CounterpartyIban, string? CounterpartyTaxNumber);

public sealed record TransactionPage(IReadOnlyList<TransactionDto> Items, string? NextCursor);

public enum Direction
{
    In,
    Out,
}

public sealed class TransactionQuery
{
    public Guid? AccountId { get; init; }

    /// <summary>First day, inclusive, in Turkish time.</summary>
    public DateOnly? From { get; init; }

    /// <summary>Last day, inclusive, in Turkish time.</summary>
    public DateOnly? To { get; init; }

    public Direction? Direction { get; init; }

    [MaxLength(100)]
    public string? Search { get; init; }

    [Range(1, 200)]
    public int Limit { get; init; } = 50;

    /// <summary>The <see cref="TransactionPage.NextCursor"/> of the previous page.</summary>
    [MaxLength(200)]
    public string? Cursor { get; init; }
}

[ApiController]
[Route("api/bank-connections")]
public sealed class BankConnectionsController(AppDbContext db, BankConnections connections, BankSync sync, IConfiguration config) : ControllerBase
{
    private const string StateCookie = "kasa_bank_state";
    private const string CallbackPath = "/api/bank-connections/acikbank/callback";

    [HttpGet]
    public async Task<IReadOnlyList<ConnectionDto>> List(CancellationToken ct)
    {
        var rows = await db.BankConnections.AsNoTracking().Where(c => c.Status != ConnectionStatus.Removed)
            .OrderBy(c => c.CreatedAt).Include(c => c.Accounts).ToListAsync(ct);
        return rows.Select(c => new ConnectionDto(c.Id, c.Bank, c.Status, c.CreatedAt, c.ConsentExpiresAt, c.LastSyncAt, c.LastSyncOutcome,
            c.LastSyncError, c.Accounts.OrderBy(a => a.Name).Select(a => ToDto(a, c.Bank)).ToList())).ToList();
    }

    /// <summary>Starts open banking. The web app sends the browser to <c>authorizationUrl</c>; the bank sends it back to the callback.</summary>
    [HttpPost("acikbank")]
    [Authorize(Policy = Policies.CanEdit)]
    public async Task<ActionResult<StartAcikBankResponse>> StartAcikBank(CancellationToken ct)
    {
        try
        {
            var (connection, state) = await connections.StartAcikBankAsync(ct);
            // Lax, not Strict: the bank's redirect back is a cross-site top-level GET, and Strict cookies are not sent on it.
            Response.Cookies.Append(StateCookie, state, new CookieOptions
            {
                HttpOnly = true,
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Path = CallbackPath,
                MaxAge = TimeSpan.FromMinutes(15),
            });
            return new StartAcikBankResponse(connection.Id, connection.AuthorizationUrl!);
        }
        catch (ConnectionConflictException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (BankApiException ex)
        {
            return BankProblem(ex);
        }
    }

    [HttpGet("acikbank/callback")]
    [AllowAnonymous]
    public async Task<IActionResult> AcikBankCallback(string? state, string? code, string? error, CancellationToken ct)
    {
        var result = await connections.CompleteAcikBankAsync(state, code, error, Request.Cookies[StateCookie], ct);
        Response.Cookies.Delete(StateCookie, new CookieOptions { Path = CallbackPath, Secure = Request.IsHttps, SameSite = SameSiteMode.Lax });
        var outcome = result switch
        {
            CallbackResult.Connected => "baglandi",
            CallbackResult.Rejected => "reddedildi",
            _ => "hata",
        };
        // Always to our own web app, never to an address from the request.
        return Redirect($"{config["Web:BaseUrl"]?.TrimEnd('/')}/baglantilar?sonuc={outcome}");
    }

    [HttpPost("klasikbank")]
    [Authorize(Policy = Policies.CanEdit)]
    public async Task<ActionResult<ConnectionDto>> ConnectKlasikBank(KlasikBankRequest request, CancellationToken ct)
    {
        try
        {
            var connection = await connections.ConnectKlasikBankAsync(request.Username, request.Password, ct);
            return Created($"/api/bank-connections/{connection.Id}", (await List(ct)).Single(c => c.Id == connection.Id));
        }
        catch (ConnectionConflictException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (BankApiException ex)
        {
            return BankProblem(ex);
        }
    }

    /// <summary>"Fetch now". The same work the 15-minute job does, for one connection.</summary>
    [HttpPost("{id:guid}/sync")]
    [Authorize(Policy = Policies.CanEdit)]
    public async Task<ActionResult<SyncResult>> Sync(Guid id, CancellationToken ct)
    {
        if (!await db.BankConnections.AnyAsync(c => c.Id == id, ct))
        {
            return NotFound();
        }

        var result = await sync.SyncAsync(id, ct);
        return result.Outcome switch
        {
            SyncRunOutcome.AlreadyRunning => Problem("Bu bağlantı şu anda zaten çekiliyor.", statusCode: StatusCodes.Status409Conflict),
            SyncRunOutcome.NotActive => Problem("Bağlantı etkin değil; önce bankaya yeniden bağlanın.", statusCode: StatusCodes.Status409Conflict),
            _ => result,
        };
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct) =>
        await connections.RemoveAsync(id, ct) ? NoContent() : NotFound();

    internal static AccountDto ToDto(BankAccount a, BankCode bank) =>
        new(a.Id, a.BankConnectionId, bank, a.Iban, a.Name, a.Currency, a.Balance, a.BalanceAt);

    private ObjectResult BankProblem(BankApiException ex) => ex.Kind switch
    {
        BankErrorKind.Rejected => Problem("Banka bilgileri kabul etmedi. Kullanıcı adını ve şifreyi kontrol edin.", statusCode: StatusCodes.Status400BadRequest),
        BankErrorKind.Unavailable => Problem("Bankaya şu anda ulaşılamıyor. Biraz sonra tekrar deneyin.", statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Problem("Banka beklenmeyen bir cevap verdi.", statusCode: StatusCodes.Status502BadGateway),
    };
}

[ApiController]
[Route("api")]
public sealed class AccountsController(AppDbContext db) : ControllerBase
{
    [HttpGet("accounts")]
    public async Task<IReadOnlyList<AccountDto>> Accounts(CancellationToken ct) =>
        await db.BankAccounts.AsNoTracking().Where(a => a.Connection!.Status != ConnectionStatus.Removed).OrderBy(a => a.Name)
            .Select(a => new AccountDto(a.Id, a.BankConnectionId, a.Connection!.Bank, a.Iban, a.Name, a.Currency, a.Balance, a.BalanceAt))
            .ToListAsync(ct);

    /// <summary>
    /// Newest first, a page at a time. Keyset paging: the next page starts after the last row of this one, so page
    /// 1000 costs the same as page 1, and a movement arriving meanwhile does not shift rows between pages (OFFSET would).
    /// </summary>
    [HttpGet("transactions")]
    public async Task<ActionResult<TransactionPage>> Transactions([FromQuery] TransactionQuery q, CancellationToken ct)
    {
        var rows = db.BankTransactions.AsNoTracking();
        if (q.AccountId is { } accountId)
        {
            rows = rows.Where(t => t.BankAccountId == accountId);
        }

        if (q.From is { } from)
        {
            var start = StartOfDay(from);
            rows = rows.Where(t => t.BookedAt >= start);
        }

        if (q.To is { } to)
        {
            var end = StartOfDay(to.AddDays(1));
            rows = rows.Where(t => t.BookedAt < end);
        }

        rows = q.Direction switch
        {
            Direction.In => rows.Where(t => t.Amount > 0),
            Direction.Out => rows.Where(t => t.Amount < 0),
            _ => rows,
        };

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            // shortcut: LIKE '%...%' scans the company's movements; fine to a few hundred thousand rows, then full-text search.
            var search = q.Search.Trim();
            rows = rows.Where(t => t.Description.Contains(search) || t.CounterpartyName!.Contains(search));
        }

        if (q.Cursor is not null)
        {
            if (!TryReadCursor(q.Cursor, out var afterAt, out var afterId))
            {
                return Problem("Geçersiz sayfa imleci.", statusCode: StatusCodes.Status400BadRequest);
            }

            rows = rows.Where(t => t.BookedAt < afterAt || (t.BookedAt == afterAt && t.Id < afterId));
        }

        var items = await rows.OrderByDescending(t => t.BookedAt).ThenByDescending(t => t.Id).Take(q.Limit + 1)
            .Select(t => new TransactionDto(t.Id, t.BankAccountId, t.Account!.Connection!.Bank, t.Account.Name, t.BankReference, t.BookedAt,
                t.Amount, t.Description, t.CounterpartyName, t.CounterpartyIban, t.CounterpartyTaxNumber))
            .ToListAsync(ct);
        var next = items.Count > q.Limit ? Cursor(items[q.Limit - 1]) : null;
        return new TransactionPage(items.Take(q.Limit).ToList(), next);
    }

    private static DateTimeOffset StartOfDay(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, BankData.Turkey.GetUtcOffset(local));
    }

    private static string Cursor(TransactionDto last) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{last.BookedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}.{last.Id.ToString(CultureInfo.InvariantCulture)}"));

    private static bool TryReadCursor(string cursor, out DateTimeOffset bookedAt, out long id)
    {
        (bookedAt, id) = (default, 0);
        try
        {
            var parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor)).Split('.');
            if (parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                && ticks <= DateTimeOffset.MaxValue.UtcTicks && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out id))
            {
                bookedAt = new DateTimeOffset(ticks, TimeSpan.Zero);
                return true;
            }
        }
        catch (FormatException)
        {
            // Not base64url.
        }

        return false;
    }
}
