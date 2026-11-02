using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using ClassMonitor.Shared.Management;

namespace ClassMonitor.Shared.Sessions;

public sealed record PairingPassword(string SaltBase64, int Iterations, string VerifierBase64);
public sealed record PairHello(string NonceT);
public sealed record PairChallenge(string SaltBase64, int Iterations, string NonceA);
public sealed record PairProof(string Proof);
public sealed record PairAccepted(string Proof, DeviceEnrollmentRequest Device, int AgentPort);
public sealed record PairResult(bool Ok, string Message);

/// <summary>
/// Adds a computer with the password chosen when its student agent was installed. The password never crosses
/// the network and only a slow salted hash is stored. Each side proves knowledge of that hash over both TLS
/// certificate fingerprints, so a machine in the middle presenting its own certificate fails. Someone who
/// impersonates a computer can capture a proof to guess passwords offline, hence the slow hash, the minimum length
/// and the agent's lockout.
/// </summary>
public static class PasswordPairing
{
    public const int Port = 5902, MinimumLength = 8, MaximumLength = 256;
    private const int DefaultIterations = 600_000, MaximumIterations = 5_000_000;
    private const int MaximumFrameBytes = 128 * 1024;

    public static void RequireAcceptable(string? password)
    {
        if (password is null || password.Length < MinimumLength || password.Length > MaximumLength)
            throw new InvalidDataException($"The password must have at least {MinimumLength} characters.");
    }

    public static PairingPassword Create(string password)
    {
        RequireAcceptable(password);
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        return new PairingPassword(Convert.ToBase64String(salt), DefaultIterations,
            Convert.ToBase64String(Derive(password, salt, DefaultIterations)));
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    public static string Proof(byte[] verifier, string role, string nonceT, string nonceA, string agentPin, string teacherPin)
    {
        byte[] transcript = Encoding.UTF8.GetBytes(string.Join('|', "ClassMonitor pairing v1", role, nonceT, nonceA,
            agentPin.ToUpperInvariant(), teacherPin.ToUpperInvariant()));
        return Convert.ToBase64String(HMACSHA256.HashData(verifier, transcript));
    }

    private static bool Same(string? a, string b) =>
        a is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static async Task<T> ReadAsync<T>(Stream stream, MsgType type, CancellationToken ct)
    {
        var message = await Frame.ReadAsync(stream, ct, MaximumFrameBytes) ?? throw new AuthenticationException("The other computer closed the connection.");
        if (message.Type != type) throw new InvalidDataException("Unexpected pairing message.");
        return Frame.ReadJson<T>(message.Payload);
    }

    /// <summary>Agent side. Returns the validated enrollment to store, or throws when the password proof is wrong.</summary>
    public static async Task<ManagedEnrollment> AgentAsync(Stream stream, PairingPassword password, DeviceEnrollmentRequest device,
        string teacherPin, int agentPort, CancellationToken ct)
    {
        byte[] verifier = Convert.FromBase64String(password.VerifierBase64);
        var hello = await ReadAsync<PairHello>(stream, MsgType.PairHello, ct);
        string nonceA = Security.NewNonce();
        await Frame.WriteJsonAsync(stream, MsgType.PairChallenge, new PairChallenge(password.SaltBase64, password.Iterations, nonceA), ct);
        var proof = await ReadAsync<PairProof>(stream, MsgType.PairProof, ct);
        if (!Same(proof.Proof, Proof(verifier, "teacher", hello.NonceT, nonceA, device.CertificateSha256, teacherPin)))
        {
            await Frame.WriteJsonAsync(stream, MsgType.PairResult, new PairResult(false, "wrong-password"), ct);
            throw new AuthenticationException("Wrong pairing password.");
        }
        await Frame.WriteJsonAsync(stream, MsgType.PairAccepted, new PairAccepted(
            Proof(verifier, "agent", hello.NonceT, nonceA, device.CertificateSha256, teacherPin), device, agentPort), ct);
        var document = await ReadAsync<ManagedEnrollment>(stream, MsgType.PairEnrollment, ct);
        if (!string.Equals(document.DeviceFingerprint, device.CertificateSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(document.TeacherFingerprint, teacherPin, StringComparison.OrdinalIgnoreCase))
            throw new AuthenticationException("The enrollment does not belong to this connection.");
        var enrollment = EnrollmentManager.ValidatePackage(document.Package, document.DeviceFingerprint, document.TeacherFingerprint);
        // A password adds a classroom teacher only. Power and program launch still need an administrator's import.
        if (enrollment.Teacher.Role != TeacherRole.Teacher)
        {
            await Frame.WriteJsonAsync(stream, MsgType.PairResult, new PairResult(false, "role-refused"), ct);
            throw new UnauthorizedAccessException("A password cannot grant the school administrator role.");
        }
        return document;
    }

    /// <summary>Tells the teacher the enrollment was stored, so its record is only saved once the computer has it too.</summary>
    public static Task ConfirmAsync(Stream stream, CancellationToken ct) =>
        Frame.WriteJsonAsync(stream, MsgType.PairResult, new PairResult(true, "paired"), ct);

    public static Task RefuseAsync(Stream stream, string reason, CancellationToken ct) =>
        Frame.WriteJsonAsync(stream, MsgType.PairResult, new PairResult(false, reason), ct);

    /// <summary>Teacher side. <paramref name="create"/> signs the enrollment only after the computer has proved it knows the password.</summary>
    public static async Task<T> TeacherAsync<T>(Stream stream, string password, string teacherPin, string agentPin,
        Func<DeviceEnrollmentRequest, int, (ManagedEnrollment Document, T Result)> create, CancellationToken ct)
    {
        RequireAcceptable(password);
        string nonceT = Security.NewNonce();
        await Frame.WriteJsonAsync(stream, MsgType.PairHello, new PairHello(nonceT), ct);
        var first = await Frame.ReadAsync(stream, ct, MaximumFrameBytes) ?? throw new AuthenticationException("The computer closed the connection.");
        if (first.Type == MsgType.PairResult) throw new PairingRefusedException(Frame.ReadJson<PairResult>(first.Payload).Message);
        if (first.Type != MsgType.PairChallenge) throw new InvalidDataException("Unexpected pairing message.");
        var challenge = Frame.ReadJson<PairChallenge>(first.Payload);
        // The other computer is not trusted yet, so it must not be able to choose an expensive calculation here.
        if (challenge.Iterations is < 100_000 or > MaximumIterations) throw new InvalidDataException("Invalid pairing challenge.");
        byte[] verifier = Derive(password, Convert.FromBase64String(challenge.SaltBase64), challenge.Iterations);
        await Frame.WriteJsonAsync(stream, MsgType.PairProof, new PairProof(Proof(verifier, "teacher", nonceT, challenge.NonceA, agentPin, teacherPin)), ct);
        var answer = await Frame.ReadAsync(stream, ct, MaximumFrameBytes) ?? throw new AuthenticationException("The computer closed the connection.");
        if (answer.Type == MsgType.PairResult) throw new PairingRefusedException(Frame.ReadJson<PairResult>(answer.Payload).Message);
        if (answer.Type != MsgType.PairAccepted) throw new InvalidDataException("Unexpected pairing message.");
        var accepted = Frame.ReadJson<PairAccepted>(answer.Payload);
        if (!Same(accepted.Proof, Proof(verifier, "agent", nonceT, challenge.NonceA, agentPin, teacherPin))
            || !string.Equals(accepted.Device.CertificateSha256, agentPin, StringComparison.OrdinalIgnoreCase))
            throw new AuthenticationException("The computer did not prove it knows this password.");
        var (document, result) = create(accepted.Device, accepted.AgentPort);
        await Frame.WriteJsonAsync(stream, MsgType.PairEnrollment, document, ct);
        var done = await ReadAsync<PairResult>(stream, MsgType.PairResult, ct);
        if (!done.Ok) throw new PairingRefusedException(done.Message);
        return result;
    }
}

public sealed class PairingRefusedException(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}
