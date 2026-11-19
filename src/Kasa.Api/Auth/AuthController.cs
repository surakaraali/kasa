using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Kasa.Api.Common;
using Kasa.Api.Data;
using Kasa.Api.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kasa.Api.Auth;

public sealed class RegisterRequest
{
    [Required(ErrorMessage = "Şirket adı gerekli."), StringLength(Company.NameMaxLength, MinimumLength = 2, ErrorMessage = "Şirket adı 2-200 karakter olmalı.")]
    public string CompanyName { get; set; } = "";

    [RegularExpression("^[0-9]{10}$", ErrorMessage = "Vergi numarası 10 haneli olmalı.")]
    public string? TaxNumber { get; set; }

    [Required(ErrorMessage = "Ad soyad gerekli."), StringLength(AppUser.FullNameMaxLength, MinimumLength = 2, ErrorMessage = "Ad soyad 2-200 karakter olmalı.")]
    public string FullName { get; set; } = "";

    [Required(ErrorMessage = "E-posta gerekli."), EmailAddress(ErrorMessage = "Geçerli bir e-posta adresi girin."), StringLength(256)]
    public string Email { get; set; } = "";

    [Required(ErrorMessage = "Şifre gerekli."), StringLength(128)]
    public string Password { get; set; } = "";
}

public sealed class LoginRequest
{
    [Required(ErrorMessage = "E-posta gerekli."), StringLength(256)]
    public string Email { get; set; } = "";

    [Required(ErrorMessage = "Şifre gerekli."), StringLength(128)]
    public string Password { get; set; } = "";
}

public sealed class AcceptInvitationRequest
{
    [Required, StringLength(100)]
    public string Token { get; set; } = "";

    [Required(ErrorMessage = "Ad soyad gerekli."), StringLength(AppUser.FullNameMaxLength, MinimumLength = 2, ErrorMessage = "Ad soyad 2-200 karakter olmalı.")]
    public string FullName { get; set; } = "";

    [Required(ErrorMessage = "Şifre gerekli."), StringLength(128)]
    public string Password { get; set; } = "";
}

public sealed class ProfileRequest
{
    [Required(ErrorMessage = "Ad soyad gerekli."), StringLength(AppUser.FullNameMaxLength, MinimumLength = 2, ErrorMessage = "Ad soyad 2-200 karakter olmalı.")]
    public string FullName { get; set; } = "";
}

public sealed class ChangePasswordRequest
{
    [Required(ErrorMessage = "Mevcut şifre gerekli."), StringLength(128)]
    public string CurrentPassword { get; set; } = "";

    [Required(ErrorMessage = "Yeni şifre gerekli."), StringLength(128)]
    public string NewPassword { get; set; } = "";
}

/// <summary>Only the access token is in the body. The refresh token is set as an httpOnly cookie.</summary>
public sealed record TokenResponse(string AccessToken, int ExpiresIn, string TokenType = "Bearer");

public sealed record MeResponse(Guid UserId, string FullName, string Email, UserRole Role, Guid CompanyId, string CompanyName);

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    AppDbContext db,
    UserManager<AppUser> users,
    TokenService tokens,
    RefreshTokens refreshTokens,
    LoginRateLimiter rateLimiter,
    TenantContext tenant,
    IOptions<JwtOptions> jwt,
    TimeProvider clock) : ControllerBase
{
    // A hash of a random password, checked when the e-mail is unknown, so "no such user" takes as long as "wrong password".
    private static readonly Lazy<string> DummyHash = new(() =>
        new PasswordHasher<AppUser>().HashPassword(new AppUser { FullName = "" }, TokenService.NewSecret()));

    /// <summary>Creates a company and its first user, an admin, in one transaction.</summary>
    [AllowAnonymous]
    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var company = new Company { Id = Guid.NewGuid(), Name = request.CompanyName.Trim(), TaxNumber = request.TaxNumber, CreatedAt = now };
        db.Companies.Add(company);
        await db.SaveChangesAsync(ct);

        var user = NewUser(request.Email, request.FullName, company.Id, UserRole.Admin, now);
        var created = await users.CreateAsync(user, request.Password);
        if (!created.Succeeded)
        {
            // Disposing the transaction without committing rolls the company back too.
            return IdentityProblem(created);
        }

        await transaction.CommitAsync(ct);
        return await SignInAsync(user, ct);
    }

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        var client = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (await rateLimiter.CheckAsync(client) is { } wait)
        {
            Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString();
            return Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Çok fazla giriş denemesi. Biraz sonra tekrar deneyin.");
        }

        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null)
        {
            users.PasswordHasher.VerifyHashedPassword(new AppUser { FullName = "" }, DummyHash.Value, request.Password);
            return WrongCredentials();
        }

        if (await users.IsLockedOutAsync(user))
        {
            return Problem(statusCode: StatusCodes.Status423Locked, title: "Çok fazla hatalı deneme. Hesap 15 dakika kilitlendi.");
        }

        if (!await users.CheckPasswordAsync(user, request.Password))
        {
            await users.AccessFailedAsync(user);
            return WrongCredentials();
        }

        await users.ResetAccessFailedCountAsync(user);
        return await SignInAsync(user, ct);
    }

    /// <summary>Trades the refresh cookie for a new access token and a new refresh cookie.</summary>
    [AllowAnonymous]
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        var (outcome, user, next) = await refreshTokens.RotateAsync(RefreshCookie.Read(HttpContext), ct);
        if (outcome != RefreshOutcome.Refreshed)
        {
            RefreshCookie.Clear(HttpContext);
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Oturumun süresi doldu. Tekrar giriş yapın.");
        }

        RefreshCookie.Set(HttpContext, next!, jwt.Value.RefreshTokenDays);
        var access = tokens.CreateAccessToken(user!);
        return Ok(new TokenResponse(access.AccessToken, access.ExpiresIn));
    }

    [AllowAnonymous]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await refreshTokens.RevokeAsync(RefreshCookie.Read(HttpContext), ct);
        RefreshCookie.Clear(HttpContext);
        return NoContent();
    }

    /// <summary>Joins the company that sent the invitation, with the role in it.</summary>
    [AllowAnonymous]
    [HttpPost("accept-invitation")]
    public async Task<IActionResult> AcceptInvitation(AcceptInvitationRequest request, CancellationToken ct)
    {
        var hash = TokenService.Hash(request.Token);
        var now = clock.GetUtcNow();
        // Nobody is signed in yet, so the company comes from the invitation itself.
        var invitation = await db.Invitations.IgnoreQueryFilters().SingleOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (invitation is null || invitation.AcceptedAt is not null || invitation.ExpiresAt <= now)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Davet bağlantısı geçersiz ya da süresi dolmuş.");
        }

        tenant.Use(invitation.CompanyId);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var user = NewUser(invitation.Email, request.FullName, invitation.CompanyId, invitation.Role, now);
        var created = await users.CreateAsync(user, request.Password);
        if (!created.Succeeded)
        {
            return IdentityProblem(created);
        }

        invitation.AcceptedAt = now;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await SignInAsync(user, ct);
    }

    [HttpGet("/api/me")]
    public async Task<ActionResult<MeResponse>> Me(CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirstValue(KasaClaims.UserId)!);
        var me = await db.Users.Where(u => u.Id == userId)
            .Select(u => new MeResponse(u.Id, u.FullName, u.Email!, u.Role, u.CompanyId, u.Company!.Name))
            .SingleOrDefaultAsync(ct);
        return me is null ? Unauthorized() : me;
    }

    /// <summary>The user's own name. E-mail and role are not changed here: one needs a verified address, the other an admin.</summary>
    [HttpPut("/api/me")]
    public async Task<ActionResult<MeResponse>> UpdateProfile(ProfileRequest request, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(User.FindFirstValue(KasaClaims.UserId)!);
        if (user is null)
        {
            return Unauthorized();
        }

        user.FullName = request.FullName.Trim();
        await users.UpdateAsync(user);
        return await Me(ct);
    }

    /// <summary>
    /// Needs the current password, so a session left open on someone else's screen cannot take the account over.
    /// Every other session is signed out; this one gets a fresh token pair and stays signed in.
    /// </summary>
    [HttpPost("/api/me/password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(User.FindFirstValue(KasaClaims.UserId)!);
        if (user is null)
        {
            return Unauthorized();
        }

        var changed = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!changed.Succeeded)
        {
            return IdentityProblem(changed);
        }

        await refreshTokens.RevokeAllAsync(user.Id, ct);
        return await SignInAsync(user, ct);
    }

    private static AppUser NewUser(string email, string fullName, Guid companyId, UserRole role, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        UserName = email.Trim(),
        Email = email.Trim(),
        FullName = fullName.Trim(),
        CompanyId = companyId,
        Role = role,
        CreatedAt = now,
    };

    private async Task<IActionResult> SignInAsync(AppUser user, CancellationToken ct)
    {
        var refresh = await refreshTokens.IssueAsync(user.Id, familyId: null, ct);
        RefreshCookie.Set(HttpContext, refresh, jwt.Value.RefreshTokenDays);
        var access = tokens.CreateAccessToken(user);
        return Ok(new TokenResponse(access.AccessToken, access.ExpiresIn));
    }

    private ObjectResult WrongCredentials() =>
        Problem(statusCode: StatusCodes.Status401Unauthorized, title: "E-posta ya da şifre hatalı.");

    private ActionResult IdentityProblem(IdentityResult result)
    {
        foreach (var error in result.Errors)
        {
            var field = error.Code.StartsWith("Password", StringComparison.Ordinal) ? "password" : "email";
            ModelState.AddModelError(field, error.Description);
        }

        return ValidationProblem(ModelState);
    }
}
