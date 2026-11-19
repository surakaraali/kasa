using System.Globalization;

namespace Kasa.MockBanks;

public sealed class MockBanksOptions
{
    public string ClientId { get; set; } = "kasa";
    public string ClientSecret { get; set; } = "acikbank-dev-secret";

    /// <summary>Codes are only ever sent to one of these exact addresses.</summary>
    public string[] RedirectUris { get; set; } = [];

    public string KlasikUsername { get; set; } = "kasa";
    public string KlasikPassword { get; set; } = "klasik-dev-pass";

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(5);
}

public enum Fault
{
    None,

    /// <summary>Answers 503 to every call.</summary>
    Down,

    /// <summary>Waits 30 seconds before answering, longer than any client should wait.</summary>
    Slow,

    /// <summary>One movement carries an amount no parser should accept.</summary>
    BadData,
}

/// <param name="Amount">Signed: positive is money coming in.</param>
public sealed record Movement(
    string Id, DateTime LocalTime, decimal Amount, string Description,
    string? CounterpartyName = null, string? CounterpartyIban = null, string? CounterpartyTaxNumber = null);

public sealed record Account(string Id, string Iban, string Name, decimal OpeningBalance);

/// <summary>
/// The customer's money at the two banks. Movements repeat every week on fixed weekdays and keep their ids forever,
/// so fetching a day twice gives the same movements, as at a real bank. Movements later than "now" do not exist yet.
/// Movements added through /mock appear at the moment they are added.
/// </summary>
public sealed class BankWorld
{
    public static readonly TimeZoneInfo Turkey = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    private sealed record Template(DayOfWeek Day, string Time, decimal Amount, string Description,
        string? Name = null, string? Iban = null, string? TaxNumber = null);

    public static readonly Account[] AcikAccounts =
    [
        new("acc-1", "TR090090001000000000000001", "Vadesiz TL", 250_000m),
        new("acc-2", "TR360090001000000000000002", "Tahsilat Hesabı", 40_000m),
    ];

    public static readonly Account[] KlasikAccounts =
    [
        new("TR690090102000000000000003", "TR690090102000000000000003", "Ticari Hesap", 120_000m),
    ];

    private static readonly Dictionary<string, Template[]> Templates = new()
    {
        ["acc-1"] =
        [
            new(DayOfWeek.Monday, "10:12:05", 18450.00m, "FATURA TAHSILATI", "ABC TEKSTİL SANAYİ A.Ş.", "TR340004605555000011112222", "1234567890"),
            new(DayOfWeek.Tuesday, "14:40:00", -7320.75m, "TEDARIKCI ODEMESI", "YILDIZ AMBALAJ LTD. ŞTİ.", "TR410006207777000033334444", "2345678901"),
            new(DayOfWeek.Wednesday, "09:03:17", 2500.00m, "EFT GELEN ÖDEME", "MEHMET ÖZGÜR ÇALIŞKAN", "TR600001001212000034345656"),
            new(DayOfWeek.Thursday, "23:59:59", -49.90m, "HESAP İŞLETİM ÜCRETİ"),
            new(DayOfWeek.Friday, "11:20:30", 990.10m, "CARI HESAP ODEMESI", "DENİZ GIDA PAZARLAMA", null, "9876543210"),
            new(DayOfWeek.Thursday, "08:30:00", 12000.00m, "FATURA TAHSILATI", "ABC TEKSTİL SANAYİ A.Ş.", "TR340004605555000011112222", "1234567890"),
        ],
        ["acc-2"] =
        [
            new(DayOfWeek.Monday, "15:00:00", 6400.00m, "HAVALE GELEN", "ÇINAR MOBİLYA A.Ş.", "TR120006200000000012345678", "4567890123"),
            new(DayOfWeek.Wednesday, "16:45:10", 3150.25m, "FATURA TAHSILATI", "ÇINAR MOBİLYA A.Ş.", "TR120006200000000012345678", "4567890123"),
        ],
        ["TR690090102000000000000003"] =
        [
            // This bank prints IBANs in groups of four.
            new(DayOfWeek.Monday, "16:05:42", 3200.00m, "HAVALE GELEN", "KUZEY LOJİSTİK A.Ş.", "TR56 0006 4099 9900 0055 5566 66", "3456789012"),
            new(DayOfWeek.Tuesday, "09:00:00", -15000.00m, "KİRA ÖDEMESİ", "IŞIK GAYRİMENKUL", "TR56 0001 5042 4200 0012 1234 34"),
            new(DayOfWeek.Wednesday, "13:27:09", 1250.50m, "FATURA ÖDEMESİ", "DENİZ GIDA PAZARLAMA", null, "9876543210"),
            new(DayOfWeek.Thursday, "00:00:01", -3.75m, "EFT MASRAFI"),
            new(DayOfWeek.Friday, "10:45:00", 84999.99m, "TOPLU TAHSILAT", "ABC TEKSTİL SANAYİ A.Ş.", "TR340004605555000011112222", "1234567890"),
        ],
    };

    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<Movement>> _added = [];
    private readonly Dictionary<string, Fault> _faults = new() { ["acikbank"] = Fault.None, ["klasikbank"] = Fault.None };
    private int _nextAdded;

    public Fault FaultOf(string bank)
    {
        lock (_gate)
        {
            return _faults[bank];
        }
    }

    public void SetFault(string bank, Fault fault)
    {
        lock (_gate)
        {
            _faults[bank] = fault;
        }
    }

    /// <summary>A movement that happens now: for the demo ("a payment just came in") and for tests.</summary>
    public Movement Add(string accountId, DateTimeOffset now, decimal amount, string description, string? name, string? iban, string? taxNumber)
    {
        lock (_gate)
        {
            var local = TimeZoneInfo.ConvertTime(now, Turkey).DateTime;
            var movement = new Movement(string.Create(CultureInfo.InvariantCulture, $"LIVE-{local:yyyyMMddHHmmss}-{++_nextAdded:0000}"),
                local, amount, description, name, iban, taxNumber);
            if (!_added.TryGetValue(accountId, out var list))
            {
                _added[accountId] = list = [];
            }

            list.Add(movement);
            return movement;
        }
    }

    public List<Movement> Movements(string accountId, DateTimeOffset now, DateOnly from, DateOnly to)
    {
        var localNow = TimeZoneInfo.ConvertTime(now, Turkey).DateTime;
        var result = new List<Movement>();
        var templates = Templates.GetValueOrDefault(accountId, []);
        var prefix = accountId.Length > 8 ? "KB" : accountId.ToUpperInvariant().Replace("-", "");

        for (var day = from; day <= to && day <= DateOnly.FromDateTime(localNow); day = day.AddDays(1))
        {
            for (var i = 0; i < templates.Length; i++)
            {
                var t = templates[i];
                var time = day.ToDateTime(TimeOnly.ParseExact(t.Time, "HH:mm:ss", CultureInfo.InvariantCulture));
                if (t.Day == day.DayOfWeek && time <= localNow)
                {
                    result.Add(new Movement(string.Create(CultureInfo.InvariantCulture, $"{prefix}-{day:yyyyMMdd}-{i + 1:000}"),
                        time, t.Amount, t.Description, t.Name, t.Iban, t.TaxNumber));
                }
            }
        }

        lock (_gate)
        {
            result.AddRange(_added.GetValueOrDefault(accountId, [])
                .Where(m => DateOnly.FromDateTime(m.LocalTime) >= from && DateOnly.FromDateTime(m.LocalTime) <= to));
        }

        // A statement is in time order.
        return [.. result.OrderBy(m => m.LocalTime)];
    }

    /// <summary>Opening balance plus every movement up to now, so the balance agrees with the statement.</summary>
    public decimal Balance(Account account, DateTimeOffset now) =>
        account.OpeningBalance + Movements(account.Id, now, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Turkey).DateTime).AddDays(-60),
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Turkey).DateTime)).Sum(m => m.Amount);
}
