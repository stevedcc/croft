using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Croft.Shared;
using Croft.Web.Authentication;
using Croft.Web.Data;
using Croft.Web.Options;
using Croft.Web.Services;
using Croft.Web.Tests.Fixtures;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 13b item 4 (review finding 9): the shared token store keeps the access, refresh and ID
/// tokens protected, never in clear text. The tests read the rows directly, because the store's own
/// reads would hide a missing protection.
/// </summary>
[Trait("Category", "Container")]
public class TokenStoreProtectionTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AfterASignIn_TheThreeColumnsHoldNoRawToken_AndTheStoreReadsThemBack()
    {
        using var factory = CreateFactory();
        var store = factory.Services.GetRequiredService<ITokenStore>();
        var identity = new ClaimsIdentity([new Claim("sub", "admin-id")], "test");
        var response = new OpenIdConnectMessage
        {
            AccessToken = "raw-access-token-1",
            RefreshToken = "raw-refresh-token-1",
            IdToken = "raw-id-token-1",
            ExpiresIn = "300",
        };

        await TokenSessionEvents.OnTokenValidated(NewContext(identity, response, store));

        var sessionId = identity.FindFirst("blog_sid")!.Value;
        var row = await ReadRowAsync(sessionId);
        row.AccessToken.Should().NotContain("raw-access-token-1").And.NotBeEmpty();
        row.RefreshToken.Should().NotContain("raw-refresh-token-1").And.NotBeNullOrEmpty();
        row.IdToken.Should().NotContain("raw-id-token-1").And.NotBeNullOrEmpty();
        var entry = await store.GetAsync(sessionId);
        entry!.AccessToken.Should().Be("raw-access-token-1");
        entry.RefreshToken.Should().Be("raw-refresh-token-1");
        entry.IdToken.Should().Be("raw-id-token-1");
    }

    [Fact]
    public async Task ASessionWithoutRefreshOrIdToken_RoundTrips()
    {
        using var factory = CreateFactory();
        var store = factory.Services.GetRequiredService<ITokenStore>();

        await store.SetAsync("s-bare", new TokenEntry("at-only", null, Now));

        (await store.GetAsync("s-bare")).Should().Be(new TokenEntry("at-only", null, Now));
    }

    [Fact]
    public async Task ARowWrittenInClearTextByAnEarlierRelease_ReadsAsAMissingSession()
    {
        using var factory = CreateFactory();
        var store = factory.Services.GetRequiredService<ITokenStore>();
        await using (var db = CreateDbContext())
        {
            db.UserSessions.Add(new UserSession
            {
                SessionId = "s-plain",
                AccessToken = "eyJhbGciOiJSUzI1NiJ9.plain.access",
                RefreshToken = "plain-refresh",
                IdToken = "eyJhbGciOiJSUzI1NiJ9.plain.id",
                ExpiresAt = Now,
            });
            await db.SaveChangesAsync();
        }

        (await store.GetAsync("s-plain")).Should().BeNull("a clear-text row cannot be unprotected, so the user signs in again");
    }

    [Fact]
    public async Task ARowThatAnotherKeyRingProtected_ReadsAsAMissingSession_AndTheLockedPathSeesNone()
    {
        using var factory = CreateFactory();
        using var other = CreateFactory(distinctKeys: true);
        await other.Services.GetRequiredService<ITokenStore>().SetAsync("s-foreign", new TokenEntry("at", "rt", Now));

        var store = factory.Services.GetRequiredService<ITokenStore>();
        (await store.GetAsync("s-foreign")).Should().BeNull("a rotated-away key reads as no session");
        var locked = await factory.Services.GetRequiredService<ISessionLock>().RunLockedAsync(
            "s-foreign", (session, _) => Task.FromResult(session.Entry));
        locked.Should().BeNull();
    }

    [Fact]
    public async Task WithACertificate_TheKeyRingRowsAreEncrypted()
    {
        // This class's database may already hold a key an earlier test's host created without a certificate.
        await using (var cleanup = CreateDbContext())
        {
            await cleanup.DataProtectionKeys.ExecuteDeleteAsync();
        }

        var directory = AuthCookie.CreateKeysDirectory();
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=croft-keyring-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var certificatePath = Path.Combine(directory, "tls.crt");
        var keyPath = Path.Combine(directory, "tls.key");
        File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
        File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());

        using var factory = CreateFactory(settings:
        [
            ("DataProtection:CertificatePath", certificatePath),
            ("DataProtection:CertificateKeyPath", keyPath),
        ]);
        var store = factory.Services.GetRequiredService<ITokenStore>();
        await store.SetAsync("s-cert", new TokenEntry("at-cert", "rt-cert", Now));

        await using var db = CreateDbContext();
        var keyXml = (await db.DataProtectionKeys.AsNoTracking().ToListAsync()).Select(k => k.Xml!).ToList();
        keyXml.Should().NotBeEmpty();
        keyXml.Should().OnlyContain(xml => xml.Contains("<encryptedSecret") && !xml.Contains("<masterKey"),
            "with a certificate configured no key is stored in readable form");
        (await store.GetAsync("s-cert"))!.AccessToken.Should().Be("at-cert");
    }

    private async Task<UserSession> ReadRowAsync(string sessionId)
    {
        await using var db = CreateDbContext();
        return await db.UserSessions.AsNoTracking().SingleAsync(s => s.SessionId == sessionId);
    }

    private CroftWebDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<CroftWebDbContext>().UseNpgsql(fixture.ConnectionString).Options);

    private static TokenValidatedContext NewContext(ClaimsIdentity identity, OpenIdConnectMessage response, ITokenStore store)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        services.AddSingleton(new AuthSettings(TimeSpan.FromDays(30)));
        services.Configure<RoleGroupSettings>(new ConfigurationBuilder().Build().GetSection(RoleGroupSettings.SectionName));
        return new TokenValidatedContext(
            new DefaultHttpContext { RequestServices = services.BuildServiceProvider() },
            new AuthenticationScheme("OpenIdConnect", null, typeof(OpenIdConnectHandler)),
            new OpenIdConnectOptions(),
            new ClaimsPrincipal(identity),
            new AuthenticationProperties())
        {
            TokenEndpointResponse = response,
        };
    }

    // With distinctKeys the host protects with a key ring of its own (a file ring instead of the
    // shared database ring) on the same database: what a rotated-away key looks like.
    private WebApplicationFactory<Program> CreateFactory(bool distinctKeys = false, params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("ConnectionStrings:WebDb", fixture.ConnectionString);
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            if (distinctKeys)
            {
                builder.ConfigureServices(services => services
                    .AddDataProtection()
                    .SetApplicationName("another-ring")
                    .PersistKeysToFileSystem(new DirectoryInfo(AuthCookie.CreateKeysDirectory())));
            }
        });
}
