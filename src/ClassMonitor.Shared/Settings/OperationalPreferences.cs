using System.Diagnostics;

namespace ClassMonitor.Shared.Settings;

/// <summary>Latest observed frame size informs a per-device profile; a sender still enforces the actual byte budget.</summary>
public sealed class AdaptiveStreamProfile
{
    private double _frameBytes = 48 * 1024;
    public void ObserveFrame(int bytes)
    {
        if (bytes <= 0 || bytes > Net.MaxMessageBytes) throw new ArgumentOutOfRangeException(nameof(bytes));
        _frameBytes = 0.75 * _frameBytes + 0.25 * bytes;
    }

    public ScreenRequest CreateRequest(DisplaySettings settings, int activeDevices = 1, int monitorIndex = -1)
    {
        new SchoolSettings { Display = settings }.EnsureValid();
        if (activeDevices < 1 || activeDevices > 500) throw new ArgumentOutOfRangeException(nameof(activeDevices));
        double budgetBytes = settings.BandwidthMbps * 1_000_000 / 8 / activeDevices;
        double allowedFps = Math.Min(settings.FramesPerSecond, budgetBytes / Math.Max(1, _frameBytes));
        int width = settings.MaxFrameWidth;
        int quality = settings.Quality;
        if (settings.AdaptiveQuality && allowedFps < settings.FramesPerSecond)
        {
            double ratio = Math.Clamp(allowedFps / settings.FramesPerSecond, 0.25, 1);
            width = Math.Max(320, (int)(width * Math.Sqrt(ratio)));
            quality = Math.Max(10, (int)(quality * Math.Sqrt(ratio)));
        }
        int interval = Math.Clamp((int)Math.Ceiling(1000 / Math.Max(0.2, allowedFps)), 100, 5000);
        return new ScreenRequest(width, quality, interval, monitorIndex);
    }
}

/// <summary>Enforces a real serialized byte budget across sends. Cancellation never increases the allowed send rate.</summary>
public sealed class BandwidthLimiter
{
    private readonly double _bytesPerSecond;
    private readonly object _sync = new();
    private double _reservedUntil;
    public BandwidthLimiter(double megabitsPerSecond)
    {
        if (!double.IsFinite(megabitsPerSecond) || megabitsPerSecond < 0.25 || megabitsPerSecond > 100)
            throw new ArgumentOutOfRangeException(nameof(megabitsPerSecond));
        _bytesPerSecond = megabitsPerSecond * 1_000_000 / 8;
    }
    public Task WaitAsync(int bytes, CancellationToken cancellationToken = default)
    {
        if (bytes < 0 || bytes > Net.MaxMessageBytes) throw new ArgumentOutOfRangeException(nameof(bytes));
        cancellationToken.ThrowIfCancellationRequested();
        double seconds;
        lock (_sync)
        {
            double now = (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
            _reservedUntil = Math.Max(now, _reservedUntil) + bytes / _bytesPerSecond;
            seconds = Math.Max(0, _reservedUntil - now);
        }
        return seconds == 0 ? Task.CompletedTask : Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
    }
}

public static class ApprovedFilePaths
{
    public static string ResolveFileName(string approvedDirectory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName) || fileName is "." or ".."
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || fileName.TrimEnd(' ', '.') != fileName)
            throw new InvalidDataException("A file name without directory components is required.");
        return ResolveRelative(approvedDirectory, fileName);
    }

    public static string ResolveRelative(string approvedDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(approvedDirectory) || !Path.IsPathFullyQualified(approvedDirectory))
            throw new InvalidDataException("An absolute approved directory is required.");
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException("An approved relative lesson path is required.");
        string[] segments = relativePath.Replace('\\', '/').Split('/');
        if (segments.Any(s => string.IsNullOrWhiteSpace(s) || s is "." or ".." || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || s.TrimEnd(' ', '.') != s))
            throw new InvalidDataException("Invalid lesson path.");
        if (segments.Any(IsReservedName)) throw new InvalidDataException("Reserved Windows device names are not lesson files.");
        string root = Path.GetFullPath(approvedDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string target = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Lesson path is outside the approved directory.");
        for (string? existing = target; !string.IsNullOrEmpty(existing); existing = Path.GetDirectoryName(existing))
        {
            if ((File.Exists(existing) || Directory.Exists(existing)) && (File.GetAttributes(existing) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Lesson paths cannot traverse a symbolic link or junction.");
        }
        return target;
    }

    private static bool IsReservedName(string segment)
    {
        string stem = segment.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                && "123456789¹²³".Contains(stem[3]));
    }

    public static void CheckLength(long bytes, FileSettings settings)
    {
        if (bytes < 0 || bytes > settings.MaximumTransferBytes) throw new InvalidDataException("Lesson file exceeds the configured transfer limit.");
    }
}

public static class RecordingRetention
{
    public const string OwnedPattern = "classmonitor-*";
    private static bool Owned(string path) =>
        (Path.GetExtension(path).Equals(".avi", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase))
        && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
    public static IReadOnlyList<string> FindExpired(string directory, int retentionDays, DateTimeOffset now)
    {
        if (retentionDays < 1 || retentionDays > 365) throw new ArgumentOutOfRangeException(nameof(retentionDays));
        _ = ApprovedFilePaths.ResolveFileName(directory, "classmonitor-retention-check.tmp");
        if (!Directory.Exists(directory)) return Array.Empty<string>();
        return Directory.EnumerateFiles(directory, OwnedPattern, SearchOption.TopDirectoryOnly)
            .Where(path => Owned(path) && File.GetLastWriteTimeUtc(path) < now.UtcDateTime.AddDays(-retentionDays)).ToArray();
    }
    public static int PruneExpired(string directory, int retentionDays, DateTimeOffset now)
    {
        var files = FindExpired(directory, retentionDays, now);
        foreach (string path in files) File.Delete(path);
        return files.Count;
    }
    public static long OwnedStorageBytes(string directory)
    {
        _ = ApprovedFilePaths.ResolveFileName(directory, "classmonitor-retention-check.tmp");
        return !Directory.Exists(directory) ? 0 : Directory.EnumerateFiles(directory, OwnedPattern, SearchOption.TopDirectoryOnly)
            .Where(Owned).Sum(path => new FileInfo(path).Length);
    }
}

public sealed record DeviceSearchEntry(string DeviceId, string DisplayName, string Classroom, string Status);
public static class DashboardQuery
{
    public static bool Matches(DeviceSearchEntry device, string? text, string presence = "all")
    {
        if (presence != "all" && !string.Equals(presence, device.Status, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(text)) return true;
        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(term => new[] { device.DeviceId, device.DisplayName, device.Classroom, device.Status }
                .Any(value => (value ?? "").Contains(term, StringComparison.OrdinalIgnoreCase)));
    }
}
