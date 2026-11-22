using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Kasa.Api.Common;
using Kasa.Api.Data;
using Kasa.Api.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Kasa.Api.Auth;

public sealed class CreateInvitationRequest
{
    [Required(ErrorMessage = "E-posta gerekli."), EmailAddress(ErrorMessage = "Geçerli bir e-posta adresi girin."), StringLength(256)]
    public string Email { get; set; } = "";

    [Required]
    public UserRole? Role { get; set; }
}

public sealed record InvitationResponse(Guid Id, string Email, UserRole Role, DateTimeOffset ExpiresAt, DateTimeOffset? AcceptedAt);

/// <summary>The token is shown once, here; only its hash is kept.</summary>
public sealed record CreatedInvitationResponse(Guid Id, string Email, UserRole Role, DateTimeOffset ExpiresAt, string Token);

public sealed record UserResponse(Guid Id, string FullName, string Email, UserRole Role, DateTimeOffset CreatedAt, int? AvatarVersion, bool Removed);

public sealed class RoleRequest
{
    [Required(ErrorMessage = "Rol gerekli.")]
    public UserRole? Role { get; set; }
}

/// <summary>Users and invitations of the signed-in user's company. Admins only.</summary>
[ApiController]
[Route("api")]
[Authorize(Policy = Policies.Admin)]
public sealed class TeamController(AppDbContext db, TenantContext tenant, TimeProvider clock) : ControllerBase
{
    public static readonly TimeSpan InvitationLifetime = TimeSpan.FromDays(7);

    [HttpGet("users")]
    public Task<List<UserResponse>> Users(CancellationToken ct)
    {
        // Users have no query filter (sign-in must find them), so the company is named here.
        var companyId = tenant.CompanyId;
        return db.Users.Where(u => u.CompanyId == companyId).OrderBy(u => u.CreatedAt)
            .Select(u => new UserResponse(u.Id, u.FullName, u.Email!, u.Role, u.CreatedAt, u.Avatar != null ? u.AvatarVersion : null, u.LockoutEnd == DateTimeOffset.MaxValue))
            .ToListAsync(ct);
    }

    [HttpGet("invitations")]
    public Task<List<InvitationResponse>> Invitations(CancellationToken ct) =>
        db.Invitations.OrderByDescending(i => i.CreatedAt)
            .Select(i => new InvitationResponse(i.Id, i.Email, i.Role, i.ExpiresAt, i.AcceptedAt))
            .ToListAsync(ct);

    [HttpGet("invitations/{id:guid}")]
    public async Task<ActionResult<InvitationResponse>> Invitation(Guid id, CancellationToken ct)
    {
        // Another company's invitation is filtered out, so it is a 404, not a 403: its existence is not revealed.
        var invitation = await db.Invitations.Where(i => i.Id == id)
            .Select(i => new InvitationResponse(i.Id, i.Email, i.Role, i.ExpiresAt, i.AcceptedAt))
            .SingleOrDefaultAsync(ct);
        return invitation is null ? NotFound() : invitation;
    }

    [HttpPost("invitations")]
    public async Task<IActionResult> Invite(CreateInvitationRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim();
        if (await db.Users.AnyAsync(u => u.NormalizedEmail == email.ToUpperInvariant(), ct))
        {
            ModelState.AddModelError("email", "Bu e-posta adresiyle bir hesap zaten var.");
            return ValidationProblem(ModelState);
        }

        var token = TokenService.NewSecret();
        var now = clock.GetUtcNow();
        var invitation = new Invitation
        {
            Id = Guid.NewGuid(),
            Email = email,
            Role = request.Role!.Value,
            TokenHash = TokenService.Hash(token),
            InvitedByUserId = Guid.Parse(User.FindFirstValue(KasaClaims.UserId)!),
            CreatedAt = now,
            ExpiresAt = now + InvitationLifetime,
        };
        db.Invitations.Add(invitation);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Invitation), new { id = invitation.Id },
            new CreatedInvitationResponse(invitation.Id, invitation.Email, invitation.Role, invitation.ExpiresAt, token));
    }

    /// <summary>
    /// Takes a person's access away: locked for good and every session revoked. The row stays, because match
    /// decisions name who made them. An access token already handed out works for at most its 15 minutes.
    /// </summary>
    [HttpDelete("users/{id:guid}")]
    public async Task<IActionResult> RemoveUser(Guid id, [FromServices] RefreshTokens refreshTokens, CancellationToken ct)
    {
        if (id == Guid.Parse(User.FindFirstValue(KasaClaims.UserId)!))
        {
            return Problem("Kendi erişiminizi kapatamazsınız.", statusCode: StatusCodes.Status400BadRequest);
        }

        // Users carry no query filter (sign-in must find them), so the company is named here.
        var companyId = tenant.CompanyId;
        var removed = await db.Users.Where(u => u.Id == id && u.CompanyId == companyId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.LockoutEnabled, true).SetProperty(u => u.LockoutEnd, DateTimeOffset.MaxValue), ct);
        if (removed == 0)
        {
            return NotFound();
        }

        await refreshTokens.RevokeAllAsync(id, ct);
        return NoContent();
    }

    /// <summary>The new role applies from the person's next access token, within 15 minutes.</summary>
    [HttpPut("users/{id:guid}/role")]
    public async Task<IActionResult> ChangeRole(Guid id, RoleRequest request, CancellationToken ct)
    {
        if (id == Guid.Parse(User.FindFirstValue(KasaClaims.UserId)!))
        {
            return Problem("Kendi rolünüzü değiştiremezsiniz; şirket yöneticisiz kalmasın.", statusCode: StatusCodes.Status400BadRequest);
        }

        var companyId = tenant.CompanyId;
        var role = request.Role!.Value;
        var changed = await db.Users.Where(u => u.Id == id && u.CompanyId == companyId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, role), ct);
        return changed == 0 ? NotFound() : NoContent();
    }

    /// <summary>An invitation not accepted yet stops working; another company's is filtered out, so it is a 404.</summary>
    [HttpDelete("invitations/{id:guid}")]
    public async Task<IActionResult> CancelInvitation(Guid id, CancellationToken ct) =>
        await db.Invitations.Where(i => i.Id == id && i.AcceptedAt == null).ExecuteDeleteAsync(ct) == 0 ? NotFound() : NoContent();
}
