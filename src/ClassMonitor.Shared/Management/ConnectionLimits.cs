namespace ClassMonitor.Shared.Management;

/// <summary>Bounded admission: concurrent connections globally and attempts per peer/minute.</summary>
public sealed class ConnectionLimits
{
    private readonly int _maxConcurrent, _attemptsPerMinute;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, Window> _peers = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private int _active;
    private sealed record Window(long Started, int Attempts);

    public ConnectionLimits(int maxConcurrent = 8, int attemptsPerMinute = 10, TimeProvider? clock = null)
    {
        if (maxConcurrent is < 1 or > 128 || attemptsPerMinute is < 1 or > 1000) throw new ArgumentOutOfRangeException();
        _maxConcurrent = maxConcurrent; _attemptsPerMinute = attemptsPerMinute; _clock = clock ?? TimeProvider.System;
    }

    public bool TryAcquire(string peer, out IDisposable? lease)
    {
        lease = null;
        if (string.IsNullOrWhiteSpace(peer) || peer.Length > 128 || peer.Any(char.IsControl)) return false;
        lock (_gate)
        {
            long now = _clock.GetTimestamp();
            if (!_peers.TryGetValue(peer, out var window) || _clock.GetElapsedTime(window.Started, now) >= TimeSpan.FromMinutes(1))
            {
                if (_peers.Count >= 1024)
                {
                    foreach (var expired in _peers.Where(p => _clock.GetElapsedTime(p.Value.Started, now) >= TimeSpan.FromMinutes(1)).Select(p => p.Key).ToArray())
                        _peers.Remove(expired);
                    if (_peers.Count >= 1024 && !_peers.ContainsKey(peer)) return false;
                }
                window = new Window(now, 0);
            }
            _peers[peer] = window with { Attempts = Math.Min(window.Attempts + 1, _attemptsPerMinute + 1) };
            if (window.Attempts >= _attemptsPerMinute || _active >= _maxConcurrent) return false;
            _active++;
            lease = new Lease(this);
            return true;
        }
    }

    private sealed class Lease(ConnectionLimits owner) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (owner._gate) owner._active--;
        }
    }
}
