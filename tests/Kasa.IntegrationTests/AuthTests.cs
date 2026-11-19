using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;

namespace Kasa.IntegrationTests;

[Collection("Kasa")]
public sealed class AuthTests(KasaFixture fixture) : IDisposable
{
    private readonly KasaApiFactory api = new(fixture);

    public void Dispose() => api.Dispose();

    [Fact]
    public async Task Registering_creates_the_company_and_its_admin()
    {
        var admin = await api.RegisterAsync("Deniz Gıda A.Ş.");

        var me = await (await admin.Client.GetAsync("/api/me")).JsonAsync();

        Assert.Equal("Admin", me.GetProperty("role").GetString());
        Assert.Equal("Deniz Gıda A.Ş.", me.GetProperty("companyName").GetString());
        Assert.Equal(admin.Email, me.GetProperty("email").GetString());
    }

    [Fact]
    public async Task A_weak_password_is_refused_and_no_company_is_left_behind()
    {
        var name = $"Weak {Guid.NewGuid():N}"[..20];

        var response = await api.CreateClient().PostAsJsonAsync("/api/auth/register",
            new { companyName = name, fullName = "X Y", email = $"{Guid.NewGuid():N}@test.kasa", password = "short" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.JsonAsync()).GetProperty("errors").TryGetProperty("password", out _));
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.Companies.AnyAsync(c => c.Name == name));
    }

    [Fact]
    public async Task The_same_email_cannot_register_twice()
    {
        var admin = await api.RegisterAsync();

        var response = await api.CreateClient().PostAsJsonAsync("/api/auth/register",
            new { companyName = "Another Co", fullName = "X Y", email = admin.Email, password = Api.Password });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.JsonAsync()).GetProperty("errors").TryGetProperty("email", out _));
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_even_for_the_right_password()
    {
        var admin = await api.RegisterAsync();
        var client = api.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            var wrong = await client.PostAsJsonAsync("/api/auth/login", new { email = admin.Email, password = "Wrong-Password-1" });
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        var right = await client.PostAsJsonAsync("/api/auth/login", new { email = admin.Email, password = admin.Password });
        Assert.Equal(HttpStatusCode.Locked, right.StatusCode);
    }

    [Fact]
    public async Task An_unknown_email_gets_the_same_answer_as_a_wrong_password()
    {
        var response = await api.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = "nobody@test.kasa", password = "Wrong-Password-1" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("E-posta ya da şifre hatalı.", (await response.JsonAsync()).GetProperty("title").GetString());
    }

    [Fact]
    public async Task The_refresh_cookie_is_httponly_strict_and_limited_to_the_auth_path()
    {
        var response = await api.CreateClient().PostAsJsonAsync("/api/auth/register",
            new { companyName = "Cookie Co", fullName = "X Y", email = $"{Guid.NewGuid():N}@test.kasa", password = Api.Password });

        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("kasa_refresh=", StringComparison.Ordinal)).ToLowerInvariant();
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=strict", cookie);
        Assert.Contains("path=/api/auth", cookie);
    }

    [Fact]
    public async Task A_refresh_token_works_once_and_a_replayed_one_signs_the_whole_session_out()
    {
        var client = api.CreateClient();
        var registered = await client.PostAsJsonAsync("/api/auth/register",
            new { companyName = "Refresh Co", fullName = "X Y", email = $"{Guid.NewGuid():N}@test.kasa", password = Api.Password });
        var stolen = registered.RefreshCookie();

        // The real user refreshes: the stolen copy is now a used token.
        var refreshed = await client.PostAsync("/api/auth/refresh", null);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);

        // The attacker replays it.
        var attacker = api.CreateClient(new() { HandleCookies = false });
        var replay = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        replay.Headers.Add("Cookie", $"kasa_refresh={stolen}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await attacker.SendAsync(replay)).StatusCode);

        // The replay revoked the family, so the real user's newest token stops working too.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/auth/refresh", null)).StatusCode);
    }

    [Fact]
    public async Task Logging_out_ends_the_session()
    {
        var client = api.CreateClient();
        await (await client.PostAsJsonAsync("/api/auth/register",
            new { companyName = "Logout Co", fullName = "X Y", email = $"{Guid.NewGuid():N}@test.kasa", password = Api.Password })).EnsureOkAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/auth/refresh", null)).StatusCode);
    }

    [Fact]
    public async Task Without_a_token_the_api_is_closed()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task The_profile_changes_the_name_and_nothing_else()
    {
        var admin = await api.RegisterAsync();

        var response = await admin.Client.PutAsJsonAsync("/api/me", new { fullName = "  Ayşe Demir  " });
        await response.EnsureOkAsync();

        var me = await response.JsonAsync();
        Assert.Equal("Ayşe Demir", me.GetProperty("fullName").GetString());
        Assert.Equal(admin.Email, me.GetProperty("email").GetString());
        Assert.Equal("Admin", me.GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Client.PutAsJsonAsync("/api/me", new { fullName = "" })).StatusCode);
    }

    [Fact]
    public async Task Changing_the_password_needs_the_current_one_and_signs_every_other_session_out()
    {
        var admin = await api.RegisterAsync();
        var otherDevice = api.CreateClient();
        await (await otherDevice.PostAsJsonAsync("/api/auth/login", new { email = admin.Email, password = admin.Password })).EnsureOkAsync();
        const string newPassword = "New-Password-2027";

        var wrong = await admin.Client.PostAsJsonAsync("/api/me/password", new { currentPassword = "Not-The-Password-1", newPassword });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Contains("Mevcut şifre hatalı.", await wrong.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var changed = await admin.Client.PostAsJsonAsync("/api/me/password", new { currentPassword = admin.Password, newPassword });
        await changed.EnsureOkAsync();

        // This session goes on with the new cookie; the other device's session is over.
        Assert.Equal(HttpStatusCode.OK, (await admin.Client.PostAsync("/api/auth/refresh", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await otherDevice.PostAsync("/api/auth/refresh", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = admin.Email, password = admin.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = admin.Email, password = newPassword })).StatusCode);
    }

    [Fact]
    public async Task A_profile_photo_is_checked_by_its_bytes_and_seen_only_inside_the_company()
    {
        var admin = await api.RegisterAsync();
        var colleague = await api.InviteAsync(admin, "Viewer");
        var stranger = await api.RegisterAsync();
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

        // A text file renamed to .png, and a photo over 512 KB, are both refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(admin, "hello"u8.ToArray(), "image/png")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(admin, [.. png, .. new byte[512 * 1024]], "image/png")).StatusCode);

        var uploaded = await Upload(admin, png, "image/jpeg");
        await uploaded.EnsureOkAsync();
        var me = await uploaded.JsonAsync();
        var address = $"/api/users/{me.GetProperty("userId").GetGuid()}/avatar";
        Assert.Equal(1, me.GetProperty("avatarVersion").GetInt32());

        // Served with the type its bytes say, not the one the browser claimed.
        var seen = await colleague.Client.GetAsync(address);
        await seen.EnsureOkAsync();
        Assert.Equal("image/png", seen.Content.Headers.ContentType?.MediaType);
        Assert.Equal(png, await seen.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Client.GetAsync(address)).StatusCode);

        var removed = await admin.Client.DeleteAsync("/api/me/avatar");
        await removed.EnsureOkAsync();
        Assert.Equal(System.Text.Json.JsonValueKind.Null, (await removed.JsonAsync()).GetProperty("avatarVersion").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await colleague.Client.GetAsync(address)).StatusCode);
    }

    private static Task<HttpResponseMessage> Upload(Session user, byte[] bytes, string type)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        return user.Client.PutAsync("/api/me/avatar", new MultipartFormDataContent { { file, "file", "foto.png" } });
    }
}
