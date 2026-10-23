using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 13b item 4: the Data Protection key ring is optionally encrypted with an RSA certificate
/// from a PEM pair (a <c>kubernetes.io/tls</c> Secret's shape). The tests generate their own
/// certificates; nothing here touches a cluster or a real Secret. The key ring is a file ring
/// (DataProtection:KeysPath) so no container is needed; the database ring uses the same builder.
/// </summary>
public class KeyRingEncryptionTests
{
    private const string Purpose = "blog-web-tests";

    [Fact]
    public void WithACertificate_TheKeyXmlIsEncrypted()
    {
        var keys = AuthCookie.CreateKeysDirectory();
        var (certificate, key) = WritePair("current", rsa: true);

        using var factory = CreateFactory(keys, ("DataProtection:CertificatePath", certificate), ("DataProtection:CertificateKeyPath", key));
        Protect(factory.Services, "payload");

        var xml = File.ReadAllText(Directory.GetFiles(keys, "*.xml").Single());
        xml.Should().Contain("<encryptedSecret", "the key is stored encrypted under the certificate");
        xml.Should().NotContain("<masterKey", "the key material is not readable from the stored XML");
    }

    [Fact]
    public void WithoutACertificate_TheKeyXmlIsPlain_AndOneWarningSaysSo()
    {
        var keys = AuthCookie.CreateKeysDirectory();
        var logs = new CapturingProvider();

        using var factory = CreateFactory(keys, logs);
        Protect(factory.Services, "payload");

        File.ReadAllText(Directory.GetFiles(keys, "*.xml").Single()).Should().Contain("<masterKey");
        logs.Warnings.Where(m => m.Contains("key ring is stored unencrypted")).Should().ContainSingle(
            "a deployment without a certificate is told once, at startup");
    }

    [Fact]
    public void WithACertificate_NoUnencryptedWarning()
    {
        var keys = AuthCookie.CreateKeysDirectory();
        var (certificate, key) = WritePair("current", rsa: true);
        var logs = new CapturingProvider();

        using var factory = CreateFactory(keys, logs, ("DataProtection:CertificatePath", certificate), ("DataProtection:CertificateKeyPath", key));
        Protect(factory.Services, "payload");

        logs.Warnings.Should().NotContain(m => m.Contains("unencrypted"));
    }

    [Fact]
    public void ConfiguredPathsWithNoFiles_StartWithTheWarning_ForASiteWithoutTheOptionalSecret()
    {
        var logs = new CapturingProvider();
        var missing = Path.Combine(AuthCookie.CreateKeysDirectory(), "not-mounted");

        using var factory = CreateFactory(AuthCookie.CreateKeysDirectory(), logs,
            ("DataProtection:CertificatePath", Path.Combine(missing, "tls.crt")),
            ("DataProtection:CertificateKeyPath", Path.Combine(missing, "tls.key")));
        Protect(factory.Services, "payload");

        logs.Warnings.Where(m => m.Contains("key ring is stored unencrypted") && m.Contains("do not exist") && m.Contains("neither does their directory")).Should().ContainSingle();
    }

    [Fact]
    public void ConfiguredPathsInAnEmptyDirectory_StartWithTheWarning_NamingTheEmptyDirectory()
    {
        var logs = new CapturingProvider();
        var directory = AuthCookie.CreateKeysDirectory();

        using var factory = CreateFactory(AuthCookie.CreateKeysDirectory(), logs,
            ("DataProtection:CertificatePath", Path.Combine(directory, "tls.crt")),
            ("DataProtection:CertificateKeyPath", Path.Combine(directory, "tls.key")));
        Protect(factory.Services, "payload");

        logs.Warnings.Should().ContainSingle(m => m.Contains("unencrypted") && m.Contains("is empty"));
    }

    [Fact]
    public void ADirectoryWithOtherFiles_FailsStartup_NamingTheFilesAndTheDirectory()
    {
        // A Secret mounted under other key names (certificate.pem / certificate.key, say).
        var directory = AuthCookie.CreateKeysDirectory();
        File.WriteAllText(Path.Combine(directory, "certificate.pem"), "x");
        var crt = Path.Combine(directory, "tls.crt");
        var key = Path.Combine(directory, "tls.key");
        using var factory = CreateFactory(AuthCookie.CreateKeysDirectory(),
            ("DataProtection:CertificatePath", crt), ("DataProtection:CertificateKeyPath", key));

        var start = () => factory.CreateClient();

        start.Should().Throw<InvalidOperationException>().WithMessage($"*{crt}*{key}*holds other files*");
    }

    [Fact]
    public void PreviousCertificatesWithoutTheCurrentPair_FailStartup()
    {
        var (oldCertificate, oldKey) = WritePair("old", rsa: true);
        var missing = Path.Combine(AuthCookie.CreateKeysDirectory(), "not-mounted");
        using var factory = CreateFactory(AuthCookie.CreateKeysDirectory(),
            ("DataProtection:CertificatePath", Path.Combine(missing, "tls.crt")),
            ("DataProtection:CertificateKeyPath", Path.Combine(missing, "tls.key")),
            ("DataProtection:PreviousCertificatePaths:0:CertificatePath", oldCertificate),
            ("DataProtection:PreviousCertificatePaths:0:CertificateKeyPath", oldKey));

        var start = () => factory.CreateClient();

        start.Should().Throw<InvalidOperationException>().WithMessage("*PreviousCertificatePaths is set but the current pair*");
    }

    [Fact]
    public void AnEcCertificate_FailsStartup_NamingRsa()
    {
        var (certificate, key) = WritePair("ec", rsa: false);
        using var factory = CreateFactory(AuthCookie.CreateKeysDirectory(),
            ("DataProtection:CertificatePath", certificate), ("DataProtection:CertificateKeyPath", key));

        var start = () => factory.CreateClient();

        start.Should().Throw<InvalidOperationException>().WithMessage("*not an RSA private key*EC keys*",
            "an EC key cannot encrypt the key ring; the message says what to issue instead");
    }

    [Fact]
    public void AnUnreadableCertificate_FailsStartup_NamingTheFiles()
    {
        var directory = AuthCookie.CreateKeysDirectory();
        var certificate = Path.Combine(directory, "garbage.crt");
        var key = Path.Combine(directory, "garbage.key");
        File.WriteAllText(certificate, "not a certificate");
        File.WriteAllText(key, "not a key");
        using var factory = CreateFactory(AuthCookie.CreateKeysDirectory(),
            ("DataProtection:CertificatePath", certificate), ("DataProtection:CertificateKeyPath", key));

        var start = () => factory.CreateClient();

        start.Should().Throw<InvalidOperationException>().WithMessage($"*{certificate}*RSA*");
    }

    [Fact]
    public void OnlyOneOfCertificateAndKey_FailsStartup()
    {
        var (certificate, _) = WritePair("current", rsa: true);
        using var factory = CreateFactory(AuthCookie.CreateKeysDirectory(), ("DataProtection:CertificatePath", certificate));

        var start = () => factory.CreateClient();

        start.Should().Throw<InvalidOperationException>().WithMessage("*both CertificatePath and CertificateKeyPath are required*");
    }

    [Fact]
    public void AKeyEncryptedUnderAPreviousCertificate_IsStillRead_AfterTheCurrentOneChanged()
    {
        var keys = AuthCookie.CreateKeysDirectory();
        var (oldCertificate, oldKey) = WritePair("old", rsa: true);
        var (newCertificate, newKey) = WritePair("new", rsa: true);

        string ciphertext;
        using (var first = CreateFactory(keys, ("DataProtection:CertificatePath", oldCertificate), ("DataProtection:CertificateKeyPath", oldKey)))
        {
            ciphertext = Protect(first.Services, "payload");
        }

        using var rotated = CreateFactory(keys,
            ("DataProtection:CertificatePath", newCertificate), ("DataProtection:CertificateKeyPath", newKey),
            ("DataProtection:PreviousCertificatePaths:0:CertificatePath", oldCertificate),
            ("DataProtection:PreviousCertificatePaths:0:CertificateKeyPath", oldKey));
        Unprotect(rotated.Services, ciphertext).Should().Be("payload", "the old pair is listed, so its keys still read");

        using var withoutPrevious = CreateFactory(keys, ("DataProtection:CertificatePath", newCertificate), ("DataProtection:CertificateKeyPath", newKey));
        var act = () => Unprotect(withoutPrevious.Services, ciphertext);
        act.Should().Throw<CryptographicException>("the control: without the old pair the key cannot be decrypted");
    }

    /// <summary>Writes a self-signed certificate and its unencrypted private key as PEM files, like a tls Secret's tls.crt and tls.key.</summary>
    private static (string Certificate, string Key) WritePair(string name, bool rsa)
    {
        var directory = AuthCookie.CreateKeysDirectory();
        var certificatePath = Path.Combine(directory, $"{name}.crt");
        var keyPath = Path.Combine(directory, $"{name}.key");
        var subject = new X500DistinguishedName($"CN=croft-keyring-test-{name}");
        using var rsaKey = rsa ? RSA.Create(2048) : null;
        using var ecKey = rsa ? null : ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = rsaKey is not null
            ? new CertificateRequest(subject, rsaKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            : new CertificateRequest(subject, ecKey!, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
        File.WriteAllText(keyPath, rsaKey is not null ? rsaKey.ExportPkcs8PrivateKeyPem() : ecKey!.ExportPkcs8PrivateKeyPem());
        return (certificatePath, keyPath);
    }

    private static WebApplicationFactory<Program> CreateFactory(string keys, params (string Key, string Value)[] settings) =>
        CreateFactory(keys, null, settings);

    private static WebApplicationFactory<Program> CreateFactory(string keys, CapturingProvider? logs, params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("DataProtection:KeysPath", keys);
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            if (logs is not null)
            {
                builder.ConfigureLogging(logging => logging.AddProvider(logs));
            }
        });

    private static string Protect(IServiceProvider services, string plaintext) =>
        services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect(plaintext);

    private static string Unprotect(IServiceProvider services, string ciphertext) =>
        services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Unprotect(ciphertext);

    private sealed class CapturingProvider : ILoggerProvider
    {
        private readonly List<string> _warnings = [];

        public IReadOnlyList<string> Warnings
        {
            get { lock (_warnings) { return [.. _warnings]; } }
        }

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose() => GC.SuppressFinalize(this);

        private sealed class Logger(CapturingProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning)
                {
                    lock (owner._warnings) { owner._warnings.Add(formatter(state, exception)); }
                }
            }
        }
    }
}
