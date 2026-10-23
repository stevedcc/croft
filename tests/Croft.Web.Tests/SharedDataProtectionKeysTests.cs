using Croft.Web.Authentication;
using Croft.Web.Services;
using Croft.Web.Tests.Fixtures;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests;

/// <summary>
/// With ConnectionStrings:WebDb set the Data Protection keys are shared state: an auth cookie one
/// instance protects must be unprotected and accepted by any other, or the load balancer signs
/// the user out whenever it sends them to a different instance. Two WebApplicationFactory hosts
/// on one database stand in for the two instances, and the cookie goes through the real cookie
/// handler on both sides (sign-in on A, authenticate on B).
/// </summary>
[Trait("Category", "Container")]
public class SharedDataProtectionKeysTests(PostgresDatabaseFixture fixture)
    : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task AuthCookieProtectedByInstanceA_IsRecognisedByInstanceB()
    {
        using var a = CreateFactory();
        using var b = CreateFactory();
        var sessionId = $"s-{Guid.NewGuid():N}";
        // The session is stored where both instances see it (the shared store), exactly as a
        // real login stores it — the cookie is the only thing under test here.
        await a.Services.GetRequiredService<ITokenStore>().SetAsync(sessionId, new TokenEntry(
            "at-1", "rt-1", DateTimeOffset.UtcNow.AddHours(1), "idt-1", "idp-sid-1", "admin-id"));

        var cookie = await AuthCookie.SignInAsync(a.Services, sessionId);

        var result = await AuthCookie.AuthenticateAsync(b.Services, cookie);

        result.Succeeded.Should().BeTrue(
            "any Web instance must accept an auth cookie another instance protected, or the load balancer sends the user to another pod and signs them out (failure: {0})",
            result.Failure?.Message ?? "none");
        result.Principal!.FindFirst(TokenSessionEvents.SessionIdClaim)?.Value.Should().Be(sessionId,
            "the recognised cookie must carry the signed-in user's session across");
    }

    [Fact]
    public async Task AuthCookieProtectedByInstanceA_IsNotRecognisedByAnInstanceWithItsOwnFileKeyRing()
    {
        // The control for the test above (the pre-D1 failure, "the second pod makes its own keys"): with the
        // same database and the same session, an instance whose keys are its own cannot read A's cookie.
        // If this recognised the cookie, the positive test would prove nothing about the shared keys.
        using var a = CreateFactory();
        using var b = CreateFactoryWithItsOwnFileKeyRing();
        var sessionId = $"s-{Guid.NewGuid():N}";
        await a.Services.GetRequiredService<ITokenStore>().SetAsync(sessionId, new TokenEntry(
            "at-1", "rt-1", DateTimeOffset.UtcNow.AddHours(1), "idt-1", "idp-sid-1", "admin-id"));

        var cookie = await AuthCookie.SignInAsync(a.Services, sessionId);

        var result = await AuthCookie.AuthenticateAsync(b.Services, cookie);

        result.Succeeded.Should().BeFalse(
            "keys that are not shared cannot unprotect the cookie: this is what the shared key ring fixes");
    }

    // The control fixture: instance B keeps the shared Web database (so its sessions are still the
    // shared rows) but is pointed at a file key ring of its own — the pre-D1 failure ("the second
    // pod makes its own keys") exactly. The A→B cookie must be refused under this.
    private WebApplicationFactory<Program> CreateFactoryWithItsOwnFileKeyRing()
    {
        var keysPath = AuthCookie.CreateKeysDirectory();
        return CreateFactory().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysPath))));
    }

    // Each instance also keeps its own file key ring configured — the state this change fixes.
    // With the Web database set the keys are rows in it and shared, and the file rings stay
    // empty; without this setting both hosts would fall back to the machine's shared default key
    // store and a broken implementation could still pass this test.
    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("ConnectionStrings:WebDb", fixture.ConnectionString);
            builder.UseSetting("DataProtection:KeysPath", AuthCookie.CreateKeysDirectory());
        });
}
