using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Croft.Api.Tests;

public class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string RoleHeader = "X-Test-Role";

    /// <summary>The caller's <c>sub</c>; <see cref="DefaultSub"/> when the header is absent.</summary>
    public const string SubHeader = "X-Test-Sub";

    /// <summary>The caller's display name; <see cref="DefaultName"/> when the header is absent.</summary>
    public const string NameHeader = "X-Test-Name";

    public const string DefaultSub = "test-user-id";
    public const string DefaultName = "Test User";

    /// <summary>
    /// Marks a request as genuinely unauthenticated (the handler returns NoResult, so
    /// HttpContext.User is the anonymous principal). Without it every test request is
    /// authenticated — "no role" is not "no user" — which is what most tests want.
    /// </summary>
    public const string AnonymousHeader = "X-Test-Anonymous";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers.ContainsKey(AnonymousHeader))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var role = Request.Headers[RoleHeader].FirstOrDefault() ?? string.Empty;
        var sub = Request.Headers[SubHeader].FirstOrDefault() ?? DefaultSub;
        var name = Request.Headers[NameHeader].FirstOrDefault() ?? DefaultName;
        var claims = new List<Claim>
        {
            new("name", name),
            new("sub", sub),
            new(ClaimTypes.Name, name),
            new(ClaimTypes.NameIdentifier, sub)
        };

        if (!string.IsNullOrWhiteSpace(role))
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
