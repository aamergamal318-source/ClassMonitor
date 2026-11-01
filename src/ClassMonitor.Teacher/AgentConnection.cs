using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using ClassMonitor.Shared;

namespace ClassMonitor.Teacher;

/// <summary>
/// The teacher side of one agent connection: opens a TLS socket to the agent, authenticates
/// with the shared class password, receives screenshot frames, and sends commands. All command
/// sends are serialized with a lock.
/// </summary>
internal sealed class AgentConnection : IDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly int _port;
    private readonly byte[] _key;
    private TcpClient? _client;
    private Stream? _ns;

    public string Host { get; }
    public string MachineName { get; private set; }
    public string UserName { get; private set; } = "";
    public MonitorInfo[] Monitors { get; private set; } = Array.Empty<MonitorInfo>();
    public byte[]? LastFrame { get; private set; }
    public ActivityMsg? LastActivity { get; private set; }
    public DateTime LastActivityAt { get; private set; } = DateTime.MinValue;

    /// <summary>Raised on a background thread with the latest JPEG frame.</summary>
    public event Action<AgentConnection, byte[]>? FrameReceived;
    public event Action<AgentConnection>? Disconnected;

    /// <summary>Raised on a background thread when a collected file arrives from this agent.</summary>
    public event Action<AgentConnection, FileDataMsg>? FileCollected;

    /// <summary>Raised on a background thread when a chat line arrives from this agent.</summary>
    public event Action<AgentConnection, ChatMsg>? ChatReceived;

    /// <summary>Raised when the agent reports its monitor layout.</summary>
    public event Action<AgentConnection>? MonitorsReceived;

    /// <summary>Raised (background thread) when the agent reports its foreground activity.</summary>
    public event Action<AgentConnection, ActivityMsg>? ActivityReceived;

    public AgentConnection(string host, string machineName, int port, byte[] key)
    {
        Host = host;
        MachineName = machineName;
        _port = port;
        _key = key;
    }

    public async Task ConnectAsync()
    {
        var ct = _cts.Token;
        _client = new TcpClient { NoDelay = true };
        await _client.ConnectAsync(Host, _port, ct);

        // TLS: encrypt the channel. We accept the agent's self-signed cert; trust comes from
        // the password handshake below, not from a certificate authority.
        var ssl = new SslStream(_client.GetStream(), leaveInnerStreamOpen: false,
            (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "ClassMonitor",
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            // Cert validation is handled by the callback passed to the SslStream constructor;
            // setting it here too would throw InvalidOperationException.
        }, ct);

        await AuthenticateClientAsync(ssl, ct);
        _ns = ssl;
        _ = Task.Run(() => ReadLoopAsync(ct));

        // Default thumbnail stream: small and gentle on the network.
        await RequestScreenAsync(maxWidth: 400, quality: 45, intervalMs: 1000);
    }

    /// <summary>Client side of the mutual challenge-response; throws if the password is wrong.</summary>
    private async Task AuthenticateClientAsync(Stream s, CancellationToken ct)
    {
        var hello = await Frame.ReadAsync(s, ct);
        if (hello is null || hello.Value.Type != MsgType.AuthHello)
            throw new AuthenticationException("No auth hello from agent");
        var ah = Frame.ReadJson<AuthHello>(hello.Value.Payload);

        string nonceT = Security.NewNonce();
        string proofT = Security.Proof(_key, "teacher", ah.NonceS, nonceT);
        await Frame.WriteJsonAsync(s, MsgType.AuthRequest, new AuthRequest(nonceT, proofT), ct);

        var res = await Frame.ReadAsync(s, ct);
        if (res is null || res.Value.Type != MsgType.AuthResult)
            throw new AuthenticationException("No auth result from agent");
        var ar = Frame.ReadJson<AuthResult>(res.Value.Payload);

        string expected = Security.Proof(_key, "agent", ah.NonceS, nonceT);
        if (!ar.Ok || !Security.ProofsEqual(expected, ar.ProofS))
            throw new AuthenticationException("Wrong class password");
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var msg = await Frame.ReadAsync(_ns!, ct);
                if (msg is null) break;
                var (type, payload) = msg.Value;

                switch (type)
                {
                    case MsgType.Hello:
                        var h = Frame.ReadJson<HelloInfo>(payload);
                        MachineName = h.MachineName;
                        UserName = h.UserName;
                        break;

                    case MsgType.ScreenFrame:
                        LastFrame = payload;
                        FrameReceived?.Invoke(this, payload);
                        break;

                    case MsgType.MonitorsInfo:
                        Monitors = Frame.ReadJson<MonitorsMsg>(payload).Monitors;
                        MonitorsReceived?.Invoke(this);
                        break;

                    case MsgType.Activity:
                        var act = Frame.ReadJson<ActivityMsg>(payload);
                        LastActivity = act;
                        LastActivityAt = DateTime.UtcNow;
                        ActivityReceived?.Invoke(this, act);
                        break;

                    case MsgType.FileData:
                        FileCollected?.Invoke(this, Frame.ReadJson<FileDataMsg>(payload));
                        break;

                    case MsgType.Chat:
                        ChatReceived?.Invoke(this, Frame.ReadJson<ChatMsg>(payload));
                        break;
                }
            }
        }
        catch { /* dropped */ }
        finally { Disconnected?.Invoke(this); }
    }

    public Task RequestScreenAsync(int maxWidth, int quality, int intervalMs, int monitorIndex = -1) =>
        SendJsonAsync(MsgType.ScreenRequest, new ScreenRequest(maxWidth, quality, intervalMs, monitorIndex));

    public Task SendMessageAsync(string text, int durationMs) =>
        SendJsonAsync(MsgType.ShowMessage, new MessageCmd(text, durationMs));

    public Task LockAsync(bool locked, string text) =>
        SendJsonAsync(MsgType.Lock, new LockCmd(locked, text));

    public Task RemoteInputAsync(RemoteInputCmd cmd) =>
        SendJsonAsync(MsgType.RemoteInput, cmd);

    public Task PowerAsync(string action) =>
        SendJsonAsync(MsgType.Power, new PowerCmd(action));

    public Task RunAsync(string kind, string target) =>
        SendJsonAsync(MsgType.Run, new RunCmd(kind, target));

    public Task SendFileAsync(string fileName, byte[] bytes) =>
        SendJsonAsync(MsgType.FileSend, new FilePush(fileName, Convert.ToBase64String(bytes)));

    public Task RequestFileAsync(string path) =>
        SendJsonAsync(MsgType.FileRequest, new FileRequestCmd(path));

    public Task SendChatAsync(string text) =>
        SendJsonAsync(MsgType.Chat, new ChatMsg("Teacher", text));

    public Task RestrictAsync(string kind, bool enabled, string[] items, string? mode = null) =>
        SendJsonAsync(MsgType.Restrict, new RestrictCmd(kind, enabled, items, mode));

    public Task SetBroadcastAsync(bool active) =>
        SendJsonAsync(MsgType.TeacherBroadcast, new BroadcastCmd(active));

    public Task SendTeacherFrameAsync(byte[] jpeg) =>
        SendRawAsync(MsgType.TeacherFrame, jpeg);

    private async Task SendJsonAsync<T>(MsgType type, T value)
    {
        if (_ns is null) return;
        await _writeLock.WaitAsync(_cts.Token);
        try { await Frame.WriteJsonAsync(_ns, type, value, _cts.Token); }
        catch { /* drop */ }
        finally { _writeLock.Release(); }
    }

    private async Task SendRawAsync(MsgType type, byte[] payload)
    {
        if (_ns is null) return;
        await _writeLock.WaitAsync(_cts.Token);
        try { await Frame.WriteAsync(_ns, type, payload, _cts.Token); }
        catch { /* drop */ }
        finally { _writeLock.Release(); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _ns?.Dispose();
        _client?.Dispose();
        _cts.Dispose();
    }
}
