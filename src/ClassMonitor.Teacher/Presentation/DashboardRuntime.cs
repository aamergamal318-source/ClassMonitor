using ClassMonitor.Shared;
using ClassMonitor.Shared.Sessions;

namespace ClassMonitor.Teacher.Presentation;

public static class PairingLookup
{
    public static bool IsExpired(PairedDevice peer) =>
        peer.Enrollment.ExpiresAtUtc <= DateTimeOffset.UtcNow || peer.Enrollment.Teacher.ExpiresAtUtc <= DateTimeOffset.UtcNow;

    public static PairedDevice? Find(IEnumerable<PairedDevice> devices, string host, string? discoveredDeviceId = null)
    {
        // A beacon names the account running there now; one computer can hold records for several accounts.
        var matches = string.IsNullOrWhiteSpace(discoveredDeviceId)
            ? devices.Where(p => string.Equals(p.Host, host, StringComparison.OrdinalIgnoreCase)).ToArray()
            : devices.Where(p => string.Equals(p.Enrollment.DeviceId, discoveredDeviceId, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1) return null;
        var peer = matches[0];
        return IsExpired(peer) ? null : peer;
    }
}

public sealed class ReconnectSchedule
{
    private readonly Dictionary<string, (int Failures, DateTimeOffset RetryAfter)> _entries = new(StringComparer.OrdinalIgnoreCase);
    public void Failed(string host, DateTimeOffset now)
    {
        int failures = Math.Min(6, _entries.GetValueOrDefault(host).Failures + 1);
        _entries[host] = (failures, now.AddSeconds(Math.Min(60, Math.Pow(2, failures))));
    }
    public bool CanTry(string host, DateTimeOffset now) => !_entries.TryGetValue(host, out var entry) || entry.RetryAfter <= now;
    public void Connected(string host) => _entries.Remove(host);
}

/// <summary>A single pending UI callback per view; arriving frames replace stale queued bytes.</summary>
public sealed class LatestFrameDispatcher : IDisposable
{
    private readonly Control _owner;
    private readonly Action<byte[]> _render;
    private byte[]? _latest;
    private int _queued;
    private int _disposed;
    public LatestFrameDispatcher(Control owner, Action<byte[]> render) { _owner = owner; _render = render; }
    public void Submit(byte[] jpeg)
    {
        if (Volatile.Read(ref _disposed) != 0 || jpeg.Length > Net.MaxMessageBytes) return;
        Interlocked.Exchange(ref _latest, jpeg);
        Queue();
    }
    private void Queue()
    {
        if (Volatile.Read(ref _disposed) != 0 || _owner.IsDisposed || !_owner.IsHandleCreated) return;
        if (Interlocked.CompareExchange(ref _queued, 1, 0) != 0) return;
        try { _owner.BeginInvoke(Drain); }
        catch (InvalidOperationException) { Interlocked.Exchange(ref _queued, 0); }
    }
    private void Drain()
    {
        try
        {
            if (Volatile.Read(ref _disposed) == 0 && !_owner.IsDisposed && Interlocked.Exchange(ref _latest, null) is { } jpeg) _render(jpeg);
        }
        finally
        {
            Interlocked.Exchange(ref _queued, 0);
            if (Volatile.Read(ref _latest) is not null) Queue();
        }
    }
    public void Dispose() { Interlocked.Exchange(ref _disposed, 1); Interlocked.Exchange(ref _latest, null); }
}
