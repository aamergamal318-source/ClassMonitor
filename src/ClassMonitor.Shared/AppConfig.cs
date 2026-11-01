using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using ClassMonitor.Shared.Settings;
using ClassMonitor.Shared.Sessions;

namespace ClassMonitor.Shared;

/// <summary>
/// Simple JSON settings shared in shape by both apps. The agent config lives in ProgramData
/// (machine-wide, so a service and an interactive agent see the same file); the teacher config
/// lives in the user's AppData. If the file is missing it is created with defaults.
/// </summary>
public sealed class AppConfig
{
    /// <summary>Class password — the shared secret both ends must know to connect.</summary>
    public string Password { get; set; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>TCP port the agent listens on / the teacher connects to.</summary>
    public int Port { get; set; } = Net.AgentTcpPort;

    /// <summary>Whether the agent shows its tray icon (true) or runs hidden (false).</summary>
    public bool AgentVisible { get; set; } = true;

    /// <summary>Teacher-side hosts that should be connected manually even when UDP discovery is unavailable.</summary>
    public List<string> ManualHosts { get; set; } = new();
    public SchoolSettings Settings { get; set; } = new();
    public List<PairedDevice> PairedDevices { get; set; } = new();
    public int SchemaVersion { get; set; } = 2;
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static string AgentConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                     "ClassMonitor", "agent.json");

    public static string TeacherConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "ClassMonitor", "teacher.json");

    /// <summary>Load config from <paramref name="path"/>, creating it with defaults if absent.</summary>
    public static AppConfig Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                var result = JsonSerializer.Deserialize<AppConfig>(json)
                    ?? throw new InvalidDataException("Configuration is empty.");
                using var document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty(nameof(Settings), out _))
                    result.Settings.Network.AgentPort = result.Port;
                result.Validate();
                return result;
            }
        }
        catch (JsonException ex) { throw new InvalidDataException("Configuration is invalid. The original file was preserved; restore its .previous backup or repair it.", ex); }

        var cfg = new AppConfig();
        try { cfg.Save(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* No trust is granted from unsaved defaults. */ }
        return cfg;
    }

    public void Save(string path)
    {
        Validate();
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, Opts));
            if (File.Exists(path)) File.Copy(path, path + ".previous", true);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Validate()
    {
        if (Port is < 1024 or > 65535) throw new InvalidDataException("Agent port must be between 1024 and 65535.");
        if (Settings is null || ManualHosts is null || PairedDevices is null) throw new InvalidDataException("Configuration collections cannot be null.");
        Settings.EnsureValid();
        if (PairedDevices.Count > 500 || ManualHosts.Count > 500) throw new InvalidDataException("Too many saved devices.");
        var savedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var peer in PairedDevices)
        {
            if (peer is null || string.IsNullOrWhiteSpace(peer.Host) || peer.Port is < 1024 or > 65535 || !savedHosts.Add(peer.Host))
                throw new InvalidDataException("Invalid paired device address.");
            ClassMonitor.Shared.Management.EnrollmentManager.ValidateEnrollment(peer.Enrollment);
            using var certificate = ClassMonitor.Shared.Management.CertificateTrust.ReadPublicCertificate(peer.PublicCertificateBase64);
            if (!ClassMonitor.Shared.Management.CertificateTrust.Matches(certificate, peer.Enrollment.DeviceCertificateSha256))
                throw new InvalidDataException("Paired device certificate does not match its enrollment.");
        }
    }
}
