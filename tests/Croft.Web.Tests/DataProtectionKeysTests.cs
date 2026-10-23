using System.Security.Cryptography;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests;

public class DataProtectionKeysTests
{
    private const string Purpose = "blog-web-tests";

    [Fact]
    public void KeysPath_Configured_PersistsKeysToConfiguredDirectory()
    {
        var keysPath = CreateKeysDirectory();

        using var factory = CreateFactory(keysPath);
        Protect(factory.Services, "payload");

        Directory.GetFiles(keysPath, "*.xml").Should().NotBeEmpty("the key ring must be written where DataProtection:KeysPath points");
    }

    [Fact]
    public void KeysPath_Configured_KeyRingSharedWithSecondAppInstance()
    {
        var keysPath = CreateKeysDirectory();

        string ciphertext;
        using (var first = CreateFactory(keysPath))
        {
            ciphertext = Protect(first.Services, "payload");
        }

        using var second = CreateFactory(keysPath);
        Unprotect(second.Services, ciphertext).Should().Be("payload");
    }

    [Fact]
    public void KeysPath_Configured_KeysScopedToBlogWebApplicationName()
    {
        var keysPath = CreateKeysDirectory();

        using var factory = CreateFactory(keysPath);
        var ciphertext = Protect(factory.Services, "payload");

        var sameName = CreateStandaloneProvider(keysPath, "blog-web");
        sameName.CreateProtector(Purpose).Unprotect(ciphertext).Should().Be("payload");

        var otherName = CreateStandaloneProvider(keysPath, "some-other-app");
        var act = () => otherName.CreateProtector(Purpose).Unprotect(ciphertext);
        act.Should().Throw<CryptographicException>("a different application name must not read blog-web's keys");
    }

    [Fact]
    public void KeysPath_NotConfigured_ProtectsAndUnprotectsAsBefore()
    {
        using var factory = CreateFactory(keysPath: null);

        var ciphertext = Protect(factory.Services, "payload");

        Unprotect(factory.Services, ciphertext).Should().Be("payload");
    }

    private static string CreateKeysDirectory() => AuthCookie.CreateKeysDirectory();

    // Settings are applied with UseSetting: it writes into builder.Configuration before the app
    // builds its host, which is when the data protection setup reads DataProtection:KeysPath.
    // ConfigureAppConfiguration sources only merge in at Build() time, too late for that read.
    private static WebApplicationFactory<Program> CreateFactory(string? keysPath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // Startup reads it when an HttpClient is created; these tests make no HTTP requests.
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            if (keysPath is not null)
            {
                builder.UseSetting("DataProtection:KeysPath", keysPath);
            }
        });

    private static IDataProtectionProvider CreateStandaloneProvider(string keysPath, string applicationName)
    {
        var services = new ServiceCollection();
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
            .SetApplicationName(applicationName);
        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
    }

    private static string Protect(IServiceProvider services, string plaintext) =>
        services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect(plaintext);

    private static string Unprotect(IServiceProvider services, string ciphertext) =>
        services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Unprotect(ciphertext);
}
