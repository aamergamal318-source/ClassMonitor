using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ClassMonitor.Shared.Management;

/// <summary>
/// Removes website and internet blocks that outlived their session after a crash, kill or restart.
/// Only ClassMonitor's own named rules and marked hosts blocks are touched. Requires administrator rights.
/// </summary>
public static class RestrictionCleanup
{
    public static readonly string[] FirewallRuleNames = { "ClassMonitor-Session-TCP", "ClassMonitor-Session-UDP" };
    private static readonly Regex Marker = new(@"^# ClassMonitor:[a-f0-9]{32}:(Begin|End)$", RegexOptions.CultureInvariant);

    /// <summary>Drops every marked block. Anything malformed is kept, because it may not be ClassMonitor's.</summary>
    public static string[] WithoutOwnedHostsBlocks(IReadOnlyList<string> lines)
    {
        var result = new List<string>();
        var pending = new List<string>();
        string? open = null;
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (open is null)
            {
                if (Marker.IsMatch(trimmed) && trimmed.EndsWith(":Begin", StringComparison.Ordinal)) { open = trimmed; pending.Clear(); pending.Add(line); }
                else result.Add(line);
                continue;
            }
            pending.Add(line);
            if (trimmed == open[..^"Begin".Length] + "End") open = null;
        }
        if (open is not null) result.AddRange(pending);
        return result.ToArray();
    }

    public static bool HostsHasOwnedBlocks(IReadOnlyList<string> lines) => WithoutOwnedHostsBlocks(lines).Length != lines.Count;

    public static void Sweep()
    {
        foreach (string rule in FirewallRuleNames)
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "advfirewall", "firewall", "delete", "rule", "name=" + rule }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("Cannot start Windows Firewall management.");
            process.StandardOutput.ReadToEndAsync(); process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000)) process.Kill();
        }
        string hosts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
        if (!File.Exists(hosts) || (File.GetAttributes(hosts) & FileAttributes.ReparsePoint) != 0) return;
        string[] lines = File.ReadAllLines(hosts);
        if (HostsHasOwnedBlocks(lines)) File.WriteAllLines(hosts, WithoutOwnedHostsBlocks(lines));
    }
}
