using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ClassMonitor.Shared;

namespace ClassMonitor.Agent;

/// <summary>
/// TCP server the teacher console connects to, plus a UDP beacon so the console can
/// auto-discover this machine on the LAN. Every connection is wrapped in TLS and then
/// authenticated with the shared class password before any command is processed.
/// One <see cref="Session"/> per authenticated teacher connection.
/// </summary>
internal sealed class AgentServer : IDisposable
{
    private readonly AgentUi _ui;
    private readonly AppConfig _config;
    private readonly byte[] _key;
    private readonly X509Certificate2 _cert;
    private readonly Restrictions _restrictions;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;

    public AgentServer(AgentUi ui, AppConfig config)
    {
        _ui = ui;
        _config = config;
        _key = Security.DeriveKey(config.Password);
        _cert = CertUtil.GetOrCreate();
        _restrictions = new Restrictions(ui);
    }

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Any, _config.Port);
        _listener.Start();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _ = Task.Run(() => BeaconLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch { /* transient accept error; keep serving */ }
        }
    }

    /// <summary>TLS handshake + password authentication, then hand off to a <see cref="Session"/>.</summary>
    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        SslStream? ssl = null;
        try
        {
            client.NoDelay = true;
            ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(_cert, clientCertificateRequired: false,
                SslProtocols.Tls12 | SslProtocols.Tls13, checkCertificateRevocation: false);

            if (!await AuthenticateServerAsync(ssl, ct))
            {
                ssl.Dispose(); client.Dispose();
                return;
            }

            await new Session(client, ssl, _ui, _restrictions).RunAsync(ct);
        }
        catch
        {
            ssl?.Dispose();
            client.Dispose();
        }
    }

    /// <summary>Server side of the mutual challenge-response. Returns false if the teacher fails auth.</summary>
    private async Task<bool> AuthenticateServerAsync(Stream s, CancellationToken ct)
    {
        string nonceS = Security.NewNonce();
        await Frame.WriteJsonAsync(s, MsgType.AuthHello, new AuthHello(nonceS), ct);

        var msg = await Frame.ReadAsync(s, ct);
        if (msg is null || msg.Value.Type != MsgType.AuthRequest) return false;
        var req = Frame.ReadJson<AuthRequest>(msg.Value.Payload);

        string expected = Security.Proof(_key, "teacher", nonceS, req.NonceT);
        if (!Security.ProofsEqual(expected, req.ProofT))
        {
            await Frame.WriteJsonAsync(s, MsgType.AuthResult, new AuthResult(false, ""), ct);
            return false;
        }

        string proofS = Security.Proof(_key, "agent", nonceS, req.NonceT);
        await Frame.WriteJsonAsync(s, MsgType.AuthResult, new AuthResult(true, proofS), ct);
        return true;
    }

    /// <summary>Broadcast "CLASSMON1|machine|user|port" every two seconds for discovery.</summary>
    private async Task BeaconLoopAsync(CancellationToken ct)
    {
        using var udp = new UdpClient { EnableBroadcast = true };
        var endpoint = new IPEndPoint(IPAddress.Broadcast, Net.DiscoveryPort);
        var payload = Encoding.UTF8.GetBytes(
            $"{Net.DiscoveryMagic}|{Environment.MachineName}|{Environment.UserName}|{_config.Port}");

        while (!ct.IsCancellationRequested)
        {
            try { await udp.SendAsync(payload, payload.Length, endpoint); }
            catch { /* interface may be down; retry next tick */ }
            try { await Task.Delay(2000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener?.Stop();
        _restrictions.Dispose();
        _cert.Dispose();
        _cts.Dispose();
    }

    /// <summary>One teacher connection: streams screenshots and executes incoming commands.</summary>
    private sealed class Session
    {
        private readonly TcpClient _client;
        private readonly Stream _stream;     // authenticated TLS stream
        private readonly AgentUi _ui;
        private readonly Restrictions _restrictions;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private volatile ScreenRequest? _request;
        private bool _didLock;
        private bool _didBroadcast;
        private CancellationToken _ct;

        public Session(TcpClient client, Stream stream, AgentUi ui, Restrictions restrictions)
        {
            _client = client;
            _stream = stream;
            _ui = ui;
            _restrictions = restrictions;
        }

        public async Task RunAsync(CancellationToken serverCt)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
            var ct = linked.Token;
            try
            {
                using var _ = _client;
                await using var ns = _stream;
                _ct = ct;

                var hello = new HelloInfo(Environment.MachineName, Environment.UserName);
                await WriteJsonAsync(ns, MsgType.Hello, hello, ct);
                await WriteJsonAsync(ns, MsgType.MonitorsInfo, BuildMonitors(), ct);

                var reader = ReadLoopAsync(ns, ct);
                var sender = SendLoopAsync(ns, ct);
                var activity = ActivityLoopAsync(ns, ct);
                await Task.WhenAny(reader, sender, activity);
                linked.Cancel();
                await Task.WhenAll(
                    reader.ContinueWith(_ => { }, TaskScheduler.Default),
                    sender.ContinueWith(_ => { }, TaskScheduler.Default),
                    activity.ContinueWith(_ => { }, TaskScheduler.Default));
            }
            catch { /* connection ended */ }
            finally
            {
                // Never leave the student stuck if the teacher drops mid-lock/broadcast.
                if (_didLock) _ui.SetLock(false, "");
                if (_didBroadcast) _ui.HideBroadcast();
            }
        }

        private async Task ReadLoopAsync(Stream ns, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var msg = await Frame.ReadAsync(ns, ct);
                if (msg is null) break;
                var (type, payload) = msg.Value;

                switch (type)
                {
                    case MsgType.ScreenRequest:
                        _request = Frame.ReadJson<ScreenRequest>(payload);
                        break;

                    case MsgType.ShowMessage:
                        var m = Frame.ReadJson<MessageCmd>(payload);
                        _ui.ShowMessage(m.Text, m.DurationMs);
                        break;

                    case MsgType.Lock:
                        var l = Frame.ReadJson<LockCmd>(payload);
                        _didLock = l.Locked;
                        _ui.SetLock(l.Locked, l.Text);
                        break;

                    case MsgType.RemoteInput:
                        RemoteInput.Apply(Frame.ReadJson<RemoteInputCmd>(payload));
                        break;

                    case MsgType.Power:
                        PowerControl.Apply(Frame.ReadJson<PowerCmd>(payload).Action);
                        break;

                    case MsgType.Run:
                        RunControl.Apply(Frame.ReadJson<RunCmd>(payload));
                        break;

                    case MsgType.FileSend:
                        var push = Frame.ReadJson<FilePush>(payload);
                        try
                        {
                            string saved = FileTransfer.SaveReceived(push);
                            _ui.ShowMessage($"The teacher sent you a file:\n{saved}\n(saved in Desktop\\ClassMonitor)", 8000);
                        }
                        catch { /* disk error; ignore */ }
                        break;

                    case MsgType.FileRequest:
                        var reqPath = Frame.ReadJson<FileRequestCmd>(payload).Path;
                        var data = FileTransfer.ReadForCollect(reqPath);
                        await WriteJsonAsync(ns, MsgType.FileData, data, ct);
                        break;

                    case MsgType.TeacherFrame:
                        _didBroadcast = true;
                        _ui.ShowBroadcastFrame(payload);
                        break;

                    case MsgType.TeacherBroadcast:
                        var bc = Frame.ReadJson<BroadcastCmd>(payload);
                        if (!bc.Active) { _ui.HideBroadcast(); _didBroadcast = false; }
                        break;

                    case MsgType.Chat:
                        var cm = Frame.ReadJson<ChatMsg>(payload);
                        _ui.ShowChat(cm.From, cm.Text, SendChatTextAsync);
                        break;

                    case MsgType.Restrict:
                        _restrictions.Apply(Frame.ReadJson<RestrictCmd>(payload));
                        break;
                }
            }
        }

        /// <summary>Send a chat line from the student back to the teacher.</summary>
        public async Task SendChatTextAsync(string text)
        {
            try { await WriteJsonAsync(_stream, MsgType.Chat, new ChatMsg(Environment.MachineName, text), _ct); }
            catch { /* connection gone */ }
        }

        private async Task SendLoopAsync(Stream ns, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var r = _request;
                if (r is null)
                {
                    await Task.Delay(200, ct);
                    continue;
                }

                byte[] jpeg = await Task.Run(() => ScreenCapture.CaptureJpeg(r.MaxWidth, r.Quality, r.MonitorIndex), ct);
                await WriteAsync(ns, MsgType.ScreenFrame, jpeg, ct);
                await Task.Delay(Math.Max(100, r.IntervalMs), ct);
            }
        }

        private static MonitorsMsg BuildMonitors()
        {
            var list = Screen.AllScreens
                .Select((s, i) => new MonitorInfo(i, s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height, s.Primary))
                .ToArray();
            return new MonitorsMsg(list);
        }

        /// <summary>Periodically report the foreground window title + process (no keystroke capture).</summary>
        private async Task ActivityLoopAsync(Stream ns, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var (title, proc) = ForegroundInfo();
                await WriteJsonAsync(ns, MsgType.Activity, new ActivityMsg(title, proc), ct);
                await Task.Delay(3000, ct);
            }
        }

        private static (string Title, string Process) ForegroundInfo()
        {
            try
            {
                var h = Native.GetForegroundWindow();
                if (h == IntPtr.Zero) return ("", "");

                var buf = new char[512];
                int n = Native.GetWindowText(h, buf, buf.Length);
                string title = n > 0 ? new string(buf, 0, n) : "";

                Native.GetWindowThreadProcessId(h, out uint pid);
                string proc = "";
                try { using var p = Process.GetProcessById((int)pid); proc = p.ProcessName; } catch { }

                return (title, proc);
            }
            catch { return ("", ""); }
        }

        private async Task WriteAsync(Stream ns, MsgType type, byte[] payload, CancellationToken ct)
        {
            await _writeLock.WaitAsync(ct);
            try { await Frame.WriteAsync(ns, type, payload, ct); }
            finally { _writeLock.Release(); }
        }

        private async Task WriteJsonAsync<T>(Stream ns, MsgType type, T value, CancellationToken ct)
        {
            await _writeLock.WaitAsync(ct);
            try { await Frame.WriteJsonAsync(ns, type, value, ct); }
            finally { _writeLock.Release(); }
        }
    }
}
