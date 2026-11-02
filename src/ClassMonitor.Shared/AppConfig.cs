using System.Text.Json;
using System.Text.Json.Serialization;
using ClassMonitor.Shared.Settings;
using ClassMonitor.Shared.Sessions;

namespace ClassMonitor.Shared;

/// <summary>
/// Simple JSON settings shared in shape by both apps. Both live in the user's profile: they hold
/// preferences and per-user paths, never trust. If the file is missing it is created with defaults.
/// </summary>
public sealed class AppConfig
{
    /// <summary>Legacy field kept so old files still load. It no longer authenticates anything and is never generated.</summary>
    public string Password { get; set; } = "";

    /// <summary>TCP port the agent listens on / the teacher connects to.</summary>
    public int Port { get => Settings.Network.AgentPort; set => Settings.Network.AgentPort = value; }

    /// <summary>Legacy field kept so old files still load. The agent's tray icon is always visible.</summary>
    public bool AgentVisible { get; set; } = true;

    /// <summary>Teacher-side hosts that should be connected manually even when UDP discovery is unavailable.</summary>
    public List<string> ManualHosts { get; set; } = new();
    public SchoolSettings Settings { get; set; } = new();
    public List<PairedDevice> PairedDevices { get; set; } = new();
    public int SchemaVersion { get; set; } = 2;
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static string AgentConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
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
        catch (Exception ex) when (ex is JsonException or NullReferenceException)
        { throw new InvalidDataException("Configuration is invalid. The original file was preserved; restore its .previous backup or repair it.", ex); }

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
        if (Settings is null || ManualHosts is null || PairedDevices is null) throw new InvalidDataException("Configuration collections cannot be null.");
        if (Port is < 1024 or > 65535) throw new InvalidDataException("Agent port must be between 1024 and 65535.");
        Settings.EnsureValid();
        if (PairedDevices.Count > 500 || ManualHosts.Count > 500) throw new InvalidDataException("Too many saved devices.");
        // One computer can hold several enrolled accounts, so a record is identified by its device identity.
        var savedDevices = new HashSet<string>(StringComparer.Ordinal);
        foreach (var peer in PairedDevices)
        {
            if (peer is null || string.IsNullOrWhiteSpace(peer.Host) || peer.Port is < 1024 or > 65535)
                throw new InvalidDataException("Invalid paired device address.");
            ClassMonitor.Shared.Management.EnrollmentManager.ValidateEnrollmentStructure(peer.Enrollment);
            if (!savedDevices.Add(peer.Enrollment.DeviceId)) throw new InvalidDataException("A device is saved twice.");
            string fingerprint = ClassMonitor.Shared.Management.CertificateTrust.FingerprintOfPublic(peer.PublicCertificateBase64);
            if (!string.Equals(fingerprint, peer.Enrollment.DeviceCertificateSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Paired device certificate does not match its enrollment.");
        }
    }
}
