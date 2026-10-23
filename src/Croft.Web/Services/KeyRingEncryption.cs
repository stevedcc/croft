using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;

namespace Croft.Web.Services;

/// <summary>
/// Optional encryption of the Data Protection key ring with a certificate (brief 13b, review
/// finding 9). The key ring lives in the Web database next to the sessions it protects, so a copy of the
/// database alone yields every key; with a certificate whose private key is in a Secret (not in the
/// database), the stored key XML is encrypted and a database copy alone yields no usable key.
/// <list type="bullet">
/// <item><c>DataProtection:CertificatePath</c> and <c>DataProtection:CertificateKeyPath</c>: a PEM
/// pair (the shape of a <c>kubernetes.io/tls</c> Secret mount, <c>tls.crt</c> and <c>tls.key</c>), no
/// password. The key must be RSA: the key ring is encrypted with EncryptedXml, which cannot use EC keys.</item>
/// <item><c>DataProtection:PreviousCertificatePaths:N:CertificatePath</c> / <c>:CertificateKeyPath</c>:
/// earlier pairs, still used to read keys that were encrypted under them (rotation).</item>
/// </list>
/// Data Protection checks neither a certificate's chain nor its expiry when it decrypts, so a
/// renewed certificate only needs the old pair kept until the keys it encrypted have expired.
/// </summary>
public static partial class KeyRingEncryption
{
    /// <summary>
    /// Encrypts the key ring with the configured certificate; false when none is configured (the
    /// ring is then stored unencrypted, <paramref name="reason"/> says why). Both settings pointing
    /// at files that do not exist counts as "none configured" only when their directory is absent or
    /// empty (the k8s base mounts its optional Secret there, so a site without the Secret still
    /// starts); a directory with other files in it, or previous pairs without a current one, fail startup. A file that exists but is
    /// unreadable, or a non-RSA key, throws, so startup fails.
    /// </summary>
    public static bool Apply(IDataProtectionBuilder builder, IConfiguration configuration, out string reason)
    {
        reason = "DataProtection:CertificatePath is not set";
        var section = configuration.GetSection("DataProtection");
        var certificatePath = section["CertificatePath"];
        var keyPath = section["CertificateKeyPath"];
        var previous = section.GetSection("PreviousCertificatePaths").GetChildren()
            .Select(child => LoadPair(
                child["CertificatePath"], child["CertificateKeyPath"], $"DataProtection:PreviousCertificatePaths:{child.Key}"))
            .ToArray();

        if (string.IsNullOrWhiteSpace(certificatePath) && string.IsNullOrWhiteSpace(keyPath))
        {
            if (previous.Length > 0)
            {
                throw new InvalidOperationException(
                    "DataProtection:PreviousCertificatePaths is set but DataProtection:CertificatePath and DataProtection:CertificateKeyPath are not: " +
                    "previous certificates only read old keys, the current one encrypts new ones.");
            }

            return false;
        }

        if (!string.IsNullOrWhiteSpace(certificatePath) && !string.IsNullOrWhiteSpace(keyPath)
            && !File.Exists(certificatePath) && !File.Exists(keyPath))
        {
            // Previous pairs only read old keys: without a current pair they would be ignored silently.
            if (previous.Length > 0)
            {
                throw new InvalidOperationException(
                    $"DataProtection:PreviousCertificatePaths is set but the current pair ('{certificatePath}' and '{keyPath}', " +
                    "DataProtection:CertificatePath / DataProtection:CertificateKeyPath) does not exist: the keys would silently stay unencrypted.");
            }

            // A directory that holds files, none of them the expected ones, is a Secret mounted
            // under other key names: a misconfiguration, not an absent Secret.
            var directory = Path.GetDirectoryName(certificatePath);
            if (directory is not null && Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            {
                throw new InvalidOperationException(
                    $"DataProtection:CertificatePath '{certificatePath}' and DataProtection:CertificateKeyPath '{keyPath}' do not exist, " +
                    $"but their directory '{directory}' holds other files (a Secret mounted under other key names?). " +
                    "Name the files in the settings, or remove the settings and the mount to run without a certificate.");
            }

            reason = Directory.Exists(directory)
                ? $"the files '{certificatePath}' and '{keyPath}' do not exist and their directory '{directory}' is empty (the optional certificate Secret is not mounted)"
                : $"the files '{certificatePath}' and '{keyPath}' do not exist and neither does their directory (the optional certificate Secret is not mounted)";
            return false;
        }

        builder.ProtectKeysWithCertificate(LoadPair(certificatePath, keyPath, "DataProtection"));
        if (previous.Length > 0)
        {
            builder.UnprotectKeysWithAnyCertificate(previous);
        }

        return true;
    }

    /// <summary>The one Warning a deployment without a certificate logs at startup.</summary>
    [LoggerMessage(Level = LogLevel.Warning, Message =
        "The Data Protection key ring is stored unencrypted ({Reason}): a copy of the Web database yields the keys, and with them the stored session tokens. " +
        "Set DataProtection:CertificatePath and DataProtection:CertificateKeyPath (an RSA certificate and key in PEM form) to encrypt it.")]
    public static partial void WarnUnencrypted(ILogger logger, string reason);

    private static X509Certificate2 LoadPair(string? certificatePath, string? keyPath, string setting)
    {
        if (string.IsNullOrWhiteSpace(certificatePath) || string.IsNullOrWhiteSpace(keyPath))
        {
            throw new InvalidOperationException(
                $"{setting}: both CertificatePath and CertificateKeyPath are required (a PEM certificate and its RSA private key).");
        }

        X509Certificate2 certificate;
        try
        {
            certificate = X509Certificate2.CreateFromPemFile(certificatePath, keyPath);
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or ArgumentException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"{setting}: the certificate '{certificatePath}' and key '{keyPath}' could not be read as a PEM pair with an unencrypted RSA private key " +
                $"(the Data Protection key ring is encrypted with RSA only): {exception.Message}", exception);
        }

        if (certificate.GetRSAPrivateKey() is null)
        {
            certificate.Dispose();
            throw new InvalidOperationException(
                $"{setting}: the key '{keyPath}' is not an RSA private key. The Data Protection key ring is encrypted with EncryptedXml, which cannot use EC keys: issue an RSA certificate.");
        }

        return certificate;
    }
}
