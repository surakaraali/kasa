using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;

namespace Kasa.IntegrationTests;

/// <summary>The two test banks in memory. Each test gets its own, so faults and added movements never leak between tests.</summary>
public sealed class MockBanksFactory : WebApplicationFactory<Kasa.MockBanks.AssemblyMarker>
{
    public const string KlasikUsername = "kasa";
    public const string KlasikPassword = "klasik-dev-pass";

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development");

    public async Task SetFaultAsync(string bank, string mode) =>
        await (await CreateClient().PostAsJsonAsync($"/mock/{bank}/fault", new { mode })).EnsureOkAsync();

    /// <summary>A movement that happens at the bank now, as the demo does.</summary>
    public async Task<string> AddMovementAsync(string bank, decimal amount, string description = "HAVALE GELEN")
    {
        var response = await CreateClient().PostAsJsonAsync($"/mock/{bank}/movements", new { amount, description });
        await response.EnsureOkAsync();
        return (await response.JsonAsync()).GetProperty("id").GetString()!;
    }
}

public static class BankKit
{
    /// <summary>
    /// The whole open banking flow as a browser does it: Kasa starts, the browser opens the bank's page, the customer
    /// answers, the bank redirects the browser back to Kasa's callback. Returns the callback's response and the consent id.
    /// </summary>
    public static async Task<(HttpResponseMessage Callback, string ConsentId)> ConnectAcikBankAsync(
        this Session session, MockBanksFactory banks, bool approve = true, HttpClient? returningBrowser = null)
    {
        var start = await session.Client.PostAsync("/api/bank-connections/acikbank", null);
        await start.EnsureOkAsync();
        var authorizationUrl = new Uri((await start.JsonAsync()).GetProperty("authorizationUrl").GetString()!);
        var consentId = QueryHelpers.ParseQuery(authorizationUrl.Query)["consent_id"].ToString();

        var bankBrowser = banks.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await (await bankBrowser.GetAsync(authorizationUrl.PathAndQuery)).EnsureOkAsync();
        var answer = await bankBrowser.PostAsync("/acikbank/authorize", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["consent_id"] = consentId,
            ["decision"] = approve ? "approve" : "reject",
        }));
        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);

        var callback = await (returningBrowser ?? session.Client).GetAsync(answer.Headers.Location!.PathAndQuery);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        return (callback, consentId);
    }

    public static async Task<Guid> ConnectAsync(this Session session, MockBanksFactory banks, string bank)
    {
        if (bank == "acikbank")
        {
            var (callback, _) = await session.ConnectAcikBankAsync(banks);
            Assert.EndsWith("sonuc=baglandi", callback.Headers.Location!.ToString(), StringComparison.Ordinal);
        }
        else
        {
            var response = await session.Client.PostAsJsonAsync("/api/bank-connections/klasikbank",
                new { username = MockBanksFactory.KlasikUsername, password = MockBanksFactory.KlasikPassword });
            await response.EnsureOkAsync();
        }

        return (await session.ConnectionsAsync()).Single().GetProperty("id").GetGuid();
    }

    public static async Task<JsonElement[]> ConnectionsAsync(this Session session) =>
        (await session.Client.GetFromJsonAsync<JsonElement[]>("/api/bank-connections"))!;

    public static async Task<JsonElement> SyncAsync(this Session session, Guid connectionId)
    {
        var response = await session.Client.PostAsync($"/api/bank-connections/{connectionId}/sync", null);
        await response.EnsureOkAsync();
        return await response.JsonAsync();
    }

    /// <summary>Every movement the company can see, through the paged list.</summary>
    public static async Task<List<JsonElement>> AllTransactionsAsync(this Session session, int pageSize = 200)
    {
        var all = new List<JsonElement>();
        string? cursor = null;
        do
        {
            var page = (await session.Client.GetFromJsonAsync<JsonElement>(
                $"/api/transactions?limit={pageSize}{(cursor is null ? "" : $"&cursor={cursor}")}"))!;
            all.AddRange(page.GetProperty("items").EnumerateArray());
            cursor = page.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        return all;
    }
}
