using System.Security.Claims;
using Kasa.Api.Auth;

namespace Kasa.Api.Common;

/// <summary>
/// The company the current work belongs to. In a request it comes from the signed-in user's token, never from the
/// request body or the URL. Background jobs and the few anonymous flows that act for one company set it with
/// <see cref="Use"/>. When nobody set it, it is null and the query filter matches nothing.
/// </summary>
public sealed class TenantContext(IHttpContextAccessor http)
{
    private Guid? _override;

    public Guid? CompanyId =>
        _override ?? (Guid.TryParse(http.HttpContext?.User.FindFirstValue(KasaClaims.CompanyId), out var id) ? id : null);

    public void Use(Guid companyId) => _override = companyId;
}
