using Croft.Web.Services;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Croft.Web.Tests.Fixtures;

/// <summary>
/// Makes every request of a test factory a signed-in admin (and gives the token service a fixed
/// token), so the staff pages render as documents and a circuit may start: circuits are for
/// signed-in staff only.
/// </summary>
internal static class SignedInAdmin
{
    public static void AddTo(IServiceCollection services)
    {
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, Handler>("TestAdmin", _ => { });
        services.PostConfigure<AuthenticationOptions>(options =>
        {
            options.DefaultScheme = "TestAdmin";
            options.DefaultAuthenticateScheme = "TestAdmin";
        });
        services.AddSingleton<IUserAccessTokenService, FixedToken>();
    }

    private sealed class FixedToken : IUserAccessTokenService
    {
        public Task<string?> GetAccessTokenAsync(ClaimsPrincipal user, CancellationToken ct = default) =>
            Task.FromResult<string?>("test-token");
    }

    private sealed class Handler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "admin@example.com"), new Claim(ClaimTypes.Role, "Admin")], "TestAdmin");
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), "TestAdmin")));
        }
    }
}
