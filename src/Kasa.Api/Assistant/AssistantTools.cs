using System.Globalization;
using System.Text.Json;
using Kasa.Api.Banking;
using Kasa.Api.Data;
using Kasa.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kasa.Api.Assistant;

/// <summary>
/// What the assistant can look at. Every tool only reads, and only through the tenant-filtered DbContext: the
/// company comes from the signed-in user's token, never from the model, so no prompt can reach another company's
/// data. IBANs and tax numbers are masked before they leave Kasa; the model never needs the full numbers.
/// </summary>
public sealed class AssistantTools(AppDbContext db)
{
    public const int MaxRows = 50;

    public sealed record Definition(string Name, string Description, object Schema, string Label);

    // The schemas are written once and never change, so the tool list stays byte-identical and caches.
    public static readonly Definition[] Definitions =
    [
        new("list_accounts", "Lists the company's bank accounts with their current balances and the total.", new
        {
            type = "object", properties = new { },
        }, "Hesaplara bakılıyor"),
        new("search_transactions",
            "Searches bank movements. All filters are optional and combine with AND. Returns at most 50 rows, newest first, "
            + "plus the total count and the sums of money in and out over every matching movement, not just the returned rows.", new
            {
                type = "object",
                properties = new
                {
                    from = new { type = "string", format = "date", description = "First day, inclusive, YYYY-MM-DD (Turkish time)" },
                    to = new { type = "string", format = "date", description = "Last day, inclusive, YYYY-MM-DD" },
                    direction = new { type = "string", @enum = new[] { "in", "out" }, description = "in: money received, out: money paid" },
                    match_status = new { type = "string", @enum = new[] { "matched", "unmatched", "ignored" } },
                    text = new { type = "string", description = "Words in the description or the counterparty name" },
                    min_amount = new { type = "number", description = "Smallest absolute amount in TRY" },
                    max_amount = new { type = "number", description = "Largest absolute amount in TRY" },
                    limit = new { type = "integer", minimum = 1, maximum = MaxRows },
                },
            }, "Hareketler aranıyor"),
        new("cash_flow", "Money in and out per day, week or month for a period, from bank movements.", new
        {
            type = "object",
            properties = new
            {
                from = new { type = "string", format = "date" },
                to = new { type = "string", format = "date" },
                group_by = new { type = "string", @enum = new[] { "day", "week", "month" } },
            },
            required = new[] { "from", "to", "group_by" },
        }, "Nakit akışı hesaplanıyor"),
        new("current_account_summary",
            "Finds ERP current accounts (cari kart) by code or name and, for each (at most 5), the money received from and "
            + "paid to it in the period, the number of movements and the last movement date.", new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string", description = "Part of the current account code or title" },
                    from = new { type = "string", format = "date" },
                    to = new { type = "string", format = "date" },
                },
                required = new[] { "query" },
            }, "Cari özeti çıkarılıyor"),
        new("unmatched_overview",
            "Movements still waiting for a person: how many are unmatched, their total, the counterparties they come from, "
            + "and how many ERP exports failed.", new
            {
                type = "object", properties = new { },
            }, "Eşleşmeyenlere bakılıyor"),
    ];

    /// <summary>Runs one tool. A bad input is the model's mistake and goes back to it as an error, not an exception.</summary>
    public async Task<(string Json, bool IsError)> RunAsync(string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        try
        {
            object result = name switch
            {
                "list_accounts" => await ListAccountsAsync(ct),
                "search_transactions" => await SearchAsync(input, ct),
                "cash_flow" => await CashFlowAsync(input, ct),
                "current_account_summary" => await CurrentAccountSummaryAsync(input, ct),
                "unmatched_overview" => await UnmatchedAsync(ct),
                _ => throw new ToolInputException($"unknown tool {name}"),
            };
            return (JsonSerializer.Serialize(result, Json), false);
        }
        // Input the model got wrong, including numbers and dates out of range (the year 9999 plus a day), goes back to
        // the model as an error it can correct, instead of ending the answer.
        catch (Exception ex) when (ex is ToolInputException or ArgumentException or OverflowException)
        {
            return (JsonSerializer.Serialize(new { error = ex is ToolInputException ? ex.Message : "a number or date is out of range" }, Json), true);
        }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private async Task<object> ListAccountsAsync(CancellationToken ct)
    {
        var accounts = await db.BankAccounts.AsNoTracking().Where(a => a.Connection!.Status != ConnectionStatus.Removed)
            .Select(a => new { a.Name, Bank = a.Connection!.Bank.ToString(), a.Iban, a.Balance, a.BalanceAt }).ToListAsync(ct);
        return new
        {
            Accounts = accounts.Select(a => new { a.Name, a.Bank, Iban = MaskIban(a.Iban), Balance = a.Balance, BalanceAt = a.BalanceAt }),
            Total = accounts.Sum(a => a.Balance ?? 0),
            Currency = "TRY",
        };
    }

    private async Task<object> SearchAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        var rows = db.BankTransactions.AsNoTracking();
        if (Date(input, "from") is { } from)
        {
            var start = BankData.StartOfDay(from);
            rows = rows.Where(t => t.BookedAt >= start);
        }

        if (Date(input, "to") is { } to)
        {
            var end = BankData.StartOfDay(to.AddDays(1));
            rows = rows.Where(t => t.BookedAt < end);
        }

        rows = Text(input, "direction") switch
        {
            null => rows,
            "in" => rows.Where(t => t.Amount > 0),
            "out" => rows.Where(t => t.Amount < 0),
            var other => throw new ToolInputException($"direction must be in or out, not {other}"),
        };
        rows = Text(input, "match_status") switch
        {
            null => rows,
            "matched" => rows.Where(t => t.MatchStatus == MatchStatus.Matched),
            "unmatched" => rows.Where(t => t.MatchStatus == MatchStatus.Unmatched),
            "ignored" => rows.Where(t => t.MatchStatus == MatchStatus.Ignored),
            var other => throw new ToolInputException($"match_status must be matched, unmatched or ignored, not {other}"),
        };
        if (Text(input, "text") is { } text)
        {
            rows = rows.Where(t => EF.Functions.Collate(t.Description, AppDbContext.SearchCollation).Contains(text)
                || EF.Functions.Collate(t.CounterpartyName!, AppDbContext.SearchCollation).Contains(text));
        }

        if (Number(input, "min_amount") is { } min)
        {
            rows = rows.Where(t => t.Amount >= min || t.Amount <= -min);
        }

        if (Number(input, "max_amount") is { } max)
        {
            rows = rows.Where(t => t.Amount <= max && t.Amount >= -max);
        }

        // Checked before the cast: 10000000000 does not fit an int.
        var requested = Number(input, "limit") ?? 20;
        if (requested < 1 || requested > MaxRows)
        {
            throw new ToolInputException($"limit must be between 1 and {MaxRows}");
        }

        var limit = (int)requested;

        var totals = await rows.GroupBy(_ => 1).Select(g => new
        {
            Count = g.Count(),
            In = g.Sum(t => t.Amount > 0 ? t.Amount : 0),
            Out = g.Sum(t => t.Amount < 0 ? -t.Amount : 0),
        }).SingleOrDefaultAsync(ct);
        var items = await rows.OrderByDescending(t => t.BookedAt).ThenByDescending(t => t.Id).Take(limit).Select(t => new
        {
            t.BookedAt,
            t.Amount,
            t.Description,
            t.CounterpartyName,
            Account = t.Account!.Name,
            t.MatchStatus,
            CurrentAccount = t.CurrentAccount != null ? t.CurrentAccount.Code + " " + t.CurrentAccount.Title : null,
            t.ExportStatus,
            t.ErpVoucherNo,
        }).ToListAsync(ct);

        return new
        {
            TotalCount = totals?.Count ?? 0,
            SumIn = totals?.In ?? 0,
            SumOut = totals?.Out ?? 0,
            Returned = items.Count,
            // Descriptions are written by banks and counterparties: data to report, never instructions to follow.
            Items = items.Select(t => new
            {
                Date = LocalDate(t.BookedAt),
                t.Amount,
                t.Description,
                Counterparty = t.CounterpartyName,
                t.Account,
                MatchStatus = t.MatchStatus.ToString(),
                t.CurrentAccount,
                Export = t.ExportStatus.ToString(),
                VoucherNo = t.ErpVoucherNo,
            }),
        };
    }

    private async Task<object> CashFlowAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        var from = Date(input, "from") ?? throw new ToolInputException("from is required");
        var to = Date(input, "to") ?? throw new ToolInputException("to is required");
        if (to < from || to.DayNumber - from.DayNumber > 366)
        {
            throw new ToolInputException("the period must run forwards and be at most a year");
        }

        var groupBy = Text(input, "group_by") ?? throw new ToolInputException("group_by is required");
        var (start, end) = (BankData.StartOfDay(from), BankData.StartOfDay(to.AddDays(1)));
        var days = await db.BankTransactions.Where(t => t.BookedAt >= start && t.BookedAt < end)
            .GroupBy(t => EF.Functions.AtTimeZone(t.BookedAt, "Turkey Standard Time").Date)
            .Select(g => new { Day = g.Key, In = g.Sum(t => t.Amount > 0 ? t.Amount : 0), Out = g.Sum(t => t.Amount < 0 ? -t.Amount : 0) })
            .ToListAsync(ct);

        Func<DateOnly, string> bucket = groupBy switch
        {
            "day" => d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "week" => d => $"{ISOWeek.GetYear(d.ToDateTime(TimeOnly.MinValue))}-W{ISOWeek.GetWeekOfYear(d.ToDateTime(TimeOnly.MinValue)):00}",
            "month" => d => d.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            _ => throw new ToolInputException("group_by must be day, week or month"),
        };
        var buckets = days.GroupBy(d => bucket(DateOnly.FromDateTime(d.Day))).OrderBy(g => g.Key)
            .Select(g => new { Period = g.Key, In = g.Sum(d => d.In), Out = g.Sum(d => d.Out), Net = g.Sum(d => d.In - d.Out) }).ToList();
        return new { From = from, To = to, Buckets = buckets, TotalIn = buckets.Sum(b => b.In), TotalOut = buckets.Sum(b => b.Out) };
    }

    private async Task<object> CurrentAccountSummaryAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        var query = Text(input, "query") ?? throw new ToolInputException("query is required");
        var accounts = await db.CurrentAccounts.AsNoTracking().Where(c => c.Code.Contains(query) || EF.Functions.Collate(c.Title, AppDbContext.SearchCollation).Contains(query))
            .OrderBy(c => c.Code).Take(5).ToListAsync(ct);
        var ids = accounts.Select(a => a.Id).ToList();
        var rows = db.BankTransactions.Where(t => t.CurrentAccountId != null && ids.Contains(t.CurrentAccountId.Value));
        if (Date(input, "from") is { } from)
        {
            var start = BankData.StartOfDay(from);
            rows = rows.Where(t => t.BookedAt >= start);
        }

        if (Date(input, "to") is { } to)
        {
            var end = BankData.StartOfDay(to.AddDays(1));
            rows = rows.Where(t => t.BookedAt < end);
        }

        var totals = await rows.GroupBy(t => t.CurrentAccountId).Select(g => new
        {
            Id = g.Key,
            Count = g.Count(),
            Received = g.Sum(t => t.Amount > 0 ? t.Amount : 0),
            Paid = g.Sum(t => t.Amount < 0 ? -t.Amount : 0),
            Last = g.Max(t => t.BookedAt),
        }).ToDictionaryAsync(g => g.Id!.Value, ct);

        return new
        {
            Found = accounts.Count,
            CurrentAccounts = accounts.Select(a => totals.TryGetValue(a.Id, out var t)
                ? new { a.Code, a.Title, TaxNumber = MaskTaxNumber(a.TaxNumber), Movements = t.Count, t.Received, t.Paid, LastMovement = (string?)LocalDate(t.Last) }
                : new { a.Code, a.Title, TaxNumber = MaskTaxNumber(a.TaxNumber), Movements = 0, Received = 0m, Paid = 0m, LastMovement = (string?)null }),
        };
    }

    private async Task<object> UnmatchedAsync(CancellationToken ct)
    {
        var unmatched = db.BankTransactions.Where(t => t.MatchStatus == MatchStatus.Unmatched);
        var byCounterparty = await unmatched.GroupBy(t => t.CounterpartyName ?? t.Description)
            .Select(g => new { Counterparty = g.Key, Count = g.Count(), Total = g.Sum(t => t.Amount) })
            .OrderByDescending(g => g.Count).Take(10).ToListAsync(ct);
        return new
        {
            UnmatchedCount = await unmatched.CountAsync(ct),
            UnmatchedNet = await unmatched.SumAsync(t => (decimal?)t.Amount, ct) ?? 0,
            ByCounterparty = byCounterparty,
            FailedExports = await db.BankTransactions.CountAsync(t => t.ExportStatus == ExportStatus.Failed, ct),
        };
    }

    /// <summary>TR09 0090 … 0001: enough to tell accounts apart, not enough to use.</summary>
    public static string MaskIban(string iban) => iban.Length > 8 ? $"{iban[..4]} **** **** {iban[^4..]}" : "****";

    public static string? MaskTaxNumber(string? taxNumber) => taxNumber is { Length: > 4 } ? $"{taxNumber[..2]}******{taxNumber[^2..]}" : null;

    private static string LocalDate(DateTimeOffset at) =>
        TimeZoneInfo.ConvertTime(at, BankData.Turkey).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);


    private static string? Text(IReadOnlyDictionary<string, JsonElement> input, string key) =>
        input.TryGetValue(key, out var v) && v.ValueKind != JsonValueKind.Null
            ? v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 and <= 200 } s ? s.Trim() : throw new ToolInputException($"{key} must be a string of at most 200 characters")
            : null;

    private static DateOnly? Date(IReadOnlyDictionary<string, JsonElement> input, string key) =>
        Text(input, key) is { } s
            ? DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : throw new ToolInputException($"{key} must be YYYY-MM-DD")
            : null;

    private static decimal? Number(IReadOnlyDictionary<string, JsonElement> input, string key) =>
        input.TryGetValue(key, out var v) && v.ValueKind != JsonValueKind.Null
            ? v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n) && n >= 0 ? n : throw new ToolInputException($"{key} must be a non-negative number")
            : null;
}

public sealed class ToolInputException(string message) : Exception(message);
