using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Kasa.IntegrationTests;

/// <summary>Every service starts with its real configuration and reports its dependencies on /health.</summary>
[Collection("Kasa")]
public class HealthTests(KasaFixture fixture)
{
    [Fact]
    public async Task Api_reports_each_dependency()
    {
        using var api = new KasaApiFactory(fixture);

        var health = await api.CreateClient().GetFromJsonAsync<JsonElement>("/health");

        Assert.Equal("Healthy", health.GetProperty("status").GetString());
        foreach (var check in new[] { "sqlserver", "redis", "mongodb", "rabbitmq" })
        {
            Assert.Equal("Healthy", health.GetProperty("checks").GetProperty(check).GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task Api_without_Redis_and_MongoDB_is_degraded_not_down()
    {
        using var api = new KasaApiFactory(fixture, b =>
        {
            b.UseSetting("Redis:Configuration", "localhost:6390");
            b.UseSetting("Mongo:ConnectionString", "mongodb://localhost:27099/?directConnection=true");
        });

        var response = await api.CreateClient().GetAsync("/health");
        var health = await response.JsonAsync();

        // Degraded still answers 200: the API keeps serving.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Degraded", health.GetProperty("status").GetString());
        Assert.Equal("Degraded", health.GetProperty("checks").GetProperty("redis").GetProperty("status").GetString());
        Assert.Equal("Healthy", health.GetProperty("checks").GetProperty("sqlserver").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Liveness_does_not_depend_on_anything()
    {
        using var api = new KasaApiFactory(fixture, b => b.UseSetting("Redis:Configuration", "localhost:6390"));
        Assert.Equal(HttpStatusCode.OK, (await api.CreateClient().GetAsync("/alive")).StatusCode);
    }

    [Fact]
    public async Task ErpExporter_is_healthy()
    {
        await using var erp = new MockErpFactory();
        await using var exporter = new ExporterFactory(fixture, erp);
        var health = await exporter.CreateClient().GetFromJsonAsync<JsonElement>("/health");
        Assert.Equal("Healthy", health.GetProperty("status").GetString());
    }

    [Fact]
    public Task MockBanks_is_healthy() => AssertHealthyAsync<Kasa.MockBanks.AssemblyMarker>();

    [Fact]
    public Task MockErp_is_healthy() => AssertHealthyAsync<Kasa.MockErp.AssemblyMarker>();

    private static async Task AssertHealthyAsync<TService>() where TService : class
    {
        await using var factory = new WebApplicationFactory<TService>();
        var health = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/health");
        Assert.Equal("Healthy", health.GetProperty("status").GetString());
    }
}
