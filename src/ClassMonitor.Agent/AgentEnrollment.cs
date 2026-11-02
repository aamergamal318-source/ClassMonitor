using System.Text.Json;
using ClassMonitor.Shared.Management;

namespace ClassMonitor.Agent;

/// <summary>The student side of explicit enrollment: export a public request. An administrator imports the signed answer with a trusted copy of setup.</summary>
internal static class AgentEnrollment
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static DeviceEnrollmentRequest CreateRequest()
    {
        using var identity = WindowsIdentityStore.GetOrCreate("Agent");
        return EnrollmentManager.CreateRequest(identity, Environment.MachineName + " / " + Environment.UserName);
    }

    public static string DevicePin()
    {
        using var identity = WindowsIdentityStore.GetOrCreate("Agent");
        return CertificateTrust.Fingerprint(identity);
    }

    /// <summary>The request holds a public certificate and fingerprint only, never a private key.</summary>
    public static void WriteRequest(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(CreateRequest(), Indented));

    public static string Group(string pin) =>
        string.Join(' ', Enumerable.Range(0, pin.Length / 4).Select(i => pin.Substring(i * 4, 4)));
}
