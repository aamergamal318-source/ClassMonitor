using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using ClassMonitor.Shared.Management;

namespace ClassMonitor.Shared.Sessions;

/// <summary>Mutual TLS pins bind both endpoint identities to the encrypted channel.</summary>
public static class SecureChannel
{
    public static async Task<SslStream> ConnectAsync(TcpClient client, X509Certificate2 identity,
        string serverPin, TimeSpan timeout, CancellationToken ct)
    {
        RequireIdentity(identity);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var ssl = new SslStream(client.GetStream(), false, (_, certificate, _, _) => Validate(certificate, serverPin));
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "ClassMonitor", ClientCertificates = new X509CertificateCollection { identity },
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, deadline.Token).ConfigureAwait(false);
            if (!ssl.IsMutuallyAuthenticated) throw new AuthenticationException("Mutual endpoint authentication is required.");
            return ssl;
        }
        catch { await ssl.DisposeAsync(); throw; }
    }

    public static async Task<SslStream> AcceptAsync(TcpClient client, X509Certificate2 identity,
        string teacherPin, TimeSpan timeout, CancellationToken ct)
    {
        RequireIdentity(identity);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var ssl = new SslStream(client.GetStream(), false, (_, certificate, _, _) => Validate(certificate, teacherPin));
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = identity, ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, deadline.Token).ConfigureAwait(false);
            if (!ssl.IsMutuallyAuthenticated) throw new AuthenticationException("Mutual endpoint authentication is required.");
            return ssl;
        }
        catch { await ssl.DisposeAsync(); throw; }
    }

    /// <summary>
    /// For the pairing port only. Neither side knows the other's pin yet, so a certificate is accepted here and bound
    /// afterwards by the password proof. Nothing but pairing messages may flow on this channel.
    /// </summary>
    public static async Task<SslStream> ConnectForPairingAsync(TcpClient client, X509Certificate2 identity, TimeSpan timeout, CancellationToken ct)
    {
        RequireIdentity(identity);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var ssl = new SslStream(client.GetStream(), false, (_, certificate, _, _) => ValidForPairing(certificate));
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "ClassMonitor", ClientCertificates = new X509CertificateCollection { identity },
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, deadline.Token).ConfigureAwait(false);
            if (!ssl.IsMutuallyAuthenticated) throw new AuthenticationException("Mutual endpoint authentication is required.");
            return ssl;
        }
        catch { await ssl.DisposeAsync(); throw; }
    }

    /// <summary>See <see cref="ConnectForPairingAsync"/>.</summary>
    public static async Task<SslStream> AcceptForPairingAsync(TcpClient client, X509Certificate2 identity, TimeSpan timeout, CancellationToken ct)
    {
        RequireIdentity(identity);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var ssl = new SslStream(client.GetStream(), false, (_, certificate, _, _) => ValidForPairing(certificate));
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = identity, ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, deadline.Token).ConfigureAwait(false);
            if (!ssl.IsMutuallyAuthenticated) throw new AuthenticationException("Mutual endpoint authentication is required.");
            return ssl;
        }
        catch { await ssl.DisposeAsync(); throw; }
    }

    public static string RemotePin(SslStream stream)
    {
        using var peer = X509CertificateLoader.LoadCertificate(stream.RemoteCertificate!.GetRawCertData());
        return CertificateTrust.Fingerprint(peer);
    }

    private static bool ValidForPairing(X509Certificate? certificate)
    {
        if (certificate is null) return false;
        try
        {
            using var peer = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            return CertificateTrust.Matches(peer, CertificateTrust.Fingerprint(peer));
        }
        catch { return false; }
    }

    public static async Task AuthenticateTeacherAsync(Stream stream, string secret, CancellationToken ct)
    {
        if (!EnrollmentManager.IsStrongPairingSecret(secret)) throw new AuthenticationException("Explicit device pairing is required.");
        var hello = await Frame.ReadAsync(stream, ct, 16 * 1024);
        if (hello?.Type != MsgType.AuthHello) throw new AuthenticationException("No authentication challenge.");
        var challenge = Frame.ReadJson<AuthHello>(hello.Value.Payload);
        ValidateNonce(challenge.NonceS);
        string nonce = Security.NewNonce();
        byte[] key = Security.DeriveKey(secret);
        await Frame.WriteJsonAsync(stream, MsgType.AuthRequest,
            new AuthRequest(nonce, Security.Proof(key, "teacher", challenge.NonceS, nonce)), ct);
        var result = await Frame.ReadAsync(stream, ct, 16 * 1024);
        if (result?.Type != MsgType.AuthResult) throw new AuthenticationException("No authentication result.");
        var proof = Frame.ReadJson<AuthResult>(result.Value.Payload);
        if (!proof.Ok || !Security.ProofsEqual(proof.ProofS, Security.Proof(key, "agent", challenge.NonceS, nonce)))
            throw new AuthenticationException("Device pairing verification failed.");
    }

    public static async Task AuthenticateAgentAsync(Stream stream, string secret, CancellationToken ct)
    {
        if (!EnrollmentManager.IsStrongPairingSecret(secret)) throw new AuthenticationException("Explicit device pairing is required.");
        string nonce = Security.NewNonce();
        await Frame.WriteJsonAsync(stream, MsgType.AuthHello, new AuthHello(nonce), ct);
        var request = await Frame.ReadAsync(stream, ct, 16 * 1024);
        if (request?.Type != MsgType.AuthRequest) throw new AuthenticationException("No teacher pairing proof.");
        var proof = Frame.ReadJson<AuthRequest>(request.Value.Payload);
        ValidateNonce(proof.NonceT);
        byte[] key = Security.DeriveKey(secret);
        if (!Security.ProofsEqual(proof.ProofT, Security.Proof(key, "teacher", nonce, proof.NonceT)))
            throw new AuthenticationException("Teacher pairing verification failed.");
        await Frame.WriteJsonAsync(stream, MsgType.AuthResult,
            new AuthResult(true, Security.Proof(key, "agent", nonce, proof.NonceT)), ct);
    }

    private static bool Validate(X509Certificate? certificate, string pin)
    {
        if (certificate is null) return false;
        try
        {
            using var peer = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            return CertificateTrust.Matches(peer, pin) && CertificateTrust.SupportsMutualTls(peer);
        }
        catch { return false; }
    }

    private static void RequireIdentity(X509Certificate2 identity)
    {
        if (!identity.HasPrivateKey || !CertificateTrust.Matches(identity, CertificateTrust.Fingerprint(identity)) ||
            !CertificateTrust.SupportsMutualTls(identity)) throw new AuthenticationException("A valid managed endpoint identity is required.");
    }

    private static void ValidateNonce(string nonce)
    {
        try { if (nonce.Length != 24 || Convert.FromBase64String(nonce).Length != 16) throw new FormatException(); }
        catch (Exception ex) when (ex is FormatException or NullReferenceException)
        { throw new AuthenticationException("Invalid authentication nonce."); }
    }
}
