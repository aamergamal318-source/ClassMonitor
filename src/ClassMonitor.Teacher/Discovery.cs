using System.Net;
using System.Net.Sockets;
using System.Text;
using ClassMonitor.Shared;
using ClassMonitor.Shared.Settings;

namespace ClassMonitor.Teacher;

/// <summary>Listens for agent UDP beacons and raises an event the first time each host is seen.</summary>
internal sealed class Discovery : IDisposable
{
    private CancellationTokenSource? _cts;
    private UdpClient? _udp;

    /// <summary>Raised on a background thread with (host, machineName, userName, port).</summary>
    public event Action<string, string, string, int, string>? AgentSeen;

    public void Start(NetworkSettings? settings = null)
    {
        Stop();
        settings ??= new NetworkSettings();
        new SchoolSettings { Network = settings }.EnsureValid();
        if (!settings.DiscoveryEnabled) return;
        _cts = new CancellationTokenSource();
        _udp = new UdpClient();
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        var address = string.IsNullOrWhiteSpace(settings.LocalInterfaceAddress) ? IPAddress.Any : IPAddress.Parse(settings.LocalInterfaceAddress);
        _udp.Client.Bind(new IPEndPoint(address, settings.DiscoveryPort));
        _ = Task.Run(() => ListenAsync(_cts.Token));
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _udp!.ReceiveAsync(ct);
                var text = Encoding.UTF8.GetString(result.Buffer);
                var parts = text.Split('|');
                if (parts.Length >= 5 && parts[0] == Net.DiscoveryMagic && parts[1].Length <= 128 && parts[4].Length <= 128)
                {
                    string host = result.RemoteEndPoint.Address.ToString();
                    int port = parts.Length >= 4 && int.TryParse(parts[3], out var p) ? p : Net.AgentTcpPort;
                    if (port is >= 1024 and <= 65535) AgentSeen?.Invoke(host, parts[1], parts[2], port, parts[4]);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* ignore malformed datagrams */ }
        }
    }

    public void Dispose()
        => Stop();

    public void Stop()
    {
        _cts?.Cancel();
        _udp?.Dispose();
        _cts?.Dispose();
        _cts = null;
        _udp = null;
    }
}
