using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Kasa.IntegrationTests;

/// <summary>Every service starts with its real configuration and answers /health.</summary>
[Collection("Kasa")]
public class HealthTests(KasaFixture fixture)
{
    [Fact]
    public async Task Api_is_healthy()
    {
        using var api = new KasaApiFactory(fixture);
        var response = await api.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ErpExporter_is_healthy()
    {
        await using var exporter = new ExporterFactory(fixture);
        var response = await exporter.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public Task MockBanks_is_healthy() => AssertHealthyAsync<Kasa.MockBanks.AssemblyMarker>();

    [Fact]
    public Task MockErp_is_healthy() => AssertHealthyAsync<Kasa.MockErp.AssemblyMarker>();

    private static async Task AssertHealthyAsync<TService>() where TService : class
    {
        await using var factory = new WebApplicationFactory<TService>();

        var response = await factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
