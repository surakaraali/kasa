using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kasa.Api.Domain;
using Kasa.Api.Erp;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;

namespace Kasa.IntegrationTests;

public sealed class MatchingRuleTests
{
    private static readonly CurrentAccount Abc = new() { Id = Guid.NewGuid(), Code = "1", Title = "ABC", Iban = "TR01", TaxNumber = "1111111111" };
    private static readonly CurrentAccount Deniz = new() { Id = Guid.NewGuid(), Code = "2", Title = "DENİZ", TaxNumber = "2222222222" };
    private static readonly CurrentAccount Kuzey = new() { Id = Guid.NewGuid(), Code = "3", Title = "KUZEY", Iban = "TR03" };
    private static readonly CurrentAccount[] All = [Abc, Deniz, Kuzey];

    [Fact]
    public void Iban_decides_first() => Assert.Equal((Abc, MatchRule.Iban), Matching.Decide("TR01", null, All));

    [Fact]
    public void Tax_number_decides_when_there_is_no_iban_match() => Assert.Equal((Deniz, MatchRule.TaxNumber), Matching.Decide("TR99", "2222222222", All));

    [Fact]
    public void Iban_and_tax_number_agreeing_is_an_iban_match() => Assert.Equal((Abc, MatchRule.Iban), Matching.Decide("TR01", "1111111111", All));

    [Fact]
    public void Iban_and_tax_number_pointing_at_different_accounts_is_left_to_a_person() => Assert.Equal((null, null), Matching.Decide("TR03", "2222222222", All));

    [Fact]
    public void Two_accounts_with_the_same_iban_is_left_to_a_person()
    {
        var twin = new CurrentAccount { Id = Guid.NewGuid(), Code = "4", Title = "ABC 2", Iban = "TR01" };
        Assert.Equal((null, null), Matching.Decide("TR01", null, [.. All, twin]));
    }

    [Fact]
    public void Nothing_to_go_on_is_unmatched() => Assert.Equal((null, null), Matching.Decide(null, null, All));
}

[Collection("Kasa")]
public sealed class ErpTests(KasaFixture fixture)
{
    [Fact]
    public async Task Movements_are_matched_by_the_rules_and_each_becomes_one_voucher_in_the_ERP()
    {
        await using var rig = new ErpRig(fixture);
        var admin = await rig.Api.RegisterAsync();
        // Bank first, ERP later: importing the current accounts matches the movements already there.
        await admin.ConnectAsync(rig.Banks, "klasikbank");
        await (await admin.Client.PutAsJsonAsync("/api/company/erp", new { firmCode = "001" })).EnsureOkAsync();
        var import = await (await admin.Client.PostAsync("/api/current-accounts/import", null)).JsonAsync();
        Assert.Equal(7, import.GetProperty("added").GetInt32());
        Assert.True(import.GetProperty("matched").GetInt32() > 0);
        // ERP first, bank later: the sync matches as it stores.
        await admin.ConnectAsync(rig.Banks, "acikbank");

        await admin.WaitForExportsAsync();

        var all = await admin.AllTransactionsAsync();
        string? CodeOf(string counterparty) => all.First(t => t.GetProperty("counterpartyName").GetString() == counterparty).GetProperty("currentAccountCode").GetString();
        Assert.Equal("120.01.001", CodeOf("ABC TEKSTİL SANAYİ A.Ş."));
        Assert.Equal("120.01.003", CodeOf("ÇINAR MOBİLYA A.Ş."));
        Assert.Equal("320.01.001", CodeOf("YILDIZ AMBALAJ LTD. ŞTİ."));
        Assert.Equal("120.01.002", CodeOf("DENİZ GIDA PAZARLAMA"));
        Assert.Null(CodeOf("KUZEY LOJİSTİK A.Ş."));
        Assert.Null(CodeOf("IŞIK GAYRİMENKUL"));
        Assert.Null(CodeOf("MEHMET ÖZGÜR ÇALIŞKAN"));

        var matched = all.Where(t => t.GetProperty("matchStatus").GetString() == "Matched").ToList();
        Assert.All(matched, t =>
        {
            Assert.Equal("Exported", t.GetProperty("exportStatus").GetString());
            Assert.False(string.IsNullOrEmpty(t.GetProperty("erpVoucherNo").GetString()));
        });
        var vouchers = await rig.Erp.VouchersAsync();
        Assert.Equal(matched.Count, vouchers.Length);
        var abc = matched.First(t => t.GetProperty("currentAccountCode").GetString() == "120.01.001" && t.GetProperty("amount").GetDecimal() > 0);
        var voucher = vouchers.Single(v => v.GetProperty("voucherNo").GetString() == abc.GetProperty("erpVoucherNo").GetString());
        Assert.Equal("Tahsilat", voucher.GetProperty("type").GetString());
        Assert.Equal(abc.GetProperty("amount").GetDecimal(), voucher.GetProperty("amount").GetDecimal());
        Assert.Equal(abc.GetProperty("bankReference").GetString(), voucher.GetProperty("reference").GetString());
    }

    [Fact]
    public async Task A_person_matches_what_the_rules_could_not_and_ignores_a_bank_fee()
    {
        await using var rig = new ErpRig(fixture);
        var admin = await rig.CompanyWithErpAsync();
        await admin.ConnectAsync(rig.Banks, "klasikbank");
        await admin.WaitForExportsAsync();
        var vouchersBefore = (await rig.Erp.VouchersAsync()).Length;

        var rent = (await admin.TransactionsAsync("match=Unmatched")).First(t => t.GetProperty("counterpartyName").GetString() == "IŞIK GAYRİMENKUL");
        var isik = await admin.CurrentAccountIdAsync("320.01.002");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.MatchAsync(rent, isik)).StatusCode);
        await admin.WaitForExportsAsync();

        var exported = await admin.TransactionAsync(rent.GetProperty("id").GetInt64());
        Assert.Equal("Manual", exported.GetProperty("matchRule").GetString());
        Assert.Equal("Exported", exported.GetProperty("exportStatus").GetString());
        var voucher = (await rig.Erp.VouchersAsync()).Single(v => v.GetProperty("voucherNo").GetString() == exported.GetProperty("erpVoucherNo").GetString());
        Assert.Equal("Tediye", voucher.GetProperty("type").GetString());

        var fee = (await admin.TransactionsAsync("match=Unmatched")).First(t => t.GetProperty("description").GetString() == "EFT MASRAFI");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.MatchAsync(fee, null)).StatusCode);
        Assert.Equal("Ignored", (await admin.TransactionAsync(fee.GetProperty("id").GetInt64())).GetProperty("matchStatus").GetString());
        Assert.Equal(vouchersBefore + 1, (await rig.Erp.VouchersAsync()).Length);

        var history = (await admin.Client.GetFromJsonAsync<JsonElement[]>($"/api/transactions/{rent.GetProperty("id").GetInt64()}/history"))!;
        var decision = Assert.Single(history);
        Assert.Equal("IŞIK GAYRİMENKUL", decision.GetProperty("currentAccountTitle").GetString());
        Assert.Equal("Test Admin", decision.GetProperty("userName").GetString());
    }

    [Fact]
    public async Task Two_people_deciding_the_same_movement_the_second_is_told_it_changed()
    {
        await using var rig = new ErpRig(fixture);
        var admin = await rig.CompanyWithErpAsync();
        var accountant = await rig.Api.InviteAsync(admin, "Accountant");
        await admin.ConnectAsync(rig.Banks, "klasikbank");
        var seenByBoth = (await admin.TransactionsAsync("match=Unmatched")).First(t => t.GetProperty("counterpartyName").GetString() == "KUZEY LOJİSTİK A.Ş.");

        var first = await admin.MatchAsync(seenByBoth, await admin.CurrentAccountIdAsync("120.01.004"));
        var second = await accountant.MatchAsync(seenByBoth, await admin.CurrentAccountIdAsync("120.01.005"));

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        await admin.WaitForExportsAsync();
        var final = await admin.TransactionAsync(seenByBoth.GetProperty("id").GetInt64());
        Assert.Equal("120.01.004", final.GetProperty("currentAccountCode").GetString());

        // Exported: changing it now would leave the ERP with a voucher Kasa no longer agrees with.
        var again = await admin.MatchAsync(final, await admin.CurrentAccountIdAsync("120.01.005"));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task The_same_export_sent_twice_makes_one_voucher()
    {
        await using var rig = new ErpRig(fixture);
        var admin = await rig.CompanyWithErpAsync();
        await admin.ConnectAsync(rig.Banks, "klasikbank");
        await admin.WaitForExportsAsync();
        var vouchers = (await rig.Erp.VouchersAsync()).Length;
        Assert.True(vouchers > 0);

        // As if the API died after RabbitMQ took the messages but before it marked them sent: they all go again.
        var companyId = (await admin.Client.GetFromJsonAsync<JsonElement>("/api/me")).GetProperty("companyId").GetGuid();
        await using (var db = fixture.CreateDbContext())
        {
            var resent = await db.OutboxMessages.Where(m => m.CompanyId == companyId).ExecuteUpdateAsync(s => s.SetProperty(m => m.PublishedAt, (DateTimeOffset?)null));
            Assert.Equal(vouchers, resent);
        }

        await ErpKit.Until(async () =>
        {
            await using var db = fixture.CreateDbContext();
            return !await db.OutboxMessages.AnyAsync(m => m.CompanyId == companyId && m.PublishedAt == null);
        });
        await Task.Delay(1500);

        Assert.Equal(vouchers, (await rig.Erp.VouchersAsync()).Length);
        Assert.All(await admin.TransactionsAsync("match=Matched"), t => Assert.Equal("Exported", t.GetProperty("exportStatus").GetString()));
    }

    [Fact]
    public async Task While_the_ERP_is_down_exports_wait_and_go_through_when_it_is_back()
    {
        await using var rig = new ErpRig(fixture, maxAttempts: 50);
        var admin = await rig.CompanyWithErpAsync();
        await rig.Erp.SetFaultAsync("down");

        await admin.ConnectAsync(rig.Banks, "klasikbank");
        await Task.Delay(1500);

        // Still retrying, nothing lost and nothing failed yet. (The ERP's own list is down too, so it is checked after.)
        Assert.NotEmpty(await admin.TransactionsAsync("export=Pending"));
        Assert.Empty(await admin.TransactionsAsync("export=Failed"));

        await rig.Erp.SetFaultAsync("none");
        await admin.WaitForExportsAsync();

        var matched = await admin.TransactionsAsync("match=Matched");
        Assert.All(matched, t => Assert.Equal("Exported", t.GetProperty("exportStatus").GetString()));
        Assert.Equal(matched.Count, (await rig.Erp.VouchersAsync()).Length);
    }

    [Fact]
    public async Task When_the_ERP_writes_the_voucher_but_the_answer_is_lost_the_retry_makes_no_second_voucher()
    {
        await using var rig = new ErpRig(fixture, maxAttempts: 50);
        var admin = await rig.CompanyWithErpAsync();
        await rig.Erp.SetFaultAsync("loseresponse");
        await admin.ConnectAsync(rig.Banks, "klasikbank");
        var matched = (await admin.TransactionsAsync("match=Matched")).Count;

        // The ERP has every voucher, Kasa does not know it yet.
        await ErpKit.Until(async () => (await rig.Erp.VouchersAsync()).Length == matched);
        Assert.NotEmpty(await admin.TransactionsAsync("export=Pending"));

        await rig.Erp.SetFaultAsync("none");
        await admin.WaitForExportsAsync();

        Assert.Equal(matched, (await rig.Erp.VouchersAsync()).Length);
        Assert.Equal(matched, (await admin.TransactionsAsync("export=Exported")).Count);
    }

    [Fact]
    public async Task While_RabbitMQ_is_down_the_outbox_keeps_the_messages_and_sends_them_later()
    {
        var prefix = $"kasa-test-{Guid.NewGuid():N}-";
        await using var banks = new MockBanksFactory();
        await using var erp = new MockErpFactory();
        Session admin;
        Guid companyId;
        await using (var down = new KasaApiFactory(fixture, b => b.UseSetting("RabbitMq:Port", "5699"), banks, erp, prefix))
        {
            admin = await down.RegisterAsync();
            await (await admin.Client.PutAsJsonAsync("/api/company/erp", new { firmCode = "001" })).EnsureOkAsync();
            await (await admin.Client.PostAsync("/api/current-accounts/import", null)).EnsureOkAsync();
            await admin.ConnectAsync(banks, "klasikbank");
            companyId = (await admin.Client.GetFromJsonAsync<JsonElement>("/api/me")).GetProperty("companyId").GetGuid();
            await Task.Delay(1500);

            await using var db = fixture.CreateDbContext();
            Assert.True(await db.OutboxMessages.CountAsync(m => m.CompanyId == companyId && m.PublishedAt == null) > 0);
        }

        // RabbitMQ is back (a new API instance with the right address); nothing was lost meanwhile.
        await using var exporter = new ExporterFactory(fixture, erp, prefix).Started();
        await using var up = new KasaApiFactory(fixture, banks: banks, erp: erp, rabbitPrefix: prefix);
        var again = await up.SignInAsync(admin);
        await again.WaitForExportsAsync();

        Assert.All(await again.TransactionsAsync("match=Matched"), t => Assert.Equal("Exported", t.GetProperty("exportStatus").GetString()));
    }

    [Fact]
    public async Task While_the_exporter_is_not_running_exports_wait_in_the_queue()
    {
        await using var rig = new ErpRig(fixture, startExporter: false);
        var admin = await rig.CompanyWithErpAsync();
        await admin.ConnectAsync(rig.Banks, "klasikbank");
        var companyId = (await admin.Client.GetFromJsonAsync<JsonElement>("/api/me")).GetProperty("companyId").GetGuid();

        // The outbox hands everything to RabbitMQ; with no consumer it stays there.
        await ErpKit.Until(async () =>
        {
            await using var db = fixture.CreateDbContext();
            return !await db.OutboxMessages.AnyAsync(m => m.CompanyId == companyId && m.PublishedAt == null);
        });
        Assert.NotEmpty(await admin.TransactionsAsync("export=Pending"));

        rig.StartExporter();
        await admin.WaitForExportsAsync();

        Assert.Empty(await admin.TransactionsAsync("export=Pending"));
        Assert.NotEmpty(await rig.Erp.VouchersAsync());
    }

    [Fact]
    public async Task After_every_retry_fails_the_export_is_dead_lettered_and_can_be_sent_again()
    {
        await using var rig = new ErpRig(fixture, maxAttempts: 2);
        var admin = await rig.CompanyWithErpAsync();
        await rig.Erp.SetFaultAsync("down");
        await admin.ConnectAsync(rig.Banks, "klasikbank");

        await admin.WaitForExportsAsync();

        var failed = await admin.TransactionsAsync("export=Failed");
        Assert.NotEmpty(failed);
        Assert.Contains("2 denemede", failed[0].GetProperty("exportError").GetString(), StringComparison.Ordinal);
        Assert.True(await DeadLetterCountAsync(rig.Prefix) >= failed.Count);

        // A person sends it again by confirming the match.
        await rig.Erp.SetFaultAsync("none");
        var retry = failed[0];
        Assert.Equal(HttpStatusCode.NoContent, (await admin.MatchAsync(retry, retry.GetProperty("currentAccountId").GetGuid())).StatusCode);
        await admin.WaitForExportsAsync();
        Assert.Equal("Exported", (await admin.TransactionAsync(retry.GetProperty("id").GetInt64())).GetProperty("exportStatus").GetString());
    }

    [Fact]
    public async Task A_voucher_the_ERP_refuses_fails_at_once_without_retrying()
    {
        await using var rig = new ErpRig(fixture, maxAttempts: 50);
        var admin = await rig.CompanyWithErpAsync();
        var companyId = (await admin.Client.GetFromJsonAsync<JsonElement>("/api/me")).GetProperty("companyId").GetGuid();
        await using (var db = fixture.CreateDbContext(companyId))
        {
            // The card was deleted in the ERP after Kasa copied it.
            await db.CurrentAccounts.Where(c => c.Code == "120.01.002").ExecuteUpdateAsync(s => s.SetProperty(c => c.Code, "999.99.999"));
        }

        await admin.ConnectAsync(rig.Banks, "klasikbank");
        await admin.WaitForExportsAsync(seconds: 10);

        var failed = Assert.Single((await admin.TransactionsAsync("export=Failed")).DistinctBy(t => t.GetProperty("currentAccountCode").GetString()));
        Assert.Equal("999.99.999", failed.GetProperty("currentAccountCode").GetString());
        Assert.Contains("422", failed.GetProperty("exportError").GetString(), StringComparison.Ordinal);
        Assert.NotEmpty(await admin.TransactionsAsync("export=Exported"));
    }

    [Fact]
    public async Task Another_company_cannot_see_current_accounts_or_match_movements()
    {
        await using var rig = new ErpRig(fixture);
        var owner = await rig.CompanyWithErpAsync();
        await owner.ConnectAsync(rig.Banks, "klasikbank");
        var movement = (await owner.TransactionsAsync("match=Unmatched")).First();
        var other = await rig.Api.RegisterAsync();

        Assert.Empty((await other.Client.GetFromJsonAsync<JsonElement[]>("/api/current-accounts"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await other.MatchAsync(movement, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.GetAsync($"/api/transactions/{movement.GetProperty("id").GetInt64()}/history")).StatusCode);
        // Another company's current account cannot be used either.
        var otherMovementOwnerAccount = await owner.MatchAsync(movement, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.BadRequest, otherMovementOwnerAccount.StatusCode);
    }

    [Fact]
    public async Task Viewers_cannot_import_or_match_and_only_admins_set_the_ERP_firm()
    {
        await using var rig = new ErpRig(fixture);
        var admin = await rig.CompanyWithErpAsync();
        await admin.ConnectAsync(rig.Banks, "klasikbank");
        var viewer = await rig.Api.InviteAsync(admin, "Viewer");
        var accountant = await rig.Api.InviteAsync(admin, "Accountant");
        var movement = (await admin.TransactionsAsync("match=Unmatched")).First();

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Client.PostAsync("/api/current-accounts/import", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.MatchAsync(movement, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.Client.PutAsJsonAsync("/api/company/erp", new { firmCode = "002" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await accountant.Client.PostAsync("/api/current-accounts/import", null)).StatusCode);
    }

    private async Task<uint> DeadLetterCountAsync(string prefix)
    {
        var factory = new ConnectionFactory { HostName = fixture.Rabbit, UserName = "kasa", Password = "LocalDev_2026_pw" };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        return await channel.MessageCountAsync($"{prefix}erp.export.dead");
    }
}
