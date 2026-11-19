using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kasa.Api.Banking;
using Kasa.Api.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kasa.IntegrationTests;

[Collection("Kasa")]
public sealed class BankingTests(KasaFixture fixture)
{
    [Fact]
    public async Task The_statement_opens_in_excel_and_a_description_cannot_run_as_a_formula()
    {
        await using var banks = new MockBanksFactory();
        await using var api = new KasaApiFactory(fixture, banks: banks);
        var admin = await api.RegisterAsync();
        await banks.AddMovementAsync("acikbank", 1250.5m, "=HYPERLINK(\"http://example.test\",\"Faturayı gör\")");
        await admin.ConnectAsync(banks, "acikbank");

        var response = await admin.Client.GetAsync("/api/transactions/export?direction=In");
        await response.EnsureOkAsync();
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("attachment; filename=\"kasa-hesap-ozeti-", response.Content.Headers.ContentDisposition?.ToString(), StringComparison.Ordinal);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]); // without the BOM Excel shows "ş" as "ÅŸ"
        var text = System.Text.Encoding.UTF8.GetString(bytes[3..]);
        Assert.DoesNotContain("\n", text.Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length > 2, "a header and the account's incoming movements");
        Assert.StartsWith("Tarih;Hesap;IBAN;Karşı taraf;Açıklama;Tutar", lines[0], StringComparison.Ordinal);
        var row = Assert.Single(lines, l => l.Contains("HYPERLINK", StringComparison.Ordinal));
        Assert.Contains(";\"'=HYPERLINK(\"\"http://example.test\"\",\"\"Faturayı gör\"\")\";1250,50;TRY;", row, StringComparison.Ordinal);
        // The direction filter applies, as in the list.
        Assert.DoesNotContain(lines.Skip(1), l => l.Split(';')[5].StartsWith('-'));
    }

    [Fact]
    public async Task KlasikBank_connects_imports_and_a_second_sync_imports_only_what_is_new()
    {
        await using var banks = new MockBanksFactory();
        await using var api = new KasaApiFactory(fixture, banks: banks);
        var admin = await api.RegisterAsync();

        var connectionId = await admin.ConnectAsync(banks, "klasikbank");

        var connection = (await admin.ConnectionsAsync()).Single();
        Assert.Equal("Active", connection.GetProperty("status").GetString());
        Assert.Equal("Succeeded", connection.GetProperty("lastSyncOutcome").GetString());
        var account = connection.GetProperty("accounts").EnumerateArray().Single();
        Assert.Equal("TR690090102000000000000003", account.GetProperty("iban").GetString());
        Assert.True(account.GetProperty("balance").GetDecimal() != 0);
        var first = await admin.AllTransactionsAsync();
        Assert.NotEmpty(first);

        Assert.Equal(0, (await admin.SyncAsync(connectionId)).GetProperty("imported").GetInt32());

        var reference = await banks.AddMovementAsync("klasikbank", 1250.50m, "FATURA TAHSILATI");
        var second = await admin.SyncAsync(connectionId);
        Assert.Equal("Succeeded", second.GetProperty("outcome").GetString());
        Assert.Equal(1, second.GetProperty("imported").GetInt32());
        var added = (await admin.AllTransactionsAsync()).Single(t => t.GetProperty("bankReference").GetString() == reference);
        // Sent by the bank as "1.250,50" with BorcAlacak "A".
        Assert.Equal(1250.50m, added.GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task KlasikBank_with_a_wrong_password_is_refused_and_nothing_is_stored()
    {
        await using var banks = new MockBanksFactory();
        await using var api = new KasaApiFactory(fixture, banks: banks);
        var admin = await api.RegisterAsync();

        var response = await admin.Client.PostAsJsonAsync("/api/bank-connections/klasikbank", new { username = "kasa", password = "wrong" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await admin.ConnectionsAsync());
    }

    [Fact]
    public async Task AcikBank_consent_flow_connects_imports_and_keeps_the_tokens_encrypted()
    {
        await using var banks = new MockBanksFactory();
        await using var api = new KasaApiFactory(fixture, banks: banks);
        var admin = await api.RegisterAsync();

        var (callback, _) = await admin.ConnectAcikBankAsync(banks);

        Assert.Equal("http://localhost:3000/baglantilar?sonuc=baglandi", callback.Headers.Location!.ToString());
        var connection = (await admin.ConnectionsAsync()).Single();
        Assert.Equal("Active", connection.GetProperty("status").GetString());
        Assert.Equal(2, connection.GetProperty("accounts").GetArrayLength());
        Assert.NotEmpty(await admin.AllTransactionsAsync());

        await using var db = fixture.CreateDbContext();
        var row = await db.BankConnections.IgnoreQueryFilters().SingleAsync(c => c.Id == connection.GetProperty("id").GetGuid());
        // "CfDJ8" starts every ASP.NET Core Data Protection payload: what is stored is ciphertext, not the token.
        Assert.StartsWith("CfDJ8", row.AccessTokenProtected, StringComparison.Ordinal);
        Assert.StartsWith("CfDJ8", row.RefreshTokenProtected, StringComparison.Ordinal);
        Assert.Null(row.StateHash);
        Assert.Null(row.CodeVerifierProtected);
    }

    [Fact]
    public async Task AcikBank_rejected_by_the_customer_leaves_a_rejected_connection()
    {
        await using var banks = new MockBanksFactory();
        await using var api = new KasaApiFactory(fixture, banks: banks);
        var admin = await api.RegisterAsync();

        var (callback, _) = await admin.ConnectAcikBankAsync(banks, approve: false);

        Assert.EndsWith("sonuc=reddedildi", callback.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Equal("Rejected", (await admin.ConnectionsAsync()).Single().GetProperty("status").GetString());
    }

    [Fact]
    public async Task AcikBank_callback_in_a_browser_that_did_not_start_the_flow_is_refused()
    {
        await using var banks = new MockBanksFactory();
        await using var api = new KasaApiFactory(fixture, banks: banks);
        var attacker = await api.RegisterAsync();

        // The attacker starts a connection for their company and gets someone else to approve it at the bank.
        var victimBrowser = api.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var (callback, _) = await attacker.ConnectAcikBankAsync(banks, returningBrowser: victimBrowser);

        Assert.EndsWith("sonuc=hata", callback.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Equal("Pending", (await attacker.ConnectionsAsync()).Single().GetProperty("status").GetString());
        Assert.Empty(await attacker.AllTransactionsAsync());
    }

    [Fact]
    public async Task An_access_token_the_bank_no_longer_accepts_is_refreshed_and_the_sync_carries_on()
    {
        await using var banks = new MockBanksFactory();
        await using var api = new KasaApiFactory(fixture, banks: banks);
        var admin = await api.RegisterAsync();
        var connectionId = await admin.ConnectAsync(banks, "acikbank");
        await using var before = fixture.CreateDbContext();
        var oldRefresh = (await before.BankConnections.IgnoreQueryFilters().SingleAsync(c => c.Id == connectionId)).RefreshTokenProtected;

        await (await banks.CreateClient().PostAsync("/mock/acikbank/expire-tokens", null)).EnsureOkAsync();
        await banks.AddMovementAsync("acikbank", 990m);
        var result = await admin.SyncAsync(connectionId);

        Assert.Equal("Succeeded", result.GetProperty("outcome").GetString());
        Assert.Equal(1, result.GetProperty("imported").GetInt32());
        await using var after = fixture.CreateDbContext();
        Assert.NotEqual(oldRefresh, (await after.BankConnections.IgnoreQueryFilters().SingleAsync(c => c.Id == connectionId)).RefreshTokenProtected);
    }

    [Fact]
    public async Task A_consent_revoked_at_the_bank_asks_for_a_new_one_and_reconnecting_keeps_the_accounts()
    {
        await using var banks = new MockBanksFactory();
        await using var api = new KasaApiFactory(fixture, banks: banks);
        var admin = await api.RegisterAsync();
        var (_, consentId) = await admin.ConnectAcikBankAsync(banks);
        var connectionId = (await admin.ConnectionsAsync()).Single().GetProperty("id").GetGuid();
        var accountIds = (await admin.ConnectionsAsync()).Single().GetProperty("accounts").EnumerateArray().Select(a => a.GetProperty("id").GetGuid()).Order().ToList();

        // The customer revokes in the bank's app; Kasa learns it on its next call.
        await (await banks.CreateClient().PostAsync($"/mock/acikbank/consents/{consentId}/revoke", null)).EnsureOkAsync();
        var result = await admin.SyncAsync(connectionId);

        Assert.Equal("ReauthRequired", result.GetProperty("outcome").GetString());
        Assert.Equal("ReauthRequired", (await admin.ConnectionsAsync()).Single().GetProperty("status").GetString());
        var again = await admin.Client.PostAsync($"/api/bank-connections/{connectionId}/sync", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        var (callback, _) = await admin.ConnectAcikBankAsync(banks);

        Assert.EndsWith("sonuc=baglandi", callback.Headers.Location!.ToString(), StringComparison.Ordinal);
        var reconnected = (await admin.ConnectionsAsync()).Single();
        Assert.Equal(connectionId, reconnected.GetProperty("id").GetGuid());
        Assert.Equal("Active", reconnected.GetProperty("status").GetString());
        Assert.Equal(accountIds, reconnected.GetProperty("accounts").EnumerateArray().Select(a => a.GetProperty("id").GetGuid()).Order().ToList());
    }

    [Fact]
    public async Task Connecting_AcikBank_twice_is_refused()
    {
        await using var banks = new MockBanksFactory();
        await using var api = new KasaApiFactory(fixture, banks: banks);
        var admin = await api.RegisterAsync();
        await admin.ConnectAsync(banks, "acikbank");

        var second = await admin.Client.PostAsync("/api/bank-connections/acikbank", null);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Theory]
    [InlineData("acikbank")]
    [InlineData("klasikbank")]
    public async Task A_bank_that_is_down_records_the_error_and_the_next_sync_catches_up(string bank)
    {
        await using var banks = new MockBanksFactory();
        await using var api = new KasaApiFactory(fixture, banks: banks);
        var admin = await api.RegisterAsync();
        var connectionId = await admin.ConnectAsync(banks, bank);

        await banks.SetFaultAsync(bank, "down");
        var reference = await banks.AddMovementAsync(bank, 500m);
        var failed = await admin.SyncAsync(connectionId);

        Assert.Equal("Failed", failed.GetProperty("outcome").GetString());
        Assert.Contains("503", failed.GetProperty("error").GetString(), StringComparison.Ordinal);
        var connection = (await admin.ConnectionsAsync()).Single();
        Assert.Equal("Active", connection.GetProperty("status").GetString());
        Assert.Equal("Failed", connection.GetProperty("lastSyncOutcome").GetString());

        await banks.SetFaultAsync(bank, "none");
        var recovered = await admin.SyncAsync(connectionId);

        Assert.Equal("Succeeded", recovered.GetProperty("outcome").GetString());
        Assert.Contains(await admin.AllTransactionsAsync(), t => t.GetProperty("bankReference").GetString() == reference);
        Assert.Equal(JsonValueKind.Null, (await admin.ConnectionsAsync()).Single().GetProperty("lastSyncError").ValueKind);
    }

    [Theory]
    [InlineData("acikbank")]
    [InlineData("klasikbank")]
    public async Task An_answer_with_a_malformed_amount_is_rejected_whole_and_nothing_from_it_is_stored(string bank)
    {
        await using var banks = new MockBanksFactory();
        await using var api = new KasaApiFactory(fixture, banks: banks);
        var admin = await api.RegisterAsync();
        var connectionId = await admin.ConnectAsync(banks, bank);
        var before = (await admin.AllTransactionsAsync()).Count;

        // AcikBank sends "12,5O" (a letter O), KlasikBank "1250.50" (an English amount in a Turkish field).
        await banks.SetFaultAsync(bank, "baddata");
        await banks.AddMovementAsync(bank, 75m);
        var result = await admin.SyncAsync(connectionId);

        Assert.Equal("Failed", result.GetProperty("outcome").GetString());
        Assert.Contains("amount", result.GetProperty("error").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, (await admin.AllTransactionsAsync()).Count);
        // Nothing was stored, but what the bank sent is kept for a person to look at.
        var archive = new MongoClient(fixture.Mongo).GetDatabase(fixture.MongoDatabase).GetCollection<BsonDocument>("bank_raw_responses");
        var bad = bank == "acikbank" ? "12,5O" : "1250.50";
        Assert.True(await archive.Find(Builders<BsonDocument>.Filter.Eq("connectionId", connectionId.ToString())
            & Builders<BsonDocument>.Filter.Regex("body", new BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(bad)))).AnyAsync());
    }

    [Fact]
    public async Task A_bank_slower_than_the_timeout_counts_as_down()
    {
        await using var banks = new MockBanksFactory();
        await using var api = new KasaApiFactory(fixture, b => b.UseSetting("Banks:Timeout", "00:00:01"), banks);
        var admin = await api.RegisterAsync();
        var connectionId = await admin.ConnectAsync(banks, "klasikbank");

        await banks.SetFaultAsync("klasikbank", "slow");
        var result = await admin.SyncAsync(connectionId);

        Assert.Equal("Failed", result.GetProperty("outcome").GetString());
        Assert.Contains("in time", result.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Syncs_started_at_the_same_moment_store_each_movement_once()
    {
        await using var banks = new MockBanksFactory();
        await using var api = new KasaApiFactory(fixture, banks: banks);
        var admin = await api.RegisterAsync();
        var connectionId = await admin.ConnectAsync(banks, "klasikbank");
        for (var i = 0; i < 3; i++)
        {
            await banks.AddMovementAsync("klasikbank", 100m + i);
        }

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => admin.Client.PostAsync($"/api/bank-connections/{connectionId}/sync", null)));

        // The Redis lock lets one run through; the others are told a sync is already running.
        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, r.StatusCode.ToString()));
        var imported = 0;
        foreach (var ok in responses.Where(r => r.StatusCode == HttpStatusCode.OK))
        {
            imported += (await ok.JsonAsync()).GetProperty("imported").GetInt32();
        }

        Assert.Equal(3, imported);
        await AssertNoDuplicatesAsync(admin);
    }

    [Fact]
    public async Task With_Redis_down_concurrent_syncs_still_store_each_movement_once()
    {
        await using var banks = new MockBanksFactory();
        // Nothing listens there: no lock at all, so the runs really race and the unique index decides.
        await using var api = new KasaApiFactory(fixture, b => b.UseSetting("Redis:Configuration", "localhost:6390"), banks);
        var admin = await api.RegisterAsync();
        var connectionId = await admin.ConnectAsync(banks, "klasikbank");
        for (var i = 0; i < 3; i++)
        {
            await banks.AddMovementAsync("klasikbank", 200m + i);
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => admin.SyncAsync(connectionId)));

        Assert.All(results, r => Assert.Equal("Succeeded", r.GetProperty("outcome").GetString()));
        Assert.Equal(3, results.Sum(r => r.GetProperty("imported").GetInt32()));
        await AssertNoDuplicatesAsync(admin);
    }

    [Fact]
    public async Task Raw_bank_answers_are_archived_in_MongoDB_without_tokens_or_passwords()
    {
        await using var banks = new MockBanksFactory();
        await using var api = new KasaApiFactory(fixture, banks: banks);
        var admin = await api.RegisterAsync();
        var klasik = await admin.ConnectAsync(banks, "klasikbank");
        var acik = await (await api.RegisterAsync()).ConnectAsync(banks, "acikbank");

        var archive = new MongoClient(fixture.Mongo).GetDatabase(fixture.MongoDatabase).GetCollection<BsonDocument>("bank_raw_responses");
        var klasikDocs = await archive.Find(Builders<BsonDocument>.Filter.Eq("connectionId", klasik.ToString())).ToListAsync();
        var acikDocs = await archive.Find(Builders<BsonDocument>.Filter.Eq("connectionId", acik.ToString())).ToListAsync();

        Assert.Contains(klasikDocs, d => d["operation"] == "HesapHareketleriSorgula" && d["body"].AsString.Contains("DekontNo", StringComparison.Ordinal));
        Assert.Contains(acikDocs, d => d["operation"] == "transactions" && d["status"] == 200);
        Assert.Contains(acikDocs, d => d["operation"] == "accounts");
        Assert.All(klasikDocs.Concat(acikDocs), d =>
        {
            Assert.DoesNotContain(MockBanksFactory.KlasikPassword, d.ToJson(), StringComparison.Ordinal);
            Assert.DoesNotContain("access_token", d.ToJson(), StringComparison.Ordinal);
        });
        var indexes = await (await archive.Indexes.ListAsync()).ToListAsync();
        Assert.Contains(indexes, i => i["name"] == "ttl_receivedAt" && i.Contains("expireAfterSeconds"));
    }

    [Fact]
    public async Task With_MongoDB_down_syncing_still_works()
    {
        await using var banks = new MockBanksFactory();
        await using var api = new KasaApiFactory(fixture, b => b.UseSetting("Mongo:ConnectionString", "mongodb://localhost:27099/?directConnection=true"), banks);
        var admin = await api.RegisterAsync();

        await admin.ConnectAsync(banks, "klasikbank");

        Assert.NotEmpty(await admin.AllTransactionsAsync());
    }

    [Fact]
    public async Task Another_company_sees_none_of_the_bank_data_and_cannot_act_on_the_connection()
    {
        await using var banks = new MockBanksFactory();
        await using var api = new KasaApiFactory(fixture, banks: banks);
        var owner = await api.RegisterAsync();
        var connectionId = await owner.ConnectAsync(banks, "klasikbank");
        var accountId = (await owner.Client.GetFromJsonAsync<JsonElement[]>("/api/accounts"))!.Single().GetProperty("id").GetGuid();
        var other = await api.RegisterAsync();

        Assert.Empty(await other.ConnectionsAsync());
        Assert.Empty((await other.Client.GetFromJsonAsync<JsonElement[]>("/api/accounts"))!);
        Assert.Empty(await other.AllTransactionsAsync());
        Assert.Empty((await other.Client.GetFromJsonAsync<JsonElement>($"/api/transactions?accountId={accountId}")).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.PostAsync($"/api/bank-connections/{connectionId}/sync", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.DeleteAsync($"/api/bank-connections/{connectionId}")).StatusCode);
        Assert.Equal("Active", (await owner.ConnectionsAsync()).Single().GetProperty("status").GetString());
    }

    [Fact]
    public async Task Viewers_cannot_connect_or_sync_accountants_can_sync_only_admins_remove()
    {
        await using var banks = new MockBanksFactory();
        await using var api = new KasaApiFactory(fixture, banks: banks);
        var admin = await api.RegisterAsync();
        var connectionId = await admin.ConnectAsync(banks, "klasikbank");
        var viewer = await api.InviteAsync(admin, "Viewer");
        var accountant = await api.InviteAsync(admin, "Accountant");

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Client.PostAsync("/api/bank-connections/acikbank", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Client.PostAsync($"/api/bank-connections/{connectionId}/sync", null)).StatusCode);
        Assert.NotEmpty(await viewer.AllTransactionsAsync());
        Assert.Equal("Succeeded", (await accountant.SyncAsync(connectionId)).GetProperty("outcome").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.Client.DeleteAsync($"/api/bank-connections/{connectionId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.Client.DeleteAsync($"/api/bank-connections/{connectionId}")).StatusCode);
    }

    [Fact]
    public async Task Removing_a_connection_forgets_its_secrets_but_keeps_the_movements()
    {
        await using var banks = new MockBanksFactory();
        await using var api = new KasaApiFactory(fixture, banks: banks);
        var admin = await api.RegisterAsync();
        var connectionId = await admin.ConnectAsync(banks, "acikbank");

        Assert.Equal(HttpStatusCode.NoContent, (await admin.Client.DeleteAsync($"/api/bank-connections/{connectionId}")).StatusCode);

        Assert.Empty(await admin.ConnectionsAsync());
        Assert.Empty((await admin.Client.GetFromJsonAsync<JsonElement[]>("/api/accounts"))!);
        await using var db = fixture.CreateDbContext();
        var row = await db.BankConnections.IgnoreQueryFilters().SingleAsync(c => c.Id == connectionId);
        Assert.Equal(ConnectionStatus.Removed, row.Status);
        Assert.Null(row.AccessTokenProtected);
        Assert.Null(row.RefreshTokenProtected);
        Assert.True(await db.BankTransactions.IgnoreQueryFilters().AnyAsync(t => t.Account!.BankConnectionId == connectionId));
        // A new connection can be made after removing the old one.
        Assert.NotEqual(connectionId, await admin.ConnectAsync(banks, "acikbank"));
    }

    [Fact]
    public async Task Paging_through_movements_returns_each_once_newest_first()
    {
        await using var banks = new MockBanksFactory();
        await using var api = new KasaApiFactory(fixture, banks: banks);
        var admin = await api.RegisterAsync();
        await admin.ConnectAsync(banks, "acikbank");

        var whole = await admin.AllTransactionsAsync(pageSize: 200);
        var paged = await admin.AllTransactionsAsync(pageSize: 3);

        Assert.True(whole.Count > 6, $"only {whole.Count} movements");
        Assert.Equal(whole.Select(t => t.GetProperty("id").GetInt64()), paged.Select(t => t.GetProperty("id").GetInt64()));
        var times = paged.Select(t => t.GetProperty("bookedAt").GetDateTimeOffset()).ToList();
        Assert.Equal(times.OrderDescending(), times);

        var incoming = (await admin.Client.GetFromJsonAsync<JsonElement>("/api/transactions?direction=In&limit=200")).GetProperty("items").EnumerateArray().ToList();
        Assert.NotEmpty(incoming);
        Assert.All(incoming, t => Assert.True(t.GetProperty("amount").GetDecimal() > 0));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Client.GetAsync("/api/transactions?cursor=not-a-cursor")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Client.GetAsync("/api/transactions?limit=1000")).StatusCode);
    }

    [Fact]
    public async Task The_scheduled_job_syncs_every_company_and_the_same_bank_movement_lands_in_each()
    {
        await using var banks = new MockBanksFactory();
        await using var api = new KasaApiFactory(fixture, banks: banks);
        var first = await api.RegisterAsync();
        var second = await api.RegisterAsync();
        await first.ConnectAsync(banks, "klasikbank");
        await second.ConnectAsync(banks, "klasikbank");
        var reference = await banks.AddMovementAsync("klasikbank", 321m);

        await ActivatorUtilities.CreateInstance<SyncAllBanksJob>(api.Services).Execute(null!, CancellationToken.None);

        Assert.Contains(await first.AllTransactionsAsync(), t => t.GetProperty("bankReference").GetString() == reference);
        Assert.Contains(await second.AllTransactionsAsync(), t => t.GetProperty("bankReference").GetString() == reference);
    }

    private static async Task AssertNoDuplicatesAsync(Session session)
    {
        var all = await session.AllTransactionsAsync();
        Assert.Equal(all.Count, all.Select(t => (t.GetProperty("accountId").GetGuid(), t.GetProperty("bankReference").GetString())).Distinct().Count());
    }
}
