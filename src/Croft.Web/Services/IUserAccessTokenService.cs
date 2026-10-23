using System.Security.Claims;

namespace Croft.Web.Services;

public interface IUserAccessTokenService
{
    Task<string?> GetAccessTokenAsync(ClaimsPrincipal user, CancellationToken ct = default);
}
