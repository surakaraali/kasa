using System.Net.Http.Json;
using System.Text.Json;
using Kasa.ErpExporter;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Kasa.IntegrationTests;

/// <summary>The test ERP in memory.</summary>
public sealed class MockErpFactory : WebApplicationFactory<Kasa.MockErp.AssemblyMarker>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development");

    public async Task SetFaultAsync(string mode) =>
        await (await CreateClient().PostAsJsonAsync("/mock/fault", new { mode })).EnsureOkAsync();

    public async Task<JsonElement[]> VouchersAsync()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "erp-dev-key");
        return (await client.GetFromJsonAsync<JsonElement[]>("/api/firms/001/vouchers"))!;
    }
}

/// <summary>Kasa.ErpExporter in memory, reading the test's queues and writing to the test ERP.</summary>
public sealed class ExporterFactory(KasaFixture fixture, MockErpFactory? erp = null, string? rabbitPrefix = null, int maxAttempts = 3)
    : WebApplicationFactory<Kasa.ErpExporter.AssemblyMarker>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        fixture.UseRabbit(builder, rabbitPrefix);
        builder.UseSetting("Erp:MaxAttempts", maxAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("Erp:Timeout", "00:00:02");
        if (erp is not null)
        {
            builder.ConfigureTestServices(services =>
                services.AddHttpClient<ErpClient>().ConfigurePrimaryHttpMessageHandler(() => erp.Server.CreateHandler()));
        }
    }

    /// <summary>The host starts on first use; this starts it, so it begins consuming.</summary>
    public ExporterFactory Started()
    {
        _ = Server;
        return this;
    }
}

/// <summary>Everything one ERP test needs, on its own queues: banks, ERP, API and (unless asked not to) the exporter.</summary>
public sealed class ErpRig : IAsyncDisposable
{
    public string Prefix { get; } = $"kasa-test-{Guid.NewGuid():N}-";
    public MockBanksFactory Banks { get; } = new();
    public MockErpFactory Erp { get; } = new();
    public KasaApiFactory Api { get; }
    public ExporterFactory? Exporter { get; private set; }
    private readonly KasaFixture _fixture;
    private readonly int _maxAttempts;

    public ErpRig(KasaFixture fixture, bool startExporter = true, int maxAttempts = 3)
    {
        (_fixture, _maxAttempts) = (fixture, maxAttempts);
        fixture.ForgetPendingOutbox();
        Api = new KasaApiFactory(fixture, banks: Banks, erp: Erp, rabbitPrefix: Prefix);
        if (startExporter)
        {
            StartExporter();
        }
    }

    public void StartExporter() => Exporter = new ExporterFactory(_fixture, Erp, Prefix, _maxAttempts).Started();

    /// <summary>A company with its ERP firm set and the ERP's current accounts imported.</summary>
    public async Task<Session> CompanyWithErpAsync()
    {
        var admin = await Api.RegisterAsync();
        await (await admin.Client.PutAsJsonAsync("/api/company/erp", new { firmCode = "001" })).EnsureOkAsync();
        await (await admin.Client.PostAsync("/api/current-accounts/import", null)).EnsureOkAsync();
        return admin;
    }

    public async ValueTask DisposeAsync()
    {
        if (Exporter is not null)
        {
            await Exporter.DisposeAsync();
        }

        await Api.DisposeAsync();
        await Erp.DisposeAsync();
        await Banks.DisposeAsync();
    }
}

public static class ErpKit
{
    public static async Task<List<JsonElement>> TransactionsAsync(this Session session, string query) =>
        (await session.Client.GetFromJsonAsync<JsonElement>($"/api/transactions?limit=200&{query}")).GetProperty("items").EnumerateArray().ToList();

    public static async Task<JsonElement> TransactionAsync(this Session session, long id) =>
        (await session.AllTransactionsAsync()).Single(t => t.GetProperty("id").GetInt64() == id);

    public static async Task<Guid> CurrentAccountIdAsync(this Session session, string code) =>
        (await session.Client.GetFromJsonAsync<JsonElement[]>("/api/current-accounts"))!.Single(c => c.GetProperty("code").GetString() == code).GetProperty("id").GetGuid();

    public static Task<HttpResponseMessage> MatchAsync(this Session session, JsonElement transaction, Guid? currentAccountId) =>
        session.Client.PutAsJsonAsync($"/api/transactions/{transaction.GetProperty("id").GetInt64()}/match",
            new { currentAccountId, rowVersion = transaction.GetProperty("rowVersion").GetString() });

    /// <summary>Waits until nothing of this company is on its way to the ERP.</summary>
    public static Task WaitForExportsAsync(this Session session, int seconds = 20) =>
        Until(async () => (await session.TransactionsAsync("export=Pending")).Count == 0, seconds);

    public static async Task Until(Func<Task<bool>> condition, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Condition not met within {seconds} seconds");
            }

            await Task.Delay(100);
        }
    }
}
