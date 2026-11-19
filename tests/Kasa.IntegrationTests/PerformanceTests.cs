using System.Buffers.Text;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kasa.Api.Assistant;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Kasa.IntegrationTests;

/// <summary>Runs only with KASA_PERF=1: it loads half a million rows and takes minutes.</summary>
public sealed class PerfFactAttribute : FactAttribute
{
    public PerfFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("KASA_PERF") != "1")
        {
            Skip = "Performance run: set KASA_PERF=1";
        }
    }
}

[Collection("Kasa")]
public sealed class PerformanceTests(KasaFixture fixture, ITestOutputHelper output)
{
    private const int Rows = 500_000;

    [PerfFact]
    public async Task Half_a_million_movements()
    {
        await using var banks = new MockBanksFactory();
        await using var api = new KasaApiFactory(fixture, banks: banks);
        var admin = await api.RegisterAsync();
        await admin.ConnectAsync(banks, "acikbank");
        await admin.ConnectAsync(banks, "klasikbank");
        var me = await admin.Client.GetFromJsonAsync<JsonElement>("/api/me");
        var companyId = me.GetProperty("companyId").GetGuid();
        var accounts = (await admin.Client.GetFromJsonAsync<JsonElement[]>("/api/accounts"))!.Select(a => a.GetProperty("id").GetGuid()).ToArray();

        var load = Stopwatch.StartNew();
        await BulkInsertAsync(companyId, accounts);
        output.WriteLine($"Loaded {Rows:N0} movements in {load.Elapsed.TotalSeconds:0.0} s");

        var results = new List<(string What, double Ms)>();
        async Task Measure(string what, Func<Task> work, int runs = 7)
        {
            await work(); // warm-up: plan cache, connection pool
            var times = new List<double>();
            for (var i = 0; i < runs; i++)
            {
                var watch = Stopwatch.StartNew();
                await work();
                times.Add(watch.Elapsed.TotalMilliseconds);
            }

            times.Sort();
            results.Add((what, times[times.Count / 2]));
        }

        Task Get(string path) => admin.Client.GetAsync(path).ContinueWith(t => t.Result.EnsureSuccessStatusCode());

        await Measure("Hareketler, ilk sayfa (50)", () => Get("/api/transactions?limit=50"));
        var deep = await CursorAtAsync(companyId, 250_000);
        await Measure("Hareketler, 250.000. satırdan sonraki sayfa (keyset)", () => Get($"/api/transactions?limit=50&cursor={deep}"));
        await Measure("Aynı sayfa OFFSET ile (yalnız SQL)", () => OffsetPageAsync(companyId, 250_000));
        await Measure("Aynı sayfa keyset ile (yalnız SQL)", () => KeysetPageAsync(companyId, 250_000));
        await Measure("Eşleşmeyenler filtresi", () => Get("/api/transactions?limit=50&match=Unmatched"));
        await Measure("Metin araması \"ABC\" (LIKE)", () => Get("/api/transactions?limit=50&search=ABC"));
        var days = 7;
        await Measure("Panel, önbelleksiz (her seferinde farklı anahtar)", () => Get($"/api/dashboard?days={++days}"));
        await Measure("Panel, önbellekten", () => Get("/api/dashboard?days=30"));
        await Measure("Asistan aracı: bir yılın arama toplamları", async () =>
        {
            using var scope = api.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<Kasa.Api.Common.TenantContext>().Use(companyId);
            await scope.ServiceProvider.GetRequiredService<AssistantTools>().RunAsync("search_transactions", new Dictionary<string, JsonElement>
            {
                ["from"] = JsonSerializer.SerializeToElement(DateTime.UtcNow.AddYears(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ["direction"] = JsonSerializer.SerializeToElement("in"),
            }, CancellationToken.None);
        });

        output.WriteLine("| Ölçüm | Ortanca (ms) |");
        output.WriteLine("|---|---|");
        foreach (var (what, ms) in results)
        {
            output.WriteLine($"| {what} | {ms:0.0} |");
        }

        Assert.True(results.Single(r => r.What.StartsWith("Hareketler, ilk", StringComparison.Ordinal)).Ms < 500);
    }

    /// <summary>Two years of movements over the company's accounts, a third matched, like a busy company's history.</summary>
    private async Task BulkInsertAsync(Guid companyId, Guid[] accounts)
    {
        var random = new Random(42);
        string[] names = ["ABC TEKSTİL SANAYİ A.Ş.", "YILDIZ AMBALAJ LTD. ŞTİ.", "DENİZ GIDA PAZARLAMA", "ÇINAR MOBİLYA A.Ş.", "KUZEY LOJİSTİK A.Ş.", "IŞIK GAYRİMENKUL"];
        string[] descriptions = ["FATURA TAHSILATI", "HAVALE GELEN", "TEDARIKCI ODEMESI", "EFT GELEN ÖDEME", "KİRA ÖDEMESİ", "CARI HESAP ODEMESI"];
        var start = DateTimeOffset.UtcNow.AddYears(-2);
        await using var connection = new SqlConnection(fixture.Sql);
        await connection.OpenAsync();
        const int batch = 50_000;
        for (var offset = 0; offset < Rows; offset += batch)
        {
            using var table = new DataTable();
            foreach (var (name, type) in new (string, Type)[]
            {
                ("CompanyId", typeof(Guid)), ("BankAccountId", typeof(Guid)), ("BankReference", typeof(string)), ("BookedAt", typeof(DateTimeOffset)),
                ("Amount", typeof(decimal)), ("Description", typeof(string)), ("CounterpartyName", typeof(string)), ("ImportedAt", typeof(DateTimeOffset)),
                ("MatchStatus", typeof(string)), ("ExportStatus", typeof(string)),
            })
            {
                table.Columns.Add(name, type);
            }

            for (var i = offset; i < offset + batch; i++)
            {
                var amount = Math.Round((decimal)(random.NextDouble() * 50_000), 2) * (random.Next(3) == 0 ? -1 : 1);
                table.Rows.Add(companyId, accounts[i % accounts.Length], $"PERF-{i:0000000}", start.AddSeconds(i * 126L),
                    amount == 0 ? 1 : amount, descriptions[random.Next(descriptions.Length)], names[random.Next(names.Length)], DateTimeOffset.UtcNow,
                    i % 3 == 0 ? "Matched" : "Unmatched", i % 3 == 0 ? "Exported" : "None");
            }

            using var bulk = new SqlBulkCopy(connection) { DestinationTableName = "BankTransactions", BatchSize = 10_000, BulkCopyTimeout = 300 };
            foreach (DataColumn column in table.Columns)
            {
                bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            }

            await bulk.WriteToServerAsync(table);
        }

        await using var update = new SqlCommand("UPDATE STATISTICS BankTransactions", connection);
        await update.ExecuteNonQueryAsync();
    }

    private async Task<string> CursorAtAsync(Guid companyId, int position)
    {
        await using var db = fixture.CreateDbContext(companyId);
        var row = await db.BankTransactions.OrderByDescending(t => t.BookedAt).ThenByDescending(t => t.Id).Skip(position).Select(t => new { t.BookedAt, t.Id }).FirstAsync();
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{row.BookedAt.UtcTicks}.{row.Id}"));
    }

    private async Task OffsetPageAsync(Guid companyId, int position)
    {
        await using var db = fixture.CreateDbContext(companyId);
        await db.BankTransactions.OrderByDescending(t => t.BookedAt).ThenByDescending(t => t.Id).Skip(position).Take(50).Select(t => t.Id).ToListAsync();
    }

    private async Task KeysetPageAsync(Guid companyId, int position)
    {
        // The cursor values are what the previous page handed out; finding them is not part of the page's cost.
        await using var db = fixture.CreateDbContext(companyId);
        _anchor ??= await db.BankTransactions.OrderByDescending(t => t.BookedAt).ThenByDescending(t => t.Id).Skip(position).Select(t => new Anchor(t.BookedAt, t.Id)).FirstAsync();
        var (at, id) = (_anchor.At, _anchor.Id);
        await db.BankTransactions.Where(t => t.BookedAt < at || (t.BookedAt == at && t.Id < id))
            .OrderByDescending(t => t.BookedAt).ThenByDescending(t => t.Id).Take(50).Select(t => t.Id).ToListAsync();
    }

    private Anchor? _anchor;

    private sealed record Anchor(DateTimeOffset At, long Id);
}
