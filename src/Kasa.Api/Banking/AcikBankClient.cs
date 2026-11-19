using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Kasa.Api.Domain;
using Kasa.Api.Infrastructure;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Kasa.Api.Banking;

public sealed class AcikBankOptions
{
    [Required, Url]
    public string BaseUrl { get; set; } = "";

    [Required]
    public string ClientId { get; set; } = "";

    [Required]
    public string ClientSecret { get; set; } = "";

    /// <summary>This API's callback, exactly as registered at the bank.</summary>
    [Required, Url]
    public string RedirectUri { get; set; } = "";

    /// <summary>The address the customer's browser opens. Defaults to BaseUrl; differs when the bank is behind a proxy.</summary>
    public string? PublicUrl { get; set; }
}

public sealed record BankTokens(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken);

/// <summary>AcikBank's open banking API, from the client side: consent, OAuth 2.0 with PKCE, accounts, transactions.</summary>
public sealed partial class AcikBankClient(HttpClient http, IOptions<AcikBankOptions> options)
{
    private const BankCode Bank = BankCode.AcikBank;
    private readonly AcikBankOptions bank = options.Value;

    public async Task<string> CreateConsentAsync(DateTimeOffset expiresAt, CancellationToken ct)
    {
        using var request = AsClient(HttpMethod.Post, "consents", JsonContent.Create(new { permissions = new[] { "ReadAccounts", "ReadBalances", "ReadTransactions" }, expiresAt }));
        return (await SendJsonAsync<ConsentBody>(request, ct)).ConsentId;
    }

    public string AuthorizationUrl(string consentId, string state, string codeChallenge) =>
        QueryHelpers.AddQueryString((bank.PublicUrl ?? bank.BaseUrl).TrimEnd('/') + "/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = bank.ClientId,
            ["consent_id"] = consentId,
            ["redirect_uri"] = bank.RedirectUri,
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        });

    public Task<BankTokens> ExchangeCodeAsync(string code, string verifier, CancellationToken ct) => TokenAsync(new()
    {
        ["grant_type"] = "authorization_code",
        ["code"] = code,
        ["code_verifier"] = verifier,
        ["redirect_uri"] = bank.RedirectUri,
    }, ct);

    public Task<BankTokens> RefreshAsync(string refreshToken, CancellationToken ct) =>
        TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken }, ct);

    public async Task RevokeConsentAsync(string consentId, CancellationToken ct)
    {
        using var request = AsClient(HttpMethod.Delete, $"consents/{Uri.EscapeDataString(consentId)}");
        using var response = await BankData.SendAsync(http, request, Bank, ct);
        // Already gone at the bank is the result we want.
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
        {
            throw await ErrorAsync(response, ct);
        }
    }

    public async Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(string accessToken, RawResponseTag tag, CancellationToken ct)
    {
        using var request = AsBearer("accounts", accessToken, tag);
        var body = await SendJsonAsync<AccountsBody>(request, ct);
        if (body.Accounts is null)
        {
            throw new BankApiException(Bank, BankErrorKind.InvalidData, "the response has no accounts list");
        }

        return body.Accounts.Select((a, i) => a is { AccountId: not null, Iban: not null }
            ? new AccountInfo(a.AccountId, BankData.Iban(a.Iban) ?? a.Iban, a.Name ?? a.AccountId, a.Currency ?? "TRY",
                a.Balance is not null && PlainAmount().IsMatch(a.Balance.TrimStart('-')) ? decimal.Parse(a.Balance, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) : null)
            : throw new BankApiException(Bank, BankErrorKind.InvalidData, $"account {i}: id or IBAN missing")).ToList();
    }

    public async Task<IReadOnlyList<StatementLine>> GetTransactionsAsync(string accessToken, string accountId, DateOnly from, DateOnly to, RawResponseTag tag, CancellationToken ct)
    {
        // "O" is always yyyy-MM-dd, whatever the server's culture or calendar.
        using var request = AsBearer($"accounts/{Uri.EscapeDataString(accountId)}/transactions?fromDate={from:O}&toDate={to:O}", accessToken, tag);
        var body = await SendJsonAsync<StatementBody>(request, ct);
        if (body.Transactions is null)
        {
            throw new BankApiException(Bank, BankErrorKind.InvalidData, "the response has no transactions list");
        }

        return body.Transactions.Select(ToLine).ToList();
    }

    private static StatementLine ToLine(TransactionBody? t, int index)
    {
        if (t is null)
        {
            throw BankData.Invalid(Bank, index, "the entry is empty");
        }

        var sign = t.CreditDebitIndicator switch
        {
            "CRDT" => 1,
            "DBIT" => -1,
            _ => throw BankData.Invalid(Bank, index, "creditDebitIndicator must be CRDT or DBIT"),
        };

        // Only plain digits with up to two decimals: decimal.Parse with a Turkish culture would read "1250.50" as 125050.
        if (t.Amount?.Value is null || !PlainAmount().IsMatch(t.Amount.Value))
        {
            throw BankData.Invalid(Bank, index, "amount.value must be digits with at most two decimals");
        }

        // The offset is required; without it the time would be read in the server's own time zone.
        if (!DateTimeOffset.TryParseExact(t.BookingDateTime, ["yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mm:ss'Z'"],
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var bookedAt))
        {
            throw BankData.Invalid(Bank, index, "bookingDateTime must be ISO 8601 with an offset");
        }

        return BankData.Line(Bank, index, t.TransactionId, bookedAt, sign * BankData.PlainAmount(t.Amount.Value),
            t.RemittanceInformation, t.Counterparty?.Name, t.Counterparty?.Iban, t.Counterparty?.TaxNumber);
    }

    private async Task<BankTokens> TokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var request = AsClient(HttpMethod.Post, "token", new FormUrlEncodedContent(form));
        return await SendJsonAsync<BankTokens>(request, ct);
    }

    private HttpRequestMessage AsClient(HttpMethod method, string path, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{bank.ClientId}:{bank.ClientSecret}")));
        return request;
    }

    /// <summary>Data calls are archived; token calls never are, because their answers carry tokens.</summary>
    private static HttpRequestMessage AsBearer(string path, string accessToken, RawResponseTag tag)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Options.Set(RawResponseTag.Key, tag);
        return request;
    }

    private async Task<T> SendJsonAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await BankData.SendAsync(http, request, Bank, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await ErrorAsync(response, ct);
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<T>(ct) ?? throw new BankApiException(Bank, BankErrorKind.InvalidData, "empty body");
        }
        catch (JsonException ex)
        {
            throw new BankApiException(Bank, BankErrorKind.InvalidData, "the response is not the expected JSON", ex);
        }
    }

    private static async Task<BankApiException> ErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? error = null;
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ErrorBody>(ct);
            error = body?.Description ?? body?.Error;
        }
        catch (JsonException)
        {
            // Not JSON: a proxy page or the plain "Service Unavailable" of a bank that is down.
        }

        var status = (int)response.StatusCode;
        var kind = status >= 500 || status == 429 ? BankErrorKind.Unavailable : BankErrorKind.Rejected;
        return new BankApiException(Bank, kind, $"HTTP {status}{(error is null ? "" : $" {error}")}", status: status);
    }

    // [0-9], not \d: in .NET \d also matches digits of other scripts, which decimal.Parse then rejects.
    [GeneratedRegex("^[0-9]{1,16}(\\.[0-9]{1,2})?$")]
    private static partial Regex PlainAmount();

    private sealed record ConsentBody(string ConsentId);

    // Every field is nullable: this is outside data and any of it can be missing.
    private sealed record AccountsBody(List<AccountBody?>? Accounts);

    private sealed record AccountBody(string? AccountId, string? Iban, string? Name, string? Currency, string? Balance);

    private sealed record StatementBody(List<TransactionBody?>? Transactions);

    private sealed record TransactionBody(string? TransactionId, string? BookingDateTime, AmountBody? Amount, string? CreditDebitIndicator,
        string? RemittanceInformation, CounterpartyBody? Counterparty);

    private sealed record AmountBody(string? Value, string? Currency);

    private sealed record CounterpartyBody(string? Name, string? Iban, string? TaxNumber);

    private sealed record ErrorBody([property: JsonPropertyName("error")] string? Error, [property: JsonPropertyName("error_description")] string? Description);
}
