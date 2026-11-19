using System.Net;

using Microsoft.AspNetCore.Mvc.Testing;

namespace Kasa.IntegrationTests;

/// <summary>Every service starts with its real configuration and answers /health.</summary>
public class HealthTests
{
    [Fact]
    public Task Api_is_healthy() => AssertHealthyAsync<Kasa.Api.AssemblyMarker>();

    [Fact]
    public Task ErpExporter_is_healthy() => AssertHealthyAsync<Kasa.ErpExporter.AssemblyMarker>();

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
