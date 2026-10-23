using System.Security.Claims;
using Croft.Web.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Croft.Web.Tests;

/// <summary>
/// Signs in and authenticates through the real cookie handler on a factory's services — a real
/// auth cookie out, a real authenticate in — so tests observe what a browser would. The handler's
/// services are scoped, so each call opens its own scope like a request would.
/// </summary>
internal static class AuthCookie
{
    public static async Task<string> SignInAsync(IServiceProvider services, string sessionId, params string[] roles)
    {
        var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
        identity.AddClaim(new Claim(ClaimTypes.Name, "admin@example.com"));
        identity.AddClaim(new Claim(TokenSessionEvents.SessionIdClaim, sessionId));
        foreach (var role in roles)
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }

        using var scope = services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        var options = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        return CookiePair(options.Cookie.Name!, context.Response.Headers[HeaderNames.SetCookie]);
    }

    /// <summary>The whole Set-Cookie header the sign-in writes (attributes included), for tests
    /// that check what the browser is told about the cookie.</summary>
    public static async Task<string> SignInSetCookieAsync(IServiceProvider services, string sessionId)
    {
        var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
        identity.AddClaim(new Claim(ClaimTypes.Name, "admin@example.com"));
        identity.AddClaim(new Claim(TokenSessionEvents.SessionIdClaim, sessionId));
        using var scope = services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return string.Join("; ", context.Response.Headers[HeaderNames.SetCookie].ToArray());
    }

    public static async Task<AuthenticateResult> AuthenticateAsync(IServiceProvider services, string cookie)
    {
        using var scope = services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Headers[HeaderNames.Cookie] = cookie;
        return await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    private static readonly System.Collections.Concurrent.ConcurrentBag<string> KeysDirectories = [];

    static AuthCookie() =>
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var path in KeysDirectories)
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
        };

    /// <summary>A key-ring folder for a host under test, removed when the test process ends (the hosts
    /// keep it open until they are disposed, and several tests share the process).</summary>
    public static string CreateKeysDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"blog-web-keys-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        KeysDirectories.Add(path);
        return path;
    }

    private static string CookiePair(string cookieName, StringValues setCookie)
    {
        foreach (var header in setCookie)
        {
            foreach (var part in header?.Split(';') ?? [])
            {
                var pair = part.Trim();
                if (pair.StartsWith($"{cookieName}=", StringComparison.Ordinal))
                {
                    return pair;
                }
            }
        }

        throw new InvalidOperationException($"The sign-in wrote no {cookieName} cookie.");
    }
}
