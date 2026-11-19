using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;

namespace Kasa.IntegrationTests;

[Collection("Kasa")]
public sealed class RateLimitTests(KasaFixture fixture)
{
    [Fact]
    public async Task Too_many_sign_in_attempts_from_one_address_are_refused_for_a_while()
    {
        using var api = new KasaApiFactory(fixture, b =>
        {
            b.UseSetting("LoginRateLimit:AttemptsPerMinute", "3");
            b.UseSetting("Redis:KeyPrefix", $"kasa-test-{Guid.NewGuid():N}:");
        });
        var client = api.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            var attempt = await client.PostAsJsonAsync("/api/auth/login", new { email = "nobody@test.kasa", password = "Wrong-Password-1" });
            Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);
        }

        var limited = await client.PostAsJsonAsync("/api/auth/login", new { email = "nobody@test.kasa", password = "Wrong-Password-1" });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(int.Parse(limited.Headers.GetValues("Retry-After").Single()) is > 0 and <= 60);
    }

    [Fact]
    public async Task With_Redis_down_signing_in_still_works()
    {
        // Nothing listens on this port: every Redis command fails.
        using var api = new KasaApiFactory(fixture, b => b.UseSetting("Redis:Configuration", "localhost:6390"));
        var admin = await api.RegisterAsync();

        var login = await api.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = admin.Email, password = admin.Password });

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }
}
