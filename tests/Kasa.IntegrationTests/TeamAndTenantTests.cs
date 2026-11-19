using System.Net;
using System.Net.Http.Json;
using Kasa.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kasa.IntegrationTests;

[Collection("Kasa")]
public sealed class TeamAndTenantTests(KasaFixture fixture) : IDisposable
{
    private readonly KasaApiFactory api = new(fixture);

    public void Dispose() => api.Dispose();

    [Fact]
    public async Task An_invited_accountant_joins_the_same_company_with_that_role()
    {
        var admin = await api.RegisterAsync("Invite Co");

        var accountant = await api.InviteAsync(admin, "Accountant");

        var me = await (await accountant.Client.GetAsync("/api/me")).JsonAsync();
        Assert.Equal("Accountant", me.GetProperty("role").GetString());
        Assert.Equal("Invite Co", me.GetProperty("companyName").GetString());
    }

    [Fact]
    public async Task An_invitation_link_works_once()
    {
        var admin = await api.RegisterAsync();
        var invite = await (await admin.Client.PostAsJsonAsync("/api/invitations", new { email = $"{Guid.NewGuid():N}@test.kasa", role = "Viewer" })).JsonAsync();
        var token = invite.GetProperty("token").GetString();

        var first = await api.CreateClient().PostAsJsonAsync("/api/auth/accept-invitation", new { token, fullName = "X Y", password = Api.Password });
        var second = await api.CreateClient().PostAsJsonAsync("/api/auth/accept-invitation", new { token, fullName = "X Y", password = Api.Password });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Theory]
    [InlineData("Accountant")]
    [InlineData("Viewer")]
    public async Task Only_admins_manage_the_team(string role)
    {
        var admin = await api.RegisterAsync();
        var user = await api.InviteAsync(admin, role);

        Assert.Equal(HttpStatusCode.Forbidden, (await user.Client.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await user.Client.PostAsJsonAsync("/api/invitations", new { email = $"{Guid.NewGuid():N}@test.kasa", role = "Admin" })).StatusCode);
    }

    [Fact]
    public async Task The_user_list_shows_only_the_own_company()
    {
        var a = await api.RegisterAsync();
        await api.InviteAsync(a, "Viewer");
        var b = await api.RegisterAsync();

        var users = await (await a.Client.GetAsync("/api/users")).JsonAsync();

        Assert.Equal(2, users.GetArrayLength());
        Assert.DoesNotContain(users.EnumerateArray(), u => u.GetProperty("email").GetString() == b.Email);
    }

    [Fact]
    public async Task Another_company_cannot_see_an_invitation_even_by_its_id()
    {
        var a = await api.RegisterAsync();
        var b = await api.RegisterAsync();
        var invite = await (await a.Client.PostAsJsonAsync("/api/invitations", new { email = $"{Guid.NewGuid():N}@test.kasa", role = "Viewer" })).JsonAsync();
        var id = invite.GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.OK, (await a.Client.GetAsync($"/api/invitations/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.GetAsync($"/api/invitations/{id}")).StatusCode);
        Assert.Equal(0, (await (await b.Client.GetAsync("/api/invitations")).JsonAsync()).GetArrayLength());
    }

    [Fact]
    public async Task Code_that_forgets_the_company_still_sees_nothing_and_cannot_write_across_companies()
    {
        var a = await api.RegisterAsync();
        var b = await api.RegisterAsync();
        var aId = (await (await a.Client.GetAsync("/api/me")).JsonAsync()).GetProperty("companyId").GetGuid();
        var bId = (await (await b.Client.GetAsync("/api/me")).JsonAsync()).GetProperty("companyId").GetGuid();
        await (await a.Client.PostAsJsonAsync("/api/invitations", new { email = $"{Guid.NewGuid():N}@test.kasa", role = "Viewer" })).EnsureOkAsync();

        // No company set: the filter matches nothing, although rows exist.
        await using (var noCompany = fixture.CreateDbContext())
        {
            Assert.Equal(0, await noCompany.Invitations.CountAsync());
            Assert.True(await noCompany.Invitations.IgnoreQueryFilters().AnyAsync(i => i.CompanyId == aId));
        }

        // Working for B, a row of A cannot be added.
        await using var asB = fixture.CreateDbContext(bId);
        asB.Invitations.Add(new Invitation { Id = Guid.NewGuid(), CompanyId = aId, Email = "x@test.kasa", Role = UserRole.Viewer, TokenHash = Guid.NewGuid().ToString("N") });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => asB.SaveChangesAsync());
        Assert.Contains("while working for company", error.Message);
    }
}
