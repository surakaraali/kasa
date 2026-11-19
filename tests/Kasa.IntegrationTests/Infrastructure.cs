using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kasa.Api.Banking;
using Kasa.Api.Common;
using Kasa.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kasa.IntegrationTests;

/// <summary>
/// Once per test run: recreates the Kasa_Test database from the migrations, so tests use the real schema with its
/// unique indexes. Tests never clean up: each one makes its own company, and tenant isolation keeps them apart.
/// </summary>
public sealed class KasaFixture
{
    public string Sql { get; } = Environment.GetEnvironmentVariable("TEST_SQL")
        ?? "Server=localhost,14335;Database=Kasa_Test;User Id=sa;Password=LocalDev_2026_pw;TrustServerCertificate=True;Connect Timeout=5";

    public string Redis { get; } = Environment.GetEnvironmentVariable("TEST_REDIS") ?? "localhost:6379";

    public string Mongo { get; } = Environment.GetEnvironmentVariable("TEST_MONGO") ?? "mongodb://kasa:LocalDev_2026_pw@localhost:27017/?authSource=admin";

    public string MongoDatabase => "kasa_test";

    public string Rabbit { get; } = Environment.GetEnvironmentVariable("TEST_RABBIT") ?? "localhost";

    /// <summary>Queue names for tests that do not choose their own; ERP tests take a fresh prefix each.</summary>
    public string RabbitPrefix { get; } = $"kasa-test-{Guid.NewGuid():N}-";

    /// <summary>The RabbitMQ settings both the API and the exporter use in a test; the queues must be declared identically.</summary>
    public void UseRabbit(IWebHostBuilder builder, string? prefix = null)
    {
        builder.UseSetting("RabbitMq:HostName", Rabbit);
        builder.UseSetting("RabbitMq:UserName", "kasa");
        builder.UseSetting("RabbitMq:Password", "LocalDev_2026_pw");
        builder.UseSetting("RabbitMq:Prefix", prefix ?? RabbitPrefix);
        builder.UseSetting("RabbitMq:RetryDelay", "00:00:00.300");
        // Test queues delete themselves once unused.
        builder.UseSetting("RabbitMq:QueueExpiry", "00:10:00");
    }

    /// <summary>One prefix per run, so counters and locks of an earlier run never leak into this one.</summary>
    public string RedisPrefix { get; } = $"kasa-test-{Guid.NewGuid():N}:";

    public KasaFixture()
    {
        using var db = CreateDbContext();
        try
        {
            db.Database.EnsureDeleted();
            db.Database.Migrate();
        }
        catch (SqlException ex)
        {
            throw new InvalidOperationException("SQL Server is not reachable. Start it with: docker compose up -d --wait", ex);
        }
    }

    public AppDbContext CreateDbContext(Guid? companyId = null)
    {
        var tenant = new TenantContext(new HttpContextAccessor());
        if (companyId is { } id)
        {
            tenant.Use(id);
        }

        return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(Sql).Options, tenant);
    }
}

[CollectionDefinition("Kasa")]
public class KasaCollection : ICollectionFixture<KasaFixture>;

/// <summary>
/// The real API in memory, pointed at the test database and a per-run Redis prefix. With <paramref name="banks"/>,
/// its bank HttpClients talk to those in-memory test banks instead of the network.
/// </summary>
public sealed class KasaApiFactory(
    KasaFixture fixture, Action<IWebHostBuilder>? configure = null, MockBanksFactory? banks = null, MockErpFactory? erp = null, string? rabbitPrefix = null)
    : WebApplicationFactory<Kasa.Api.AssemblyMarker>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Sql", fixture.Sql);
        builder.UseSetting("Database:MigrateOnStartup", "false");
        builder.UseSetting("Redis:Configuration", fixture.Redis);
        builder.UseSetting("Redis:KeyPrefix", fixture.RedisPrefix);
        // Tests sign in many times from the same in-memory "IP"; the limit has its own test.
        builder.UseSetting("LoginRateLimit:AttemptsPerMinute", "100000");
        builder.UseSetting("Mongo:ConnectionString", fixture.Mongo);
        builder.UseSetting("Mongo:Database", fixture.MongoDatabase);
        // Tests run syncs themselves; the 15-minute job stays off.
        builder.UseSetting("BankSync:Schedule", "");
        builder.UseSetting("Outbox:PollInterval", "00:00:00.100");
        fixture.UseRabbit(builder, rabbitPrefix);
        if (erp is not null)
        {
            builder.ConfigureTestServices(services =>
                services.AddHttpClient<Kasa.Api.Erp.ErpReader>().ConfigurePrimaryHttpMessageHandler(() => erp.Server.CreateHandler()));
        }

        if (banks is not null)
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient<AcikBankClient>().ConfigurePrimaryHttpMessageHandler(() => banks.Server.CreateHandler());
                services.AddHttpClient<KlasikBankClient>().ConfigurePrimaryHttpMessageHandler(() => banks.Server.CreateHandler());
            });
        }

        configure?.Invoke(builder);
    }
}

public sealed record Session(HttpClient Client, string Email, string Password)
{
    public string AccessToken => Client.DefaultRequestHeaders.Authorization!.Parameter!;
}

public static class Api
{
    public const string Password = "Test-Password-2026";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Registers a new company with its admin and returns a client signed in as that admin.</summary>
    public static async Task<Session> RegisterAsync(this KasaApiFactory factory, string? company = null)
    {
        var email = $"admin-{Guid.NewGuid():N}@test.kasa";
        // Redirects are checked by the tests, not followed.
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            companyName = company ?? $"Test {Guid.NewGuid():N}"[..20],
            fullName = "Test Admin",
            email,
            password = Password,
        });
        await response.EnsureOkAsync();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await response.AccessTokenAsync());
        return new Session(client, email, Password);
    }

    /// <summary>Invites a user with a role and signs that user in through the invitation.</summary>
    public static async Task<Session> InviteAsync(this KasaApiFactory factory, Session admin, string role)
    {
        var email = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@test.kasa";
        var invite = await admin.Client.PostAsJsonAsync("/api/invitations", new { email, role });
        await invite.EnsureOkAsync();
        var token = (await invite.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var accepted = await client.PostAsJsonAsync("/api/auth/accept-invitation", new { token, fullName = $"Test {role}", password = Password });
        await accepted.EnsureOkAsync();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await accepted.AccessTokenAsync());
        return new Session(client, email, Password);
    }

    public static async Task<string> AccessTokenAsync(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;

    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    /// <summary>Like EnsureSuccessStatusCode, but the failure message carries the body, which names the problem.</summary>
    public static async Task EnsureOkAsync(this HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            Assert.Fail($"{(int)response.StatusCode} {response.RequestMessage?.RequestUri}: {await response.Content.ReadAsStringAsync()}");
        }
    }

    /// <summary>The refresh cookie value from a response, to replay it later as an attacker would.</summary>
    public static string RefreshCookie(this HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("kasa_refresh=", StringComparison.Ordinal))
            .Split(';')[0]["kasa_refresh=".Length..];
}
