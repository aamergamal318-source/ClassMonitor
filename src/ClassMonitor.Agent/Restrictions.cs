using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using ClassMonitor.Shared;
using ClassMonitor.Shared.Management;

namespace ClassMonitor.Agent;

internal sealed record InteractiveProcess(int Id, string Name, int SessionId, long StartedUtcTicks, bool HasVisibleWindow);
internal interface IRestrictionSystem
{
    IReadOnlyList<InteractiveProcess> Processes();
    bool CloseProcess(InteractiveProcess process);
    void SetSites(string owner, string[] domains);
    void SetWebBlocked(string owner, bool blocked);
    void ClearOwned(string owner);
}

/// <summary>A bounded session policy owns its own changes; it never clears another session's hosts/firewall entries.</summary>
internal sealed class Restrictions : ISessionRestrictions
{
    private readonly IAgentInteraction _ui;
    private readonly IRestrictionSystem _system;
    private readonly System.Threading.Timer? _timer;
    private readonly object _gate = new();
    private readonly string _owner = Guid.NewGuid().ToString("N");
    private string[] _blocked = [], _allowed = [];
    private DateTime _lastNotice = DateTime.MinValue;
    private bool _disposed;

    public Restrictions(IAgentInteraction ui, IRestrictionSystem? system = null, bool startTimer = true)
    {
        _ui = ui; _system = system ?? new WindowsRestrictionSystem();
        if (startTimer) _timer = new System.Threading.Timer(_ => EnforceNow(), null, 1000, 1000);
    }
    public void Apply(RestrictCmd command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            switch (command.Kind)
            {
                case "apps":
                    if (command.Mode is not (null or "" or "blocklist" or "allowlist")) throw new InvalidDataException("Unsupported application policy mode.");
                    var names = command.Enabled ? NormalizeApps(command.Items) : [];
                    _allowed = command.Mode == "allowlist" ? names : [];
                    _blocked = command.Mode == "allowlist" ? [] : names;
                    break;
                case "sites":
                    if (command.Mode is not (null or "" or "blocklist")) throw new NotSupportedException("Domain allow-list enforcement is not supported by the hosts backend.");
                    _system.SetSites(_owner, command.Enabled ? NormalizeDomains(command.Items) : []);
                    break;
                case "internet": _system.SetWebBlocked(_owner, command.Enabled); break;
                default: throw new InvalidDataException("Unsupported focus policy.");
            }
        }
    }
    public void Clear()
    {
        lock (_gate)
        {
            _blocked = []; _allowed = [];
            _system.ClearOwned(_owner);
        }
    }
    public void EnforceNow()
    {
        lock (_gate)
        {
            if (_disposed || (_blocked.Length == 0 && _allowed.Length == 0)) return;
            bool closed = false;
            foreach (var process in _system.Processes())
            {
                if (!process.HasVisibleWindow || process.SessionId != Process.GetCurrentProcess().SessionId || IsProtectedProcess(process.Name)) continue;
                if (_blocked.Contains(process.Name, StringComparer.OrdinalIgnoreCase) ||
                    (_allowed.Length > 0 && !_allowed.Contains(process.Name, StringComparer.OrdinalIgnoreCase)))
                    closed |= _system.CloseProcess(process);
            }
            if (closed && DateTime.UtcNow - _lastNotice > TimeSpan.FromSeconds(8))
            {
                _lastNotice = DateTime.UtcNow;
                _ui.ShowMessage("A classroom focus policy closed an unrelated application. Restrictions end with the lesson.", 4000);
            }
        }
    }
    public static string[] NormalizeApps(string[] names)
    {
        if (names is null || names.Length > 100) throw new InvalidDataException("Application list is too large.");
        var result = names.Select(name => (name ?? "").Trim()).Select(name => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name).ToArray();
        if (result.Any(name => name.Length is < 1 or > 80 || !Regex.IsMatch(name, "^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant) || name is "." or ".."))
            throw new InvalidDataException("Only process names, not executable paths or arguments, are permitted.");
        if (result.Any(IsProtectedProcess)) throw new InvalidDataException("Essential Windows and ClassMonitor processes cannot be restricted.");
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public static string[] NormalizeDomains(string[] domains)
    {
        if (domains is null || domains.Length > 100) throw new InvalidDataException("Domain list is too large.");
        var result = domains.Select(domain => (domain ?? "").Trim().ToLowerInvariant()).ToArray();
        if (result.Any(domain => domain.Length is < 1 or > 253 || domain.EndsWith('.') || domain == "localhost" ||
            Uri.CheckHostName(domain) != UriHostNameType.Dns || domain.Any(char.IsWhiteSpace) || domain.Contains('_')))
            throw new InvalidDataException("Website policies require DNS host names without URLs, paths, ports or wildcards.");
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public static bool IsProtectedProcess(string name) => string.IsNullOrWhiteSpace(name) || name.StartsWith("ClassMonitor", StringComparison.OrdinalIgnoreCase) ||
        new[] { "explorer", "System", "Idle", "wininit", "csrss", "lsass", "services", "smss", "dwm", "fontdrvhost", "svchost", "RuntimeBroker", "taskmgr", "consent", "StartMenuExperienceHost", "ShellExperienceHost", "SearchHost", "SecurityHealthSystray" }.Contains(name, StringComparer.OrdinalIgnoreCase);
    public void Dispose()
    {
        _timer?.Dispose();
        lock (_gate) { if (_disposed) return; Clear(); _disposed = true; }
    }
}

internal sealed class WindowsRestrictionSystem : IRestrictionSystem
{
    private readonly HashSet<string> _siteOwners = [];
    private readonly Dictionary<string, HashSet<string>> _rules = [];
    private readonly object _gate = new();
    public IReadOnlyList<InteractiveProcess> Processes()
    {
        var found = new List<InteractiveProcess>();
        int session = Process.GetCurrentProcess().SessionId;
        foreach (var process in Process.GetProcesses())
            using (process)
                try
                {
                    if (process.SessionId == session && process.MainWindowHandle != IntPtr.Zero)
                        found.Add(new InteractiveProcess(process.Id, process.ProcessName, session, process.StartTime.ToUniversalTime().Ticks, true));
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return found;
    }
    public bool CloseProcess(InteractiveProcess expected)
    {
        try
        {
            using var process = Process.GetProcessById(expected.Id);
            if (process.Id == Environment.ProcessId || process.SessionId != Process.GetCurrentProcess().SessionId || process.MainWindowHandle == IntPtr.Zero ||
                process.StartTime.ToUniversalTime().Ticks != expected.StartedUtcTicks || !process.ProcessName.Equals(expected.Name, StringComparison.OrdinalIgnoreCase) || Restrictions.IsProtectedProcess(process.ProcessName)) return false;
            process.Kill(); return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
    public void SetSites(string owner, string[] domains)
    {
        lock (_gate)
        {
            if (domains.Length == 0 && !_siteOwners.Contains(owner)) return;
            HostsBlocker.Set(owner, domains);
            if (domains.Length == 0) _siteOwners.Remove(owner); else _siteOwners.Add(owner);
        }
    }
    public void SetWebBlocked(string owner, bool blocked)
    {
        lock (_gate)
        {
            if (!blocked) { ClearWeb(owner); return; }
            if (_rules.ContainsKey(owner)) return;
            var owned = new HashSet<string>(); _rules.Add(owner, owned);
            try
            {
                foreach (string protocol in new[] { "TCP", "UDP" })
                {
                    // A fixed name lets the next start remove a rule left behind by a crash or restart.
                    string name = "ClassMonitor-Session-" + protocol;
                    RunNetsh("advfirewall", "firewall", "add", "rule", "name=" + name, "dir=out", "action=block", "protocol=" + protocol, "remoteport=" + (protocol == "TCP" ? "80,443" : "443"));
                    owned.Add(name);
                }
            }
            catch { ClearWeb(owner); throw; }
        }
    }
    public void ClearOwned(string owner)
    {
        lock (_gate)
        {
            var errors = new List<Exception>();
            if (_siteOwners.Contains(owner)) try { SetSites(owner, []); } catch (Exception error) { errors.Add(error); }
            try { ClearWeb(owner); } catch (Exception error) { errors.Add(error); }
            if (errors.Count > 0) throw new AggregateException("Some owned classroom restrictions could not be restored.", errors);
        }
    }
    private void ClearWeb(string owner)
    {
        if (!_rules.TryGetValue(owner, out var owned)) return;
        foreach (string name in owned.ToArray())
        {
            RunNetsh("advfirewall", "firewall", "delete", "rule", "name=" + name);
            owned.Remove(name);
        }
        _rules.Remove(owner);
    }
    private static void RunNetsh(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start Windows Firewall management.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(5000)) { process.Kill(); throw new IOException("Windows Firewall operation timed out."); }
        Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new IOException("Windows Firewall could not apply or restore the session rule. Administrator permission is required.");
    }
}

internal static class HostsBlocker
{
    private static readonly object Gate = new();
    public static string[] UpdateOwnedLines(IEnumerable<string> original, string owner, IReadOnlyList<string> domains)
    {
        if (!Regex.IsMatch(owner, "^[a-f0-9]{32}$", RegexOptions.CultureInvariant)) throw new InvalidDataException("Invalid policy ownership identifier.");
        string begin = "# ClassMonitor:" + owner + ":Begin", end = "# ClassMonitor:" + owner + ":End";
        bool inside = false; var result = new List<string>();
        foreach (string line in original)
        {
            if (line.Trim() == begin) { if (inside) throw new InvalidDataException("Malformed owned hosts block."); inside = true; continue; }
            if (line.Trim() == end) { if (!inside) throw new InvalidDataException("Malformed owned hosts block."); inside = false; continue; }
            if (!inside) result.Add(line);
        }
        if (inside) throw new InvalidDataException("Malformed owned hosts block.");
        if (domains.Count > 0)
        {
            result.Add(begin);
            foreach (string domain in domains) { result.Add("0.0.0.0 " + domain); if (!domain.StartsWith("www.", StringComparison.Ordinal)) result.Add("0.0.0.0 www." + domain); }
            result.Add(end);
        }
        return result.ToArray();
    }
    public static void Set(string owner, string[] domains)
    {
        lock (Gate)
        {
            string hosts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
            if ((File.GetAttributes(hosts) & FileAttributes.ReparsePoint) != 0) throw new IOException("A linked hosts file cannot be changed.");
            string temporary = hosts + ".classmonitor-" + Guid.NewGuid().ToString("N"), backup = temporary + ".backup";
            try
            {
                using (var exclusive = new FileStream(hosts, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete))
                {
                    using var reader = new StreamReader(exclusive, leaveOpen: true);
                    string[] lines = reader.ReadToEnd().Replace("\r\n", "\n").Split('\n');
                    File.WriteAllLines(temporary, UpdateOwnedLines(lines, owner, domains));
                    File.Replace(temporary, hosts, backup, ignoreMetadataErrors: false);
                }
            }
            finally
            {
                foreach (string file in new[] { temporary, backup }) try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { }
            }
        }
        // DNS cache refresh is best-effort after a successful owned-file transaction.
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ipconfig.exe"), "/flushdns") { UseShellExecute = false, CreateNoWindow = true });
            if (process is not null && !process.WaitForExit(3000)) process.Kill();
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception) { }
    }
}
