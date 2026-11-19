namespace Kasa.MockErp;

public sealed class MockErpOptions
{
    public string ApiKey { get; set; } = "erp-dev-key";
}

public enum Fault
{
    None,

    /// <summary>Answers 503 to every call.</summary>
    Down,

    /// <summary>Waits 30 seconds before answering.</summary>
    Slow,

    /// <summary>Writes the voucher, then never answers: the caller times out not knowing it worked.</summary>
    LoseResponse,
}

public sealed record CurrentAccount(string Code, string Title, string? TaxNumber, string? Iban);

public sealed record VoucherRequest(string? Type, DateOnly Date, decimal Amount, string? CurrentAccountCode, string? BankIban, string? Description, string? Reference);

public sealed record Voucher(string VoucherNo, string Firm, string Type, DateOnly Date, decimal Amount, string CurrentAccountCode,
    string CurrentAccountTitle, string BankIban, string Description, string Reference, string IdempotencyKey, DateTimeOffset CreatedAt);

/// <summary>
/// One firm's books in the style of a typical Turkish ERP: current accounts (cari kartlar) and the collection and payment
/// vouchers (tahsilat / tediye fişleri) written to them. The current accounts are chosen so that every matching
/// rule shows up with the test banks' movements: by IBAN, by tax number, a conflict, and no match at all.
/// </summary>
public sealed class ErpWorld
{
    public const string Firm = "001";

    public static readonly CurrentAccount[] CurrentAccounts =
    [
        // By IBAN.
        new("120.01.001", "ABC TEKSTİL SANAYİ A.Ş.", "1234567890", "TR340004605555000011112222"),
        new("120.01.003", "ÇINAR MOBİLYA A.Ş.", "4567890123", "TR120006200000000012345678"),
        // By tax number: the card has no IBAN.
        new("320.01.001", "YILDIZ AMBALAJ LTD. ŞTİ.", "2345678901", null),
        new("120.01.002", "DENİZ GIDA PAZARLAMA", "9876543210", null),
        // A conflict: the bank's IBAN points to one card, its tax number to another. Left for a person to decide.
        new("120.01.004", "KUZEY LOJİSTİK A.Ş.", "3456789099", "TR560006409999000055556666"),
        new("120.01.005", "KUZEY LOJİSTİK TAŞIMACILIK", "3456789012", null),
        // Nothing to match on: only a person can match the rent to this card.
        new("320.01.002", "IŞIK GAYRİMENKUL", null, null),
    ];

    private readonly Lock _gate = new();
    private readonly List<Voucher> _vouchers = [];
    private readonly Dictionary<string, (VoucherRequest Request, Voucher Voucher)> _byKey = [];
    private Fault _fault;

    public Fault Fault
    {
        get { lock (_gate) { return _fault; } }
        set { lock (_gate) { _fault = value; } }
    }

    public IReadOnlyList<Voucher> Vouchers()
    {
        lock (_gate)
        {
            return [.. _vouchers];
        }
    }

    /// <summary>
    /// The same key with the same request returns the voucher made the first time (Replayed); the same key with a
    /// different request is a client bug and is refused, like Stripe does.
    /// </summary>
    public (Voucher? Voucher, bool Replayed, string? Error) Create(string key, VoucherRequest request, CurrentAccount account, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out var earlier))
            {
                return earlier.Request == request ? (earlier.Voucher, true, null) : (null, false, "Idempotency-Key başka bir istekle kullanılmış");
            }

            var prefix = request.Type == "Tahsilat" ? "TH" : "TD";
            var voucher = new Voucher($"{prefix}{_vouchers.Count + 1:000000}", Firm, request.Type!, request.Date, request.Amount, account.Code, account.Title,
                request.BankIban!, request.Description ?? "", request.Reference ?? "", key, now);
            _vouchers.Add(voucher);
            _byKey[key] = (request, voucher);
            return (voucher, false, null);
        }
    }
}
