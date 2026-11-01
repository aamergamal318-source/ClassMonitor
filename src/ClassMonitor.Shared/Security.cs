using System.Security.Cryptography;
using System.Text;

namespace ClassMonitor.Shared;

/// <summary>
/// Shared-secret authentication helpers. The teacher and agent prove to each other that they
/// know the same class password, using HMAC-SHA256 over exchanged nonces — the password itself
/// never crosses the wire. Runs on top of a TLS channel that provides the encryption.
/// </summary>
public static class Security
{
    /// <summary>Derive a 256-bit key from the class password.</summary>
    public static byte[] DeriveKey(string password)
    {
        // Fixed salt is acceptable here: this authenticates a shared LAN secret, it is not
        // password-at-rest storage. The nonces make each handshake unique.
        byte[] salt = Encoding.UTF8.GetBytes("ClassMonitor/v1/auth");
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password ?? ""), salt, 100_000, HashAlgorithmName.SHA256, 32);
    }

    public static string NewNonce()
    {
        Span<byte> b = stackalloc byte[16];
        RandomNumberGenerator.Fill(b);
        return Convert.ToBase64String(b);
    }

    /// <summary>HMAC-SHA256 of <paramref name="label"/>+nonces, as Base64. Order of nonces matters.</summary>
    public static string Proof(byte[] key, string label, string nonceA, string nonceB)
    {
        using var h = new HMACSHA256(key);
        byte[] mac = h.ComputeHash(Encoding.UTF8.GetBytes($"{label}|{nonceA}|{nonceB}"));
        return Convert.ToBase64String(mac);
    }

    /// <summary>Constant-time comparison of two Base64 proofs.</summary>
    public static bool ProofsEqual(string a, string b)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromBase64String(a ?? ""), Convert.FromBase64String(b ?? ""));
        }
        catch { return false; }
    }
}
