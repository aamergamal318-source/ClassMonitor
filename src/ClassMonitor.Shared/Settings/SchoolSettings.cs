using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassMonitor.Shared.Settings;

/// <summary>User preferences only. None of these values grant enrollment or operation permissions.</summary>
public sealed class SchoolSettings
{
    public int SchemaVersion { get; set; } = 1;
    public GeneralSettings General { get; set; } = new();
    public NetworkSettings Network { get; set; } = new();
    public DisplaySettings Display { get; set; } = new();
    public FileSettings Files { get; set; } = new();
    public RecordingSettings Recording { get; set; } = new();
    public LayoutSettings Layout { get; set; } = new();
    public PolicySettings Policies { get; set; } = new();

    public SchoolSettings Clone() => JsonSerializer.Deserialize<SchoolSettings>(JsonSerializer.Serialize(this))!;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        void Range(string name, double value, double minimum, double maximum)
        {
            if (!double.IsFinite(value) || value < minimum || value > maximum)
                errors.Add($"{name}: expected {minimum}–{maximum}.");
        }
        void Choice(string name, string? value, params string[] choices)
        {
            if (value is null || !choices.Contains(value, StringComparer.Ordinal)) errors.Add($"{name}: unsupported value.");
        }
        void DirectoryPath(string name, string? value)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) || value.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                    errors.Add($"{name}: an absolute directory is required.");
                else _ = Path.GetFullPath(value);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            { errors.Add($"{name}: invalid directory."); }
        }
        if (SchemaVersion != 1) errors.Add("SchemaVersion: unsupported settings version.");
        if (General is null) errors.Add("General: required.");
        else
        {
            Choice("General.Language", General.Language, "en", "ar", "he");
            Choice("General.Theme", General.Theme, "system", "light", "dark");
        }
        if (Network is null) errors.Add("Network: required.");
        else
        {
            Range("Network.AgentPort", Network.AgentPort, 1024, 65535);
            Range("Network.DiscoveryPort", Network.DiscoveryPort, 1024, 65535);
            if (Network.AgentPort == Network.DiscoveryPort) errors.Add("Network: discovery and agent ports must differ.");
            Range("Network.ConnectTimeoutSeconds", Network.ConnectTimeoutSeconds, 2, 60);
            Range("Network.HandshakeTimeoutSeconds", Network.HandshakeTimeoutSeconds, 2, 60);
            Range("Network.HealthTimeoutSeconds", Network.HealthTimeoutSeconds, 5, 300);
            if (Network.LocalInterfaceAddress is null || (Network.LocalInterfaceAddress.Length != 0 &&
                (!IPAddress.TryParse(Network.LocalInterfaceAddress, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)))
                errors.Add("Network.LocalInterfaceAddress: an IPv4 address or empty value is required.");
        }
        if (Display is null) errors.Add("Display: required.");
        else
        {
            Range("Display.ThumbnailWidth", Display.ThumbnailWidth, 160, 640);
            Range("Display.ThumbnailHeight", Display.ThumbnailHeight, 120, 480);
            Range("Display.MaxFrameWidth", Display.MaxFrameWidth, 320, 1920);
            Range("Display.Quality", Display.Quality, 10, 90);
            Range("Display.FramesPerSecond", Display.FramesPerSecond, 0.2, 10);
            Range("Display.BandwidthMbps", Display.BandwidthMbps, 0.25, 100);
        }
        if (Files is null) errors.Add("Files: required.");
        else
        {
            DirectoryPath("Files.ReceiveDirectory", Files.ReceiveDirectory);
            DirectoryPath("Files.CollectDirectory", Files.CollectDirectory);
            Range("Files.MaximumTransferMiB", Files.MaximumTransferMiB, 1, 8);
        }
        if (Recording is null) errors.Add("Recording: required.");
        else
        {
            DirectoryPath("Recording.Directory", Recording.Directory);
            Range("Recording.RetentionDays", Recording.RetentionDays, 1, 365);
            Range("Recording.MaximumStorageMiB", Recording.MaximumStorageMiB, 64, 102400);
        }
        if (Layout is null) errors.Add("Layout: required.");
        else
        {
            Range("Layout.WindowWidth", Layout.WindowWidth, 900, 7680);
            Range("Layout.WindowHeight", Layout.WindowHeight, 600, 4320);
            Range("Layout.SidebarWidth", Layout.SidebarWidth, 160, 480);
            Choice("Layout.DeviceSort", Layout.DeviceSort, "name", "presence");
            if (Layout.DeviceOrder is null || Layout.DeviceOrder.Count > 500 || Layout.DeviceOrder.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 256)
                || Layout.DeviceOrder.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Layout.DeviceOrder.Count)
                errors.Add("Layout.DeviceOrder: expected up to 500 distinct nonempty device identifiers.");
        }
        if (Policies is null) errors.Add("Policies: required.");
        else
        {
            if (!Policies.RestoreOnSessionEnd) errors.Add("Policies.RestoreOnSessionEnd: restoration cannot be disabled.");
            Range("Policies.MaximumFocusMinutes", Policies.MaximumFocusMinutes, 1, 240);
        }
        return errors;
    }

    public void EnsureValid()
    {
        var errors = Validate();
        if (errors.Count > 0) throw new SettingsValidationException(errors);
    }
}

public sealed class GeneralSettings
{
    public string Language { get; set; } = "en";
    public string Theme { get; set; } = "system";
    public bool NotificationsEnabled { get; set; } = true;
}

public sealed class NetworkSettings
{
    public int AgentPort { get; set; } = 5900;
    public bool DiscoveryEnabled { get; set; } = true;
    public int DiscoveryPort { get; set; } = 5901;
    public int ConnectTimeoutSeconds { get; set; } = 10;
    public int HandshakeTimeoutSeconds { get; set; } = 10;
    public int HealthTimeoutSeconds { get; set; } = 15;
    public string LocalInterfaceAddress { get; set; } = "";
}

public sealed class DisplaySettings
{
    public int ThumbnailWidth { get; set; } = 280;
    public int ThumbnailHeight { get; set; } = 205;
    public int MaxFrameWidth { get; set; } = 640;
    public int Quality { get; set; } = 50;
    public double FramesPerSecond { get; set; } = 1;
    public double BandwidthMbps { get; set; } = 8;
    public bool AdaptiveQuality { get; set; } = true;
    [JsonIgnore] public int IntervalMs => Math.Clamp((int)Math.Ceiling(1000 / FramesPerSecond), 100, 5000);
}

public sealed class FileSettings
{
    public string ReceiveDirectory { get; set; } = DefaultReceivedDirectory();
    public string CollectDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassMonitor", "Collected");
    public int MaximumTransferMiB { get; set; } = 4;
    [JsonIgnore] public long MaximumTransferBytes => MaximumTransferMiB * 1024L * 1024;
    private static string DefaultReceivedDirectory()
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return string.IsNullOrWhiteSpace(desktop)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassMonitor", "Received")
            : Path.Combine(desktop, "ClassMonitor");
    }
}

public sealed class RecordingSettings
{
    public bool AllowRecording { get; set; }
    public string Directory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassMonitor", "Recordings");
    public int RetentionDays { get; set; } = 7;
    public int MaximumStorageMiB { get; set; } = 2048;
}

public sealed class LayoutSettings
{
    public bool RememberWindowBounds { get; set; } = true;
    public int WindowWidth { get; set; } = 1180;
    public int WindowHeight { get; set; } = 760;
    public int SidebarWidth { get; set; } = 220;
    public string DeviceSort { get; set; } = "name";
    public List<string> DeviceOrder { get; set; } = new();
}

public sealed class PolicySettings
{
    public bool ConfirmBeforeFocus { get; set; } = true;
    public bool RestoreOnSessionEnd { get; set; } = true;
    public int MaximumFocusMinutes { get; set; } = 60;
}

public sealed class SettingsValidationException(IReadOnlyList<string> errors)
    : ArgumentException(string.Join(Environment.NewLine, errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
