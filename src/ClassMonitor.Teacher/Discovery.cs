using System.Net;
using System.Net.Sockets;
using System.Text;
using ClassMonitor.Shared;

namespace ClassMonitor.Teacher;

/// <summary>Listens for agent UDP beacons and raises an event the first time each host is seen.</summary>
internal sealed class Discovery : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _udp;

    /// <summary>Raised on a background thread with (host, machineName, userName, port).</summary>
    public event Action<string, string, string, int>? AgentSeen;

    public void Start()
    {
        _udp = new UdpClient();
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, Net.DiscoveryPort));
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
                if (parts.Length >= 3 && parts[0] == Net.DiscoveryMagic)
                {
                    string host = result.RemoteEndPoint.Address.ToString();
                    int port = parts.Length >= 4 && int.TryParse(parts[3], out var p) ? p : Net.AgentTcpPort;
                    AgentSeen?.Invoke(host, parts[1], parts[2], port);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* ignore malformed datagrams */ }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp?.Dispose();
        _cts.Dispose();
    }
}
