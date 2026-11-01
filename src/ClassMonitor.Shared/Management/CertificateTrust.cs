using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ClassMonitor.Shared.Management;

public static class CertificateTrust
{
    public static string Fingerprint(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.RawData));

    public static bool IsFingerprint(string? fingerprint) =>
        fingerprint is { Length: 64 } && fingerprint.All(Uri.IsHexDigit);

    public static bool Matches(X509Certificate2? certificate, string? expectedFingerprint,
        DateTimeOffset? now = null)
    {
        if (certificate is null || !IsFingerprint(expectedFingerprint)) return false;
        var instant = (now ?? DateTimeOffset.UtcNow).UtcDateTime;
        if (instant < certificate.NotBefore.ToUniversalTime() || instant > certificate.NotAfter.ToUniversalTime()) return false;
        byte[] expected = Convert.FromHexString(expectedFingerprint!);
        byte[] actual = SHA256.HashData(certificate.RawData);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public static X509Certificate2 ReadPublicCertificate(string base64)
    {
        if (string.IsNullOrWhiteSpace(base64) || base64.Length > 16_384)
            throw new InvalidDataException("certificate.invalid");
        try
        {
            var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(base64));
            if (!Matches(certificate, Fingerprint(certificate)))
            {
                certificate.Dispose();
                throw new InvalidDataException("certificate.expired");
            }
            return certificate;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw new InvalidDataException("certificate.invalid", ex);
        }
    }

    public static bool SupportsMutualTls(X509Certificate2 certificate)
    {
        var usage = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        if (usage is null) return false;
        var oids = usage.EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value).ToHashSet();
        return oids.Contains("1.3.6.1.5.5.7.3.1") && oids.Contains("1.3.6.1.5.5.7.3.2");
    }
}
