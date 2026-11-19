using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Kasa.MockBanks;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

// Two test banks for local development, the demo and the tests. Nothing here is production code.
//   AcikBank   - open banking over REST + JSON: consent, a page where the customer approves, OAuth 2.0 with PKCE
//   KlasikBank - an older bank over SOAP 1.1 + XML, with Turkish number and date formats
// /mock lets the demo and the tests add a movement now, take a bank down or make it send bad data.

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<BankWorld>();
builder.Services.AddSingleton<AcikBankAuth>();
builder.Services.Configure<MockBanksOptions>(builder.Configuration.GetSection("MockBanks"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<MockBanksOptions>>().Value);
var app = builder.Build();

var invariant = CultureInfo.InvariantCulture;
// The format a Turkish statement uses: 1.250,50. Written out, so it works where culture data is not installed.
var turkish = new NumberFormatInfo { NumberDecimalSeparator = ",", NumberGroupSeparator = "." };
XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
XNamespace kb = "http://klasikbank.example/ekstre";

app.MapGet("/", () => "Kasa test banks: AcikBank (REST, open banking) and KlasikBank (SOAP). Test data only.");
app.MapHealthChecks("/health");

// Faults and slowness apply to the bank endpoints, never to /mock or /health.
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var bank = path.StartsWithSegments("/acikbank") ? "acikbank" : path.StartsWithSegments("/klasikbank") ? "klasikbank" : null;
    var world = context.RequestServices.GetRequiredService<BankWorld>();
    switch (bank is null ? Fault.None : world.FaultOf(bank))
    {
        case Fault.Down:
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync("Service Unavailable");
            return;
        case Fault.Slow:
            await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted);
            break;
    }

    await next();
});

// ---------- AcikBank: open banking ----------

app.MapPost("/acikbank/consents", (ConsentRequest body, HttpRequest request, AcikBankAuth auth, MockBanksOptions options, TimeProvider clock) =>
{
    if (!IsClient(request, options))
    {
        return OAuthError(401, "invalid_client");
    }

    var now = clock.GetUtcNow();
    if (body.ExpiresAt <= now || body.ExpiresAt > now.AddDays(180))
    {
        return OAuthError(400, "invalid_request", "expiresAt must be within 180 days");
    }

    var consent = auth.CreateConsent(body.ExpiresAt);
    return Results.Json(new { consentId = consent.Id, status = consent.Status.ToString() }, statusCode: 201);
});

app.MapDelete("/acikbank/consents/{id}", (string id, HttpRequest request, AcikBankAuth auth, MockBanksOptions options) =>
    !IsClient(request, options) ? OAuthError(401, "invalid_client") : auth.Revoke(id) ? Results.NoContent() : Results.NotFound());

app.MapGet("/acikbank/authorize", (string? client_id, string? consent_id, string? redirect_uri, string? state, string? code_challenge,
    string? code_challenge_method, AcikBankAuth auth, MockBanksOptions options) =>
{
    // A wrong client or redirect address gets an error page, never a redirect: redirecting to whatever address the
    // request names would make the bank an open redirect and could hand the code to someone else.
    if (client_id != options.ClientId || redirect_uri is null || !options.RedirectUris.Contains(redirect_uri))
    {
        return Page(400, "<p>Geçersiz istemci ya da yönlendirme adresi.</p>");
    }

    if (string.IsNullOrEmpty(state) || string.IsNullOrEmpty(code_challenge) || code_challenge_method != "S256")
    {
        return Page(400, "<p>Eksik parametre: state ve S256 code_challenge gerekli.</p>");
    }

    var consent = auth.Find(consent_id ?? "");
    if (consent is not { Status: ConsentStatus.AwaitingAuthorization })
    {
        return Page(400, "<p>Rıza bulunamadı ya da daha önce yanıtlanmış.</p>");
    }

    consent.RedirectUri = redirect_uri;
    consent.State = state;
    consent.CodeChallenge = code_challenge;
    return Page(200,
        "<p><b>Kasa</b> şu bilgilere erişmek istiyor:</p><ul><li>Hesaplarınız ve bakiyeleri</li><li>Hesap hareketleriniz</li></ul>"
        + $"<p>Geçerlilik: {consent.ExpiresAt:dd.MM.yyyy} tarihine kadar</p>"
        + "<form method=\"post\" action=\"authorize\">"
        + $"<input type=\"hidden\" name=\"consent_id\" value=\"{WebUtility.HtmlEncode(consent.Id)}\">"
        + "<button name=\"decision\" value=\"approve\">Onayla</button> <button name=\"decision\" value=\"reject\">Reddet</button></form>");
});

app.MapPost("/acikbank/authorize", async (HttpRequest request, AcikBankAuth auth, TimeProvider clock) =>
{
    var form = await request.ReadFormAsync();
    var consent = auth.Find(form["consent_id"].ToString());
    if (consent is not { Status: ConsentStatus.AwaitingAuthorization, RedirectUri: not null })
    {
        return Page(400, "<p>Rıza bulunamadı ya da daha önce yanıtlanmış.</p>");
    }

    var code = auth.Answer(consent, form["decision"] == "approve", clock.GetUtcNow());
    var query = new Dictionary<string, string?> { ["state"] = consent.State };
    if (code is null)
    {
        query["error"] = "access_denied";
    }
    else
    {
        query["code"] = code;
    }

    return Results.Redirect(QueryHelpers.AddQueryString(consent.RedirectUri, query));
});

app.MapPost("/acikbank/token", async (HttpRequest request, AcikBankAuth auth, MockBanksOptions options, TimeProvider clock) =>
{
    if (!IsClient(request, options))
    {
        return OAuthError(401, "invalid_client");
    }

    var form = await request.ReadFormAsync();
    var now = clock.GetUtcNow();
    var (access, refresh, error) = form["grant_type"].ToString() switch
    {
        "authorization_code" => auth.ExchangeCode(form["code"].ToString(), form["code_verifier"].ToString(), form["redirect_uri"].ToString(), now, options.AccessTokenLifetime),
        "refresh_token" => auth.Refresh(form["refresh_token"].ToString(), now, options.AccessTokenLifetime),
        _ => (null, null, "unsupported_grant_type"),
    };

    return error is not null
        ? OAuthError(400, "invalid_grant", error)
        : Results.Json(new { access_token = access, token_type = "Bearer", expires_in = (int)options.AccessTokenLifetime.TotalSeconds, refresh_token = refresh });
});

app.MapGet("/acikbank/accounts", (HttpRequest request, AcikBankAuth auth, BankWorld world, TimeProvider clock) =>
{
    var now = clock.GetUtcNow();
    var (consent, status, error) = auth.Authorize(request.Headers.Authorization, now);
    if (consent is null)
    {
        return OAuthError(status, error!);
    }

    return Results.Json(new
    {
        accounts = BankWorld.AcikAccounts.Select(a => new
        {
            accountId = a.Id,
            iban = a.Iban,
            name = a.Name,
            currency = "TRY",
            balance = world.Balance(a, now).ToString("0.00", invariant),
        }),
    });
});

app.MapGet("/acikbank/accounts/{accountId}/transactions", (string accountId, DateOnly fromDate, DateOnly toDate, HttpRequest request,
    AcikBankAuth auth, BankWorld world, TimeProvider clock) =>
{
    var now = clock.GetUtcNow();
    var (consent, status, error) = auth.Authorize(request.Headers.Authorization, now);
    if (consent is null)
    {
        return OAuthError(status, error!);
    }

    if (BankWorld.AcikAccounts.All(a => a.Id != accountId))
    {
        return OAuthError(404, "account_not_found");
    }

    var bad = world.FaultOf("acikbank") == Fault.BadData;
    return Results.Json(new
    {
        transactions = world.Movements(accountId, now, fromDate, toDate).Select((m, i) => new
        {
            transactionId = m.Id,
            bookingDateTime = new DateTimeOffset(m.LocalTime, BankWorld.Turkey.GetUtcOffset(m.LocalTime)).ToString("yyyy-MM-dd'T'HH:mm:sszzz", invariant),
            amount = new { value = bad && i == 0 ? "12,5O" : Math.Abs(m.Amount).ToString("0.00", invariant), currency = "TRY" },
            creditDebitIndicator = m.Amount > 0 ? "CRDT" : "DBIT",
            remittanceInformation = m.Description,
            counterparty = m.CounterpartyName is null ? null : new { name = m.CounterpartyName, iban = m.CounterpartyIban, taxNumber = m.CounterpartyTaxNumber },
        }),
    });
});

// ---------- KlasikBank: SOAP ----------

app.MapPost("/klasikbank/EkstreServisi.asmx", async (HttpRequest request, BankWorld world, MockBanksOptions options, TimeProvider clock) =>
{
    XElement? body;
    try
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, Async = true };
        using var reader = XmlReader.Create(request.Body, settings);
        body = (await XDocument.LoadAsync(reader, LoadOptions.None, request.HttpContext.RequestAborted)).Root?.Element(soap + "Body");
    }
    catch (XmlException)
    {
        return SoapFault("Gecersiz XML");
    }

    var operation = body?.Elements().FirstOrDefault();
    if (operation is null || request.Headers["SOAPAction"].ToString().Trim('"') != $"http://klasikbank.example/ekstre/{operation.Name.LocalName}")
    {
        return SoapFault("Bilinmeyen islem");
    }

    if (operation.Element(kb + "KullaniciAdi")?.Value != options.KlasikUsername || operation.Element(kb + "Sifre")?.Value != options.KlasikPassword)
    {
        return SoapFault("Kullanici adi veya sifre hatali");
    }

    var now = clock.GetUtcNow();
    if (operation.Name == kb + "HesapListesi")
    {
        return Envelope(new XElement(kb + "HesapListesiResponse", new XElement(kb + "Hesaplar",
            BankWorld.KlasikAccounts.Select(a => new XElement(kb + "Hesap",
                new XElement(kb + "Iban", a.Iban),
                new XElement(kb + "HesapAdi", a.Name),
                new XElement(kb + "DovizKodu", "TL"),
                new XElement(kb + "Bakiye", world.Balance(a, now).ToString("#,##0.00", turkish)))))));
    }

    if (operation.Name != kb + "HesapHareketleriSorgula")
    {
        return SoapFault("Bilinmeyen islem");
    }

    var iban = operation.Element(kb + "Iban")?.Value;
    if (BankWorld.KlasikAccounts.All(a => a.Iban != iban))
    {
        return SoapFault("Hesap bulunamadi");
    }

    if (!DateOnly.TryParseExact(operation.Element(kb + "BaslangicTarihi")?.Value, "yyyy-MM-dd", invariant, DateTimeStyles.None, out var from)
        || !DateOnly.TryParseExact(operation.Element(kb + "BitisTarihi")?.Value, "yyyy-MM-dd", invariant, DateTimeStyles.None, out var to))
    {
        return SoapFault("Tarih formati yyyy-MM-dd olmali");
    }

    var bad = world.FaultOf("klasikbank") == Fault.BadData;
    return Envelope(new XElement(kb + "HesapHareketleriSorgulaResponse", new XElement(kb + "Hareketler",
        world.Movements(iban!, now, from, to).Select((m, i) => new XElement(kb + "Hareket",
            new XElement(kb + "DekontNo", m.Id),
            new XElement(kb + "IslemTarihi", m.LocalTime.ToString("dd.MM.yyyy", invariant)),
            new XElement(kb + "IslemSaati", m.LocalTime.ToString("HH:mm:ss", invariant)),
            new XElement(kb + "Tutar", bad && i == 0 ? "1250.50" : Math.Abs(m.Amount).ToString("#,##0.00", turkish)),
            new XElement(kb + "BorcAlacak", m.Amount > 0 ? "A" : "B"),
            new XElement(kb + "Aciklama", m.Description),
            new XElement(kb + "KarsiAd", m.CounterpartyName),
            new XElement(kb + "KarsiIban", m.CounterpartyIban),
            new XElement(kb + "KarsiVkn", m.CounterpartyTaxNumber))))));
});

// ---------- Test and demo controls ----------

app.MapPost("/mock/{bank}/movements", (string bank, NewMovement body, BankWorld world, TimeProvider clock) =>
{
    var account = bank switch
    {
        "acikbank" => body.AccountId ?? BankWorld.AcikAccounts[0].Id,
        "klasikbank" => BankWorld.KlasikAccounts[0].Id,
        _ => null,
    };
    if (account is null)
    {
        return Results.NotFound();
    }

    var movement = world.Add(account, clock.GetUtcNow(), body.Amount, body.Description ?? "HAVALE GELEN", body.CounterpartyName, body.CounterpartyIban, body.CounterpartyTaxNumber);
    return Results.Json(movement, statusCode: 201);
});

app.MapPost("/mock/{bank}/fault", (string bank, FaultRequest body, BankWorld world) =>
{
    if (bank is not ("acikbank" or "klasikbank") || !Enum.TryParse<Fault>(body.Mode, ignoreCase: true, out var fault))
    {
        return Results.BadRequest("bank is acikbank or klasikbank; mode is none, down, slow or baddata");
    }

    world.SetFault(bank, fault);
    return Results.NoContent();
});

app.MapPost("/mock/acikbank/expire-tokens", (AcikBankAuth auth, TimeProvider clock) =>
{
    auth.ExpireAccessTokens(clock.GetUtcNow());
    return Results.NoContent();
});

app.MapPost("/mock/acikbank/consents/{id}/revoke", (string id, AcikBankAuth auth) =>
    // The customer revokes in the bank's app. Kasa is not told; it finds out on its next call.
    auth.Revoke(id) ? Results.NoContent() : Results.NotFound());

app.Run();

static bool IsClient(HttpRequest request, MockBanksOptions options)
{
    var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.ClientId}:{options.ClientSecret}"));
    return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(request.Headers.Authorization.ToString()), Encoding.UTF8.GetBytes(expected));
}

static IResult OAuthError(int status, string error, string? description = null) =>
    Results.Json(new { error, error_description = description }, statusCode: status);

static IResult Page(int status, string content) => Results.Content(
    "<!doctype html><html lang=\"tr\"><meta charset=\"utf-8\"><title>AcikBank</title>"
    + "<body style=\"font-family:system-ui;max-width:30rem;margin:3rem auto;line-height:1.5\">"
    + $"<h1>AcikBank</h1>{content}<p style=\"color:#777\">Test bankası, örnek veri.</p></body></html>",
    "text/html; charset=utf-8", Encoding.UTF8, status);

// SOAP 1.1 reports errors as a Fault element with HTTP 500.
IResult SoapFault(string message) => Envelope(
    new XElement(soap + "Fault", new XElement("faultcode", "soap:Client"), new XElement("faultstring", message)), 500);

IResult Envelope(XElement content, int statusCode = 200) => Results.Text(
    new XDocument(new XElement(soap + "Envelope", new XAttribute(XNamespace.Xmlns + "soap", soap), new XElement(soap + "Body", content))).ToString(),
    "text/xml; charset=utf-8", statusCode: statusCode);

internal sealed record ConsentRequest(DateTimeOffset ExpiresAt);

internal sealed record NewMovement(decimal Amount, string? Description, string? CounterpartyName, string? CounterpartyIban, string? CounterpartyTaxNumber, string? AccountId);

internal sealed record FaultRequest(string? Mode);
