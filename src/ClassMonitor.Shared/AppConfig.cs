using System.Text.Json;

namespace ClassMonitor.Shared;

/// <summary>
/// Simple JSON settings shared in shape by both apps. The agent config lives in ProgramData
/// (machine-wide, so a service and an interactive agent see the same file); the teacher config
/// lives in the user's AppData. If the file is missing it is created with defaults.
/// </summary>
public sealed class AppConfig
{
    /// <summary>Class password — the shared secret both ends must know to connect.</summary>
    public string Password { get; set; } = "classmonitor";

    /// <summary>TCP port the agent listens on / the teacher connects to.</summary>
    public int Port { get; set; } = Net.AgentTcpPort;

    /// <summary>Whether the agent shows its tray icon (true) or runs hidden (false).</summary>
    public bool AgentVisible { get; set; } = true;

    /// <summary>Teacher-side hosts that should be connected manually even when UDP discovery is unavailable.</summary>
    public List<string> ManualHosts { get; set; } = new();

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
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path)) ?? new AppConfig();
        }
        catch { /* corrupt file -> fall back to defaults */ }

        var cfg = new AppConfig();
        try { cfg.Save(path); } catch { /* read-only location; ignore */ }
        return cfg;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Opts));
    }
}
