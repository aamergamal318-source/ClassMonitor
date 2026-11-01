using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ClassMonitor.Shared.Management;

/// <summary>Current-user Windows CNG key plus My certificate store; no private-key file is exported.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsIdentityStore
{
    public static X509Certificate2 GetOrCreate(string role)
    {
        if (role is not ("Agent" or "Teacher")) throw new ArgumentOutOfRangeException(nameof(role));
        string subject = $"CN=ClassMonitor {role} Identity v1";
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        var candidates = store.Certificates.Cast<X509Certificate2>()
            .Where(c => c.Subject == subject && c.HasPrivateKey && CertificateTrust.Matches(c, CertificateTrust.Fingerprint(c))
                && CertificateTrust.SupportsMutualTls(c))
            .OrderByDescending(c => c.NotAfter).ToArray();
        if (candidates.Length != 0) return candidates[0];

        // A unique persisted key name avoids overwriting an expired/previous identity.
        // Windows protects CurrentUser CNG keys and the key is explicitly non-exportable.
        var parameters = new CngKeyCreationParameters
        {
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
            KeyUsage = CngKeyUsages.Signing | CngKeyUsages.Decryption,
            ExportPolicy = CngExportPolicies.None,
            KeyCreationOptions = CngKeyCreationOptions.None
        };
        parameters.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(3072), CngPropertyOptions.None));
        using var key = CngKey.Create(CngAlgorithm.Rsa, $"ClassMonitor-{role}-{Guid.NewGuid():N}", parameters);
        using var rsa = new RSACng(key);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
        {
            new("1.3.6.1.5.5.7.3.1"), new("1.3.6.1.5.5.7.3.2")
        }, false));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(2));
        store.Add(generated);
        string fingerprint = CertificateTrust.Fingerprint(generated);
        return store.Certificates.Cast<X509Certificate2>().First(c => c.HasPrivateKey && CertificateTrust.Fingerprint(c) == fingerprint);
    }
}
