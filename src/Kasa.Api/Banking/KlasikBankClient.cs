using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Kasa.Api.Domain;
using Kasa.Api.Infrastructure;

namespace Kasa.Api.Banking;

public sealed class KlasikBankOptions
{
    [Required, Url]
    public string BaseUrl { get; set; } = "";
}

/// <summary>
/// SOAP 1.1 client for KlasikBank, by hand with HttpClient and LINQ to XML: one POST with a SOAPAction header and an
/// XML envelope, and an envelope back. The bank speaks Turkish formats: 31.12.2026, 1.250,50, B/A for debit/credit.
/// </summary>
public sealed partial class KlasikBankClient(HttpClient http)
{
    private const BankCode Bank = BankCode.KlasikBank;
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Kb = "http://klasikbank.example/ekstre";

    public async Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(string username, string password, RawResponseTag? tag, CancellationToken ct)
    {
        var result = await CallAsync("HesapListesi", [new XElement(Kb + "KullaniciAdi", username), new XElement(Kb + "Sifre", password)], tag, ct);
        return result.Descendants(Kb + "Hesap").Select((h, i) =>
        {
            var iban = BankData.Iban(h.Element(Kb + "Iban")?.Value) ?? throw new BankApiException(Bank, BankErrorKind.InvalidData, $"account {i}: IBAN missing");
            var balance = h.Element(Kb + "Bakiye")?.Value;
            return new AccountInfo(iban, iban, h.Element(Kb + "HesapAdi")?.Value ?? iban, h.Element(Kb + "DovizKodu")?.Value == "TL" ? "TRY" : "TRY",
                balance is not null && TurkishAmount().IsMatch(balance.TrimStart('-')) ? ParseTurkish(balance.TrimStart('-')) * (balance.StartsWith('-') ? -1 : 1) : null);
        }).ToList();
    }

    public async Task<IReadOnlyList<StatementLine>> GetTransactionsAsync(string username, string password, string iban, DateOnly from, DateOnly to, RawResponseTag tag, CancellationToken ct)
    {
        var result = await CallAsync("HesapHareketleriSorgula",
        [
            new XElement(Kb + "KullaniciAdi", username),
            new XElement(Kb + "Sifre", password),
            new XElement(Kb + "Iban", iban),
            new XElement(Kb + "BaslangicTarihi", from.ToString("O", CultureInfo.InvariantCulture)),
            new XElement(Kb + "BitisTarihi", to.ToString("O", CultureInfo.InvariantCulture)),
        ], tag, ct);
        return result.Descendants(Kb + "Hareket").Select(ToLine).ToList();
    }

    private async Task<XElement> CallAsync(string operation, XElement[] fields, RawResponseTag? tag, CancellationToken ct)
    {
        // XElement escapes the values, so a password or IBAN containing < or & cannot break the XML.
        var envelope = new XElement(Soap + "Envelope", new XAttribute(XNamespace.Xmlns + "soap", Soap),
            new XElement(Soap + "Body", new XElement(Kb + operation, fields)));
        using var request = new HttpRequestMessage(HttpMethod.Post, "EkstreServisi.asmx")
        {
            Content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml"),
        };
        request.Headers.Add("SOAPAction", $"\"http://klasikbank.example/ekstre/{operation}\"");
        if (tag is not null)
        {
            request.Options.Set(RawResponseTag.Key, tag);
        }

        using var response = await BankData.SendAsync(http, request, Bank, ct);
        // A SOAP fault comes with HTTP 500, so the body is read before the status is judged.
        var text = await response.Content.ReadAsStringAsync(ct);
        if ((int)response.StatusCode is >= 502 and <= 504)
        {
            throw new BankApiException(Bank, BankErrorKind.Unavailable, $"HTTP {(int)response.StatusCode}");
        }

        XDocument document;
        try
        {
            document = ParseWithoutDtd(text);
        }
        catch (XmlException ex)
        {
            throw new BankApiException(Bank, BankErrorKind.InvalidData, $"HTTP {(int)response.StatusCode}, the response is not acceptable XML", ex);
        }

        var body = document.Root?.Element(Soap + "Body");
        if (body?.Element(Soap + "Fault") is { } fault)
        {
            var message = fault.Element("faultstring")?.Value ?? "SOAP fault";
            var kind = message.Contains("sifre", StringComparison.OrdinalIgnoreCase) ? BankErrorKind.Rejected : BankErrorKind.InvalidData;
            throw new BankApiException(Bank, kind, $"SOAP fault: {message}");
        }

        return body?.Element(Kb + $"{operation}Response") is { } result && response.IsSuccessStatusCode
            ? result
            : throw new BankApiException(Bank, BankErrorKind.InvalidData, $"HTTP {(int)response.StatusCode}, no {operation}Response element");
    }

    /// <summary>A statement never needs a DTD; refusing one blocks XXE and "billion laughs" attacks.</summary>
    private static XDocument ParseWithoutDtd(string xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        return XDocument.Load(reader);
    }

    private static StatementLine ToLine(XElement hareket, int index)
    {
        string? Field(string name) => hareket.Element(Kb + name)?.Value;

        // B = borç (money out), A = alacak (money in).
        var sign = Field("BorcAlacak") switch
        {
            "A" => 1,
            "B" => -1,
            _ => throw BankData.Invalid(Bank, index, "BorcAlacak must be A or B"),
        };

        // Turkish format: 1.250,50. Parsing it loosely would accept "1250.50" too and read it as 125050.
        var amountText = Field("Tutar");
        if (amountText is null || !TurkishAmount().IsMatch(amountText))
        {
            throw BankData.Invalid(Bank, index, "the amount (Tutar) must look like 1.250,50");
        }

        if (!DateTime.TryParseExact($"{Field("IslemTarihi")} {Field("IslemSaati")}", "dd.MM.yyyy HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            throw BankData.Invalid(Bank, index, "IslemTarihi and IslemSaati must look like 31.12.2026 and 23:59:59");
        }

        // The bank sends Turkish local time without an offset.
        var bookedAt = new DateTimeOffset(local, BankData.Turkey.GetUtcOffset(local));
        return BankData.Line(Bank, index, Field("DekontNo"), bookedAt, sign * ParseTurkish(amountText),
            Field("Aciklama"), Field("KarsiAd"), Field("KarsiIban"), Field("KarsiVkn"));
    }

    private static decimal ParseTurkish(string text) => BankData.PlainAmount(text.Replace(".", "", StringComparison.Ordinal).Replace(',', '.'));

    [GeneratedRegex(@"^[0-9]{1,3}(\.[0-9]{3}){0,5},[0-9]{2}$")]
    private static partial Regex TurkishAmount();
}
