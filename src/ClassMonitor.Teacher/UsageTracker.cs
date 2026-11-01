namespace ClassMonitor.Teacher;

/// <summary>
/// Accumulates foreground-application time per machine from the agents' periodic activity
/// reports. Each report represents roughly one sampling interval of active use.
/// </summary>
internal sealed class UsageTracker
{
    private readonly Dictionary<(string Machine, string App), int> _seconds = new();
    private readonly object _lock = new();

    public void Add(string machine, string app, int seconds)
    {
        if (string.IsNullOrWhiteSpace(app)) app = "(unknown)";
        lock (_lock)
        {
            var key = (machine, app);
            _seconds[key] = _seconds.GetValueOrDefault(key) + seconds;
        }
    }

    public List<(string Machine, string App, int Seconds)> Rows()
    {
        lock (_lock)
        {
            return _seconds
                .Select(kv => (kv.Key.Machine, kv.Key.App, kv.Value))
                .OrderBy(r => r.Machine, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(r => r.Value)
                .ToList();
        }
    }
}
