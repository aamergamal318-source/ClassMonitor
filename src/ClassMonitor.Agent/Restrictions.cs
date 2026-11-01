using System.Diagnostics;
using ClassMonitor.Shared;

namespace ClassMonitor.Agent;

/// <summary>
/// Enforces teacher restrictions on this machine: kills blocked applications, blocks websites
/// via the hosts file, and blocks all web traffic via a firewall rule. Hosts/firewall changes
/// require the agent to run with administrator rights (the logon task uses highest privileges).
/// Restrictions are cleared when the agent stops, so a machine is never left blocked.
/// </summary>
internal sealed class Restrictions : IDisposable
{
    private readonly AgentUi _ui;
    private readonly System.Threading.Timer _appTimer;
    private volatile string[] _blockedApps = Array.Empty<string>();
    private volatile string[] _allowedApps = Array.Empty<string>();
    private DateTime _lastNotice = DateTime.MinValue;

    public Restrictions(AgentUi ui)
    {
        _ui = ui;
        _appTimer = new System.Threading.Timer(_ => EnforceApps(), null, 1000, 1000);
    }

    public void Apply(RestrictCmd c)
    {
        switch (c.Kind)
        {
            case "apps":
                if (string.Equals(c.Mode, "allowlist", StringComparison.OrdinalIgnoreCase))
                {
                    _allowedApps = c.Enabled ? NormalizeApps(c.Items) : Array.Empty<string>();
                    _blockedApps = Array.Empty<string>();
                }
                else
                {
                    _blockedApps = c.Enabled ? NormalizeApps(c.Items) : Array.Empty<string>();
                    _allowedApps = Array.Empty<string>();
                }
                break;
            case "sites":
                HostsBlocker.Set(c.Enabled ? c.Items : Array.Empty<string>());
                break;
            case "internet":
                FirewallBlocker.SetBlocked(c.Enabled);
                break;
        }
    }

    private static string[] NormalizeApps(string[] items) => items
        .Select(s => s.Trim())
        .Where(s => s.Length > 0)
        .Select(s => s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s[..^4] : s)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private void EnforceApps()
    {
        bool killed = false;
        var blocked = _blockedApps;
        foreach (var name in blocked)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { p.Kill(); killed = true; } catch { }
                finally { try { p.Dispose(); } catch { } }
            }
        }

        var allowed = _allowedApps;
        if (allowed.Length > 0)
        {
            var allowedSet = allowed.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var processName = p.ProcessName;
                    if (IsProtectedProcess(processName)) continue;
                    if (allowedSet.Contains(processName)) continue;
                    p.Kill();
                    killed = true;
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
        }

        if (killed && (DateTime.Now - _lastNotice).TotalSeconds > 8)
        {
            _lastNotice = DateTime.Now;
            _ui.ShowMessage("An application blocked by the teacher was closed.", 4000);
        }
    }

    private static bool IsProtectedProcess(string processName)
    {
        var name = processName.Trim();
        if (string.IsNullOrEmpty(name)) return true;

        return name.Equals("explorer", StringComparison.OrdinalIgnoreCase)
            || name.Equals("System", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Idle", StringComparison.OrdinalIgnoreCase)
            || name.Equals("wininit", StringComparison.OrdinalIgnoreCase)
            || name.Equals("csrss", StringComparison.OrdinalIgnoreCase)
            || name.Equals("lsass", StringComparison.OrdinalIgnoreCase)
            || name.Equals("services", StringComparison.OrdinalIgnoreCase)
            || name.Equals("smss", StringComparison.OrdinalIgnoreCase)
            || name.Equals("dwm", StringComparison.OrdinalIgnoreCase)
            || name.Equals("fontdrvhost", StringComparison.OrdinalIgnoreCase)
            || name.Equals("svchost", StringComparison.OrdinalIgnoreCase)
            || name.Equals("RuntimeBroker", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _appTimer.Dispose();
        _blockedApps = Array.Empty<string>();
        _allowedApps = Array.Empty<string>();
        // Don't strand the machine: lift hosts/firewall restrictions when the agent stops.
        try { HostsBlocker.Set(Array.Empty<string>()); } catch { }
        try { FirewallBlocker.SetBlocked(false); } catch { }
    }
}

/// <summary>Blocks domains by routing them to 0.0.0.0 in the Windows hosts file.</summary>
internal static class HostsBlocker
{
    private const string Begin = "# ClassMonitor-Begin";
    private const string End = "# ClassMonitor-End";

    private static string HostsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    public static void Set(string[] domains)
    {
        try
        {
            var lines = File.Exists(HostsPath) ? File.ReadAllLines(HostsPath).ToList() : new List<string>();
            lines = StripBlock(lines);

            var clean = domains
                .Select(d => d.Trim().ToLowerInvariant()
                    .Replace("https://", "").Replace("http://", "").TrimEnd('/'))
                .Where(d => d.Length > 0)
                .Distinct()
                .ToList();

            if (clean.Count > 0)
            {
                lines.Add(Begin);
                foreach (var d in clean)
                {
                    lines.Add($"0.0.0.0 {d}");
                    if (!d.StartsWith("www.")) lines.Add($"0.0.0.0 www.{d}");
                }
                lines.Add(End);
            }

            File.WriteAllLines(HostsPath, lines);
            FlushDns();
        }
        catch { /* needs admin; ignore if not elevated */ }
    }

    private static List<string> StripBlock(List<string> lines)
    {
        var result = new List<string>(lines.Count);
        bool inBlock = false;
        foreach (var line in lines)
        {
            if (line.Trim() == Begin) { inBlock = true; continue; }
            if (line.Trim() == End) { inBlock = false; continue; }
            if (!inBlock) result.Add(line);
        }
        return result;
    }

    private static void FlushDns()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ipconfig", "/flushdns")
            { CreateNoWindow = true, UseShellExecute = false });
            p?.WaitForExit(3000);
        }
        catch { }
    }
}

/// <summary>Blocks all outbound web traffic (TCP 80/443) with a Windows Firewall rule.</summary>
internal static class FirewallBlocker
{
    private const string Rule = "ClassMonitor-BlockWeb";

    public static void SetBlocked(bool on)
    {
        // Remove any existing rule first (idempotent), then add if blocking.
        Run($"advfirewall firewall delete rule name=\"{Rule}\"");
        if (on)
            Run($"advfirewall firewall add rule name=\"{Rule}\" dir=out action=block protocol=TCP remoteport=80,443");
    }

    private static void Run(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("netsh", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            p.WaitForExit(4000);
        }
        catch { /* needs admin; ignore if not elevated */ }
    }
}
