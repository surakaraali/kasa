using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace Kasa.IntegrationTests;

[Collection("Kasa")]
public sealed class DashboardTests(KasaFixture fixture)
{
    [Fact]
    public async Task The_dashboard_is_cached_until_something_changes()
    {
        await using var banks = new MockBanksFactory();
        await using var api = new KasaApiFactory(fixture, banks: banks);
        var admin = await api.RegisterAsync();
        var connectionId = await admin.ConnectAsync(banks, "klasikbank");

        var first = await admin.Client.GetAsync("/api/dashboard");
        var second = await admin.Client.GetAsync("/api/dashboard");

        Assert.Equal("miss", first.Headers.GetValues("X-Cache").Single());
        Assert.Equal("hit", second.Headers.GetValues("X-Cache").Single());
        var dashboard = await first.JsonAsync();
        Assert.Equal(30, dashboard.GetProperty("daily").GetArrayLength());
        Assert.Equal(dashboard.GetProperty("accounts").EnumerateArray().Sum(a => a.GetProperty("balance").GetDecimal()), dashboard.GetProperty("totalBalance").GetDecimal());
        Assert.Equal((await admin.AllTransactionsAsync()).Count, dashboard.GetProperty("unmatched").GetInt32());

        await banks.AddMovementAsync("klasikbank", 1000m);
        await admin.SyncAsync(connectionId);
        var after = await admin.Client.GetAsync("/api/dashboard");

        Assert.Equal("miss", after.Headers.GetValues("X-Cache").Single());
        var updated = await after.JsonAsync();
        Assert.Equal(dashboard.GetProperty("todayIn").GetDecimal() + 1000m, updated.GetProperty("todayIn").GetDecimal());
    }

    [Fact]
    public async Task With_Redis_down_the_dashboard_is_computed_every_time()
    {
        await using var api = new KasaApiFactory(fixture, b => b.UseSetting("Redis:Configuration", "localhost:6390"));
        var admin = await api.RegisterAsync();

        var response = await admin.Client.GetAsync("/api/dashboard");

        await response.EnsureOkAsync();
        Assert.Equal("miss", response.Headers.GetValues("X-Cache").Single());
    }

    [Fact]
    public async Task A_sync_is_pushed_live_to_the_company_and_to_no_other()
    {
        await using var banks = new MockBanksFactory();
        await using var api = new KasaApiFactory(fixture, banks: banks);
        var owner = await api.RegisterAsync();
        var other = await api.RegisterAsync();
        var connectionId = await owner.ConnectAsync(banks, "klasikbank");

        var ownerHeard = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherHeard = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var ownerHub = await ConnectHubAsync(api, owner, ownerHeard);
        await using var otherHub = await ConnectHubAsync(api, other, otherHeard);

        await owner.SyncAsync(connectionId);

        Assert.Equal("sync", await ownerHeard.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await Task.Delay(500);
        Assert.False(otherHeard.Task.IsCompleted);
    }

    [Fact]
    public async Task The_hub_refuses_a_connection_without_a_token()
    {
        await using var api = new KasaApiFactory(fixture);
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(api.Server.BaseAddress, "/hubs/kasa"), o =>
            {
                o.HttpMessageHandlerFactory = _ => api.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => hub.StartAsync());

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, error.StatusCode);
    }

    private static async Task<HubConnection> ConnectHubAsync(KasaApiFactory api, Session session, TaskCompletionSource<string> heard)
    {
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(api.Server.BaseAddress, "/hubs/kasa"), o =>
            {
                // The test server has no real sockets; long polling goes through its handler.
                o.HttpMessageHandlerFactory = _ => api.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.AccessTokenProvider = () => Task.FromResult<string?>(session.AccessToken);
            })
            .Build();
        hub.On<string>("changed", what => heard.TrySetResult(what));
        await hub.StartAsync();
        return hub;
    }
}
