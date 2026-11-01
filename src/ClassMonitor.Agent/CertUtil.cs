using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ClassMonitor.Shared;

namespace ClassMonitor.Agent;

/// <summary>
/// Provides the agent's TLS certificate. A self-signed cert is generated once and cached as a
/// .pfx next to the config, then reused. The teacher does not validate this cert (there is no
/// PKI on a classroom LAN); trust is established by the shared-password handshake instead — the
/// cert only provides the encrypted channel.
/// </summary>
internal static class CertUtil
{
    private static string CertPath =>
        Path.Combine(Path.GetDirectoryName(AppConfig.AgentConfigPath)!, "agent-cert.pfx");

    public static X509Certificate2 GetOrCreate()
    {
        try
        {
            if (File.Exists(CertPath))
                return X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(CertPath), "",
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
        }
        catch { /* regenerate below */ }

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=ClassMonitor Agent", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // serverAuth

        using var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(10));
        byte[] pfx = cert.Export(X509ContentType.Pfx, "");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CertPath)!);
            File.WriteAllBytes(CertPath, pfx);
        }
        catch { /* read-only location; use the in-memory cert this run */ }

        // Reload from PFX so the private key is reliably associated for SslStream on Windows.
        return X509CertificateLoader.LoadPkcs12(pfx, "",
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
    }
}
