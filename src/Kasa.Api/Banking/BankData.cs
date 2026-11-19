using System.Globalization;
using Kasa.Api.Domain;

namespace Kasa.Api.Banking;

/// <summary>A movement in the shape every bank client returns.</summary>
public sealed record StatementLine(
    string Reference, DateTimeOffset BookedAt, decimal Amount, string Description,
    string? CounterpartyName, string? CounterpartyIban, string? CounterpartyTaxNumber);

public sealed record AccountInfo(string ExternalId, string Iban, string Name, string Currency, decimal? Balance);

public enum BankErrorKind
{
    /// <summary>Not reachable, too slow, or a 5xx: try again later.</summary>
    Unavailable,

    /// <summary>The bank refused us: wrong password, revoked or expired consent. Retrying will not help.</summary>
    Rejected,

    /// <summary>The bank answered with data we do not accept. Nothing is stored.</summary>
    InvalidData,
}

public sealed class BankApiException(BankCode bank, BankErrorKind kind, string message, Exception? inner = null, int? status = null)
    : Exception($"{bank}: {message}", inner)
{
    public BankCode Bank { get; } = bank;
    public BankErrorKind Kind { get; } = kind;

    /// <summary>The HTTP status, when the bank answered. 401 means "refresh the token and try again".</summary>
    public int? Status { get; } = status;
}

/// <summary>
/// The rules for turning what a bank sends into a <see cref="StatementLine"/>. Bank responses are outside data:
/// nothing is stored before it passes through here.
/// </summary>
public static class BankData
{
    public static readonly TimeZoneInfo Turkey = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    public static DateOnly TodayInTurkey(DateTimeOffset now) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Turkey).DateTime);

    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, BankCode bank, CancellationToken ct)
    {
        try
        {
            return await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new BankApiException(bank, BankErrorKind.Unavailable, "the bank could not be reached", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // HttpClient reports its own timeout as a cancellation.
            throw new BankApiException(bank, BankErrorKind.Unavailable, "the bank did not answer in time", ex);
        }
    }

    public static StatementLine Line(BankCode bank, int index, string? reference, DateTimeOffset bookedAt, decimal amount,
        string? description, string? counterpartyName, string? counterpartyIban, string? counterpartyTaxNumber)
    {
        // The reference is what stops duplicates, so it is never cut or guessed.
        var cleanReference = NullIfBlank(reference);
        if (cleanReference is null || cleanReference.Length > BankTransaction.BankReferenceMaxLength)
        {
            throw Invalid(bank, index, "the reference is missing or too long");
        }

        return new StatementLine(cleanReference, bookedAt, amount,
            Cut(NullIfBlank(description), BankTransaction.DescriptionMaxLength) ?? "",
            Cut(NullIfBlank(counterpartyName), BankTransaction.CounterpartyNameMaxLength),
            Iban(counterpartyIban), TaxNumber(counterpartyTaxNumber));
    }

    /// <summary>Digits with an optional decimal part, e.g. "1250.50". The server's culture plays no part.</summary>
    public static decimal PlainAmount(string digits) => decimal.Parse(digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

    public static BankApiException Invalid(BankCode bank, int index, string problem) =>
        new(bank, BankErrorKind.InvalidData, $"movement {index}: {problem}");

    public static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>"tr56 0006 4099..." becomes "TR560006409...". A broken IBAN is only a lost matching hint, so it becomes null.</summary>
    public static string? Iban(string? value)
    {
        // ToUpperInvariant, not ToUpper: on a Turkish server ToUpper turns "i" into "İ".
        var compact = NullIfBlank(value)?.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        return compact is { Length: >= 15 and <= 34 } && compact.All(char.IsAsciiLetterOrDigit) ? compact : null;
    }

    /// <summary>A VKN has 10 digits and a TCKN 11. Anything else becomes null.</summary>
    public static string? TaxNumber(string? value)
    {
        var trimmed = NullIfBlank(value);
        return trimmed is { Length: 10 or 11 } && trimmed.All(char.IsAsciiDigit) ? trimmed : null;
    }

    private static string? Cut(string? value, int maxLength) => value is null || value.Length <= maxLength ? value : value[..maxLength];
}
