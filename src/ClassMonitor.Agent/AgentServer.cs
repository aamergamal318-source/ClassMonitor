using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ClassMonitor.Shared;
using ClassMonitor.Shared.Management;
using ClassMonitor.Shared.Sessions;
using ClassMonitor.Shared.Settings;

namespace ClassMonitor.Agent;

/// <summary>Only administrator-enrolled peers can create an authorized interactive lesson.</summary>
internal sealed class AgentServer : IDisposable
{
    private readonly IAgentInteraction _ui;
    private readonly AppConfig _config;
    private readonly X509Certificate2 _cert;
    private readonly bool _ownsCertificate;
    private readonly ISessionRestrictions _restrictions;
    private readonly Func<DeviceEnrollment?> _enrollment;
    private readonly Func<int, int, int, byte[]> _capture;
    private readonly AgentOperations _operations;
    private readonly ConnectionLimits _limits = new(8, 20);
    private readonly SemaphoreSlim _lessonOwner = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly AuditWriter _audit;
    private readonly Func<PairingPassword?> _pairingPassword;
    private readonly Action<ManagedEnrollment> _savePairing;
    private readonly int _pairingPort;
    private readonly SemaphoreSlim _pairingGate = new(1, 1);
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private TimeSpan _pairingLockedUntil;
    private int _pairingFailures;
    private const int MaximumPairingFailures = 5;
    private static readonly TimeSpan PairingLockout = TimeSpan.FromMinutes(15);
    private TcpListener? _listener;
    private TcpListener? _pairingListener;

    public AgentServer(IAgentInteraction ui, AppConfig config, X509Certificate2? identity = null,
        Func<DeviceEnrollment?>? enrollmentProvider = null, ISessionRestrictions? restrictions = null,
        Func<int, int, int, byte[]>? capture = null, AgentOperations? operations = null, string? auditPath = null,
        Func<PairingPassword?>? pairingPassword = null, Action<ManagedEnrollment>? savePairing = null, int pairingPort = PasswordPairing.Port)
    {
        config.Validate();
        _ui = ui; _config = config;
        _cert = identity ?? WindowsIdentityStore.GetOrCreate("Agent"); _ownsCertificate = identity is null;
        string devicePin = CertificateTrust.Fingerprint(_cert);
        // An enrollment an administrator imported takes precedence over one added with the password.
        _enrollment = enrollmentProvider ?? (() => ManagedEnrollmentStore.Load(devicePin) ?? LocalEnrollmentStore.Load(devicePin));
        _pairingPassword = pairingPassword ?? LocalEnrollmentStore.LoadPassword;
        _savePairing = savePairing ?? LocalEnrollmentStore.SaveEnrollment;
        _pairingPort = pairingPort;
        _restrictions = restrictions ?? new Restrictions(ui);
        _capture = capture ?? ScreenCapture.CaptureJpeg;
        _operations = operations ?? AgentOperations.Default;
        _audit = new AuditWriter(auditPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassMonitor", "audit.jsonl"));
    }

    public void Start()
    {
        _ = Task.Run(() => ListenWhenEnrolledAsync(_cts.Token));
        _ = Task.Run(() => ListenForPairingAsync(_cts.Token));
        if (_config.Settings.Network.DiscoveryEnabled) _ = Task.Run(() => BeaconLoopAsync(_cts.Token));
    }

    private PairingPassword? ReadPairingPassword()
    {
        try { return _pairingPassword(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { return null; }
    }

    /// <summary>The pairing port stays closed until a password was set during installation.</summary>
    private async Task ListenForPairingAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (ReadPairingPassword() is not null)
                {
                    var address = string.IsNullOrEmpty(_config.Settings.Network.LocalInterfaceAddress)
                        ? IPAddress.Any : IPAddress.Parse(_config.Settings.Network.LocalInterfaceAddress);
                    var listener = new TcpListener(address, _pairingPort);
                    listener.Start();
                    _pairingListener = listener;
                    if (ct.IsCancellationRequested) { listener.Stop(); return; }
                    while (!ct.IsCancellationRequested)
                    {
                        var client = await listener.AcceptTcpClientAsync(ct);
                        _ = Task.Run(() => HandlePairingAsync(client, ct));
                    }
                    return;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { /* The port is busy; keep the agent alive and retry. */ }
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One attempt at a time, and a lockout after repeated wrong passwords, so guessing over the network is slow.</summary>
    private async Task HandlePairingAsync(TcpClient client, CancellationToken serverCt)
    {
        using var ownedClient = client;
        if (!await _pairingGate.WaitAsync(0, serverCt)) return;
        try
        {
            client.NoDelay = true;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            await using var tls = await SecureChannel.AcceptForPairingAsync(client, _cert, TimeSpan.FromSeconds(_config.Settings.Network.HandshakeTimeoutSeconds), deadline.Token);
            if (_clock.Elapsed < _pairingLockedUntil)
            {
                await PasswordPairing.RefuseAsync(tls, "locked", deadline.Token);
                return;
            }
            var password = ReadPairingPassword();
            if (password is null) return;
            var device = EnrollmentManager.CreateRequest(_cert, Environment.MachineName + " / " + Environment.UserName);
            try
            {
                var document = await PasswordPairing.AgentAsync(tls, password, device, SecureChannel.RemotePin(tls), _config.Port, deadline.Token);
                _savePairing(document);
                _pairingFailures = 0;
                var added = ReadPolicy();
                if (added is not null) Record(AuditEvent.EnrollmentImported, added, ClassroomPermission.None, true);
                await PasswordPairing.ConfirmAsync(tls, deadline.Token);
                _ui.ShowMessage($"This computer was added by {added?.Teacher.DisplayName}. / أضاف المعلم هذا الجهاز.", 8000);
            }
            catch (AuthenticationException)
            {
                if (++_pairingFailures >= MaximumPairingFailures)
                {
                    _pairingFailures = 0;
                    _pairingLockedUntil = _clock.Elapsed + PairingLockout;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* A failed attempt changes nothing and must not stop the agent. */ }
        finally { _pairingGate.Release(); }
    }

    /// <summary>The port stays closed until a valid enrollment exists; an import needs no restart.</summary>
    private async Task ListenWhenEnrolledAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (ReadPolicy() is not null)
                {
                    var address = string.IsNullOrEmpty(_config.Settings.Network.LocalInterfaceAddress)
                        ? IPAddress.Any : IPAddress.Parse(_config.Settings.Network.LocalInterfaceAddress);
                    var listener = new TcpListener(address, _config.Port);
                    listener.Start();
                    _listener = listener;
                    if (ct.IsCancellationRequested) { listener.Stop(); return; }
                    await AcceptLoopAsync(ct);
                    return;
                }
            }
            catch (SocketException) { /* The port is busy; keep the agent alive and retry. */ }
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private DeviceEnrollment? ReadPolicy()
    {
        try
        {
            var policy = _enrollment();
            if (policy is null || !CertificateTrust.Matches(_cert, policy.DeviceCertificateSha256)) return null;
            EnrollmentManager.ValidateEnrollment(policy);
            return policy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AuthenticationException or JsonException
            or InvalidDataException or ArgumentException or System.Security.Cryptography.CryptographicException)
        { return null; }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(client, ct));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception) when (ct.IsCancellationRequested) { break; }
            catch (SocketException) { await Task.Delay(250, ct); }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverCt)
    {
        using var ownedClient = client;
        string peer = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
        if (!_limits.TryAcquire(peer, out var lease)) return;
        using var admission = lease;
        bool ownsLesson = false;
        var policy = ReadPolicy();
        if (policy is null) return;
        try
        {
            client.NoDelay = true;
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
            handshake.CancelAfter(TimeSpan.FromSeconds(_config.Settings.Network.HandshakeTimeoutSeconds));
            await using var tls = await SecureChannel.AcceptAsync(client, _cert, policy.Teacher.CertificateSha256,
                TimeSpan.FromSeconds(_config.Settings.Network.HandshakeTimeoutSeconds), handshake.Token);
            await SecureChannel.AuthenticateAgentAsync(tls, policy.PairingSecret, handshake.Token);
            ownsLesson = await _lessonOwner.WaitAsync(0, serverCt);
            if (!ownsLesson) throw new UnauthorizedAccessException("Another authorized lesson is active on this device.");
            Record(AuditEvent.ConnectionAccepted, policy, ClassroomPermission.None, true);
            using var remote = X509CertificateLoader.LoadCertificate(tls.RemoteCertificate!.GetRawCertData());
            await new Session(tls, _ui, _restrictions, _config.Settings, policy, CertificateTrust.Fingerprint(remote), ReadPolicy, _capture, _operations, Record).RunAsync(serverCt);
        }
        catch (Exception ex) when (!serverCt.IsCancellationRequested)
        {
            Record(AuditEvent.ConnectionDenied, policy, ClassroomPermission.None, false, ex switch
            {
                AuthenticationException => AuditReason.CertificateMismatch,
                UnauthorizedAccessException => AuditReason.PermissionDenied,
                InvalidDataException or JsonException => AuditReason.MalformedMessage,
                _ => AuditReason.InternalError,
            });
        }
        catch (OperationCanceledException) { }
        finally { if (ownsLesson) _lessonOwner.Release(); }
    }

    /// <summary>The log is in the student's profile. A student locking it must not be able to end a lesson, so a write failure is not fatal.</summary>
    private void Record(AuditEvent action, DeviceEnrollment policy, ClassroomPermission permission, bool allowed, AuditReason reason = AuditReason.None)
    {
        try { _audit.Record(action, policy.Teacher.TeacherId, policy.DeviceId, policy.SchoolId, policy.ClassroomId, permission, allowed, reason); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private async Task BeaconLoopAsync(CancellationToken ct)
    {
        using var udp = new UdpClient { EnableBroadcast = true };
        if (!string.IsNullOrEmpty(_config.Settings.Network.LocalInterfaceAddress))
            udp.Client.Bind(new IPEndPoint(IPAddress.Parse(_config.Settings.Network.LocalInterfaceAddress), 0));
        var endpoint = new IPEndPoint(IPAddress.Broadcast, _config.Settings.Network.DiscoveryPort);
        while (!ct.IsCancellationRequested)
        {
            var policy = ReadPolicy();
            string id = policy?.DeviceId ?? EnrollmentManager.CreateRequest(_cert, Environment.MachineName).DeviceId;
            var payload = Encoding.UTF8.GetBytes($"{Net.DiscoveryMagic}|{Environment.MachineName}|{(policy is null ? "pending" : "enrolled")}|{_config.Port}|{id}");
            try { await udp.SendAsync(payload, endpoint, ct); } catch (SocketException) { }
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose()
    {
        _cts.Cancel(); _listener?.Stop(); _pairingListener?.Stop(); _restrictions.Dispose();
        if (_ownsCertificate) _cert.Dispose();
        // Active tasks observe cancellation; do not dispose their token/semaphore while unwinding.
    }

    private sealed class Session
    {
        private readonly Stream _stream;
        private readonly IAgentInteraction _ui;
        private readonly ISessionRestrictions _restrictions;
        private readonly SchoolSettings _settings;
        private readonly DeviceEnrollment _policy;
        private readonly Func<DeviceEnrollment?> _currentPolicy;
        private readonly Func<int, int, int, byte[]> _capture;
        private readonly AgentOperations _operations;
        private readonly Action<AuditEvent, DeviceEnrollment, ClassroomPermission, bool, AuditReason> _audit;
        private readonly LessonAuthorization _authorization;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly BandwidthLimiter _videoBudget;
        private readonly ConnectionLimits _fileLimits = new(1, 6);
        private volatile ScreenRequest? _request;
        private bool _everStarted, _assisting, _recording, _shared, _locked, _restricted;
        private DateTime _lastPeer = DateTime.UtcNow;
        private DateTime? _focusUntil;
        private CancellationToken _ct;
        private CancellationTokenSource? _stop;

        public Session(Stream stream, IAgentInteraction ui, ISessionRestrictions restrictions, SchoolSettings settings,
            DeviceEnrollment policy, string remotePin, Func<DeviceEnrollment?> currentPolicy, Func<int, int, int, byte[]> capture,
            AgentOperations operations, Action<AuditEvent, DeviceEnrollment, ClassroomPermission, bool, AuditReason> audit)
        {
            _stream = stream; _ui = ui; _restrictions = restrictions; _settings = settings.Clone(); _policy = policy;
            _currentPolicy = currentPolicy; _capture = capture; _operations = operations; _audit = audit;
            _authorization = new LessonAuthorization(policy, remotePin);
            _videoBudget = new BandwidthLimiter(settings.Display.BandwidthMbps);
        }

        public async Task RunAsync(CancellationToken serverCt)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
            _stop = stop; _ct = stop.Token;
            _ui.EndSessionRequested += StopRequested;
            try
            {
                await WriteJsonAsync(MsgType.Hello, new HelloInfo(Environment.MachineName, Environment.UserName), _ct);
                await WriteJsonAsync(MsgType.MonitorsInfo, new MonitorsMsg(Screen.AllScreens.Select((s, i) =>
                    new MonitorInfo(i, s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height, s.Primary)).ToArray()), _ct);
                var tasks = new[] { ReadLoopAsync(_ct), SendLoopAsync(_ct), ActivityLoopAsync(_ct), WatchdogAsync(_ct) };
                await Task.WhenAny(tasks);
                stop.Cancel();
                // A hung capture must not hold cleanup or the lesson slot; cleanup is authoritative on every exit.
                try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); } catch (Exception) { }
            }
            finally
            {
                _ui.EndSessionRequested -= StopRequested;
                stop.Cancel(); _authorization.End(); _request = null;
                try { _restrictions.Clear(); } finally
                {
                    _ui.SetLock(false, ""); _ui.HideBroadcast();
                    _ui.SetManagedState(false, "", "", false, false, false);
                    _operations.Release?.Invoke();
                    try { Log(AuditEvent.LessonEnded, ClassroomPermission.None, true, AuditReason.SessionEnded); } catch { }
                }
            }
        }

        /// <summary>The teacher is told before anything is cancelled, because cancelled socket reads can leave the connection unusable.</summary>
        private void StopRequested() => _ = Task.Run(async () =>
        {
            using var notice = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await WriteJsonAsync(MsgType.SessionEnded, new SessionEndNotice(true), notice.Token); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            finally { _stop?.Cancel(); }
        });
        private void Status() => _ui.SetManagedState(_authorization.IsActive, _policy.Teacher.DisplayName, _policy.ClassroomId, _assisting, _recording, _shared);
        private void Log(AuditEvent action, ClassroomPermission permission, bool allowed, AuditReason reason = AuditReason.None)
            => _audit(action, _policy, permission, allowed, reason);

        private async Task ReadLoopAsync(CancellationToken ct)
        {
            int count = 0; var window = Stopwatch.StartNew();
            while (!ct.IsCancellationRequested)
            {
                var message = await Frame.ReadAsync(_stream, ct);
                if (message is null) return;
                _lastPeer = DateTime.UtcNow;
                if (window.Elapsed >= TimeSpan.FromSeconds(1)) { window.Restart(); count = 0; }
                if (++count > 300) throw new InvalidDataException("Command rate limit exceeded.");
                var (type, payload) = message.Value;
                string requestId = "";
                if (type == MsgType.ManagedCommand)
                {
                    var command = Frame.ReadJson<ManagedCommand>(payload);
                    if (!Guid.TryParseExact(command.RequestId, "N", out _) || command.Payload is null)
                        throw new InvalidDataException("Invalid command envelope.");
                    type = command.Operation; payload = command.Payload; requestId = command.RequestId;
                    if (type is MsgType.LessonStart or MsgType.LessonEnd or MsgType.ManagedCommand or MsgType.HealthPing)
                        throw new InvalidDataException("Invalid nested command.");
                }
                try
                {
                    if (type == MsgType.LessonStart)
                    {
                        var lesson = Frame.ReadJson<LessonRequest>(payload); requestId = lesson.SessionId;
                        _authorization.Start(lesson); _everStarted = true; Status();
                        Log(AuditEvent.LessonStarted, ClassroomPermission.None, true);
                        await ResultAsync(requestId, true, "Lesson authorized", ct); continue;
                    }
                    if (type == MsgType.LessonEnd) return;
                    if (type == MsgType.HealthPing)
                    {
                        // Liveness only: an idle enrolled connection reports no session and grants nothing.
                        bool active = _authorization.IsActive;
                        await WriteJsonAsync(MsgType.SessionHealth, new SessionHealth(active ? _authorization.SessionId! : "",
                            active, active && _assisting, active && _recording), ct); continue;
                    }
                    var permission = CommandPolicy.Required(type, payload);
                    _authorization.Demand(permission);
                    switch (type)
                    {
                        case MsgType.ScreenRequest:
                            var r = Frame.ReadJson<ScreenRequest>(payload);
                            if (r.MaxWidth is < 320 or > 1920 || r.Quality is < 10 or > 90 || r.IntervalMs is < 100 or > 5000
                                || r.MonitorIndex < -1 || r.MonitorIndex >= Screen.AllScreens.Length)
                                throw new InvalidDataException("Invalid screen profile.");
                            _request = r; break;
                        case MsgType.ShowMessage:
                            var m = Frame.ReadJson<MessageCmd>(payload);
                            if (m.Text is null || m.Text.Length > 8000 || m.DurationMs is < 1000 or > 60000) throw new InvalidDataException("Message limits exceeded.");
                            _ui.ShowMessage(m.Text, m.DurationMs); break;
                        case MsgType.Lock:
                            var locked = Frame.ReadJson<LockCmd>(payload);
                            if (locked.Text is null || locked.Text.Length > 2000) throw new InvalidDataException("Lock notice is too long.");
                            _ui.SetLock(locked.Locked, locked.Text);
                            _locked = locked.Locked;
                            if (_locked) _focusUntil ??= DateTime.UtcNow.AddMinutes(_settings.Policies.MaximumFocusMinutes);
                            else if (!_restricted) _focusUntil = null;
                            break;
                        case MsgType.AssistanceState:
                            _assisting = Frame.ReadJson<StateRequest>(payload).Active;
                            if (!_assisting) _operations.Release?.Invoke();
                            Status(); break;
                        case MsgType.RecordingState:
                            // The Record grant in the administrator-reviewed enrollment is the device's consent.
                            bool recording = Frame.ReadJson<StateRequest>(payload).Active;
                            _recording = recording; Status();
                            Log(recording ? AuditEvent.RecordingStarted : AuditEvent.RecordingStopped, ClassroomPermission.Record, true); break;
                        case MsgType.StudentShared:
                            _shared = Frame.ReadJson<StateRequest>(payload).Active;
                            Status(); break;
                        case MsgType.RemoteInput:
                            if (!_assisting) throw new UnauthorizedAccessException("Visible remote assistance has not been authorized.");
                            _operations.Input(Frame.ReadJson<RemoteInputCmd>(payload), _request?.MonitorIndex ?? -1); break;
                        case MsgType.Power:
                            var power = Frame.ReadJson<PowerCmd>(payload);
                            // A restart ends this process with no chance to restore system changes afterwards.
                            _restrictions.Clear(); _ui.SetLock(false, ""); _locked = false; _restricted = false; _focusUntil = null;
                            _operations.Power(power); break;
                        case MsgType.Run: _operations.Run(Frame.ReadJson<RunCmd>(payload)); break;
                        case MsgType.FileSend:
                            using (AcquireFileLease())
                            {
                                string saved = FileTransfer.SaveReceived(Frame.ReadJson<FilePush>(payload), _settings.Files);
                                _ui.ShowMessage($"Lesson file received: {saved}", 8000);
                            }
                            break;
                        case MsgType.FileRequest:
                            using (AcquireFileLease())
                                await WriteJsonAsync(MsgType.FileData, FileTransfer.ReadForCollect(Frame.ReadJson<FileRequestCmd>(payload).Path, _settings.Files), ct);
                            break;
                        case MsgType.TeacherFrame:
                            if (payload.Length > 2 * 1024 * 1024) throw new InvalidDataException("Presentation frame too large.");
                            _ui.ShowBroadcastFrame(payload); break;
                        case MsgType.TeacherBroadcast:
                            if (!Frame.ReadJson<BroadcastCmd>(payload).Active) _ui.HideBroadcast(); break;
                        case MsgType.Chat:
                            var chat = Frame.ReadJson<ChatMsg>(payload);
                            if (chat.Text is null || chat.Text.Length > 8000) throw new InvalidDataException("Chat text too long.");
                            _ui.ShowChat(_policy.Teacher.DisplayName, chat.Text, SendChatAsync); break;
                        case MsgType.Question:
                            var question = Frame.ReadJson<QuestionCmd>(payload);
                            if (!Guid.TryParseExact(question.QuestionId, "N", out _) || question.Text is null || question.Text.Length > 8000)
                                throw new InvalidDataException("Invalid question.");
                            _ui.ShowChat("Question: " + _policy.Teacher.DisplayName, question.Text,
                                text =>
                                {
                                    _authorization.Demand(ClassroomPermission.Message);
                                    if (text.Length > 8000) throw new InvalidDataException("Answer text too long.");
                                    return WriteJsonAsync(MsgType.Answer, new AnswerMsg(question.QuestionId, text), ct);
                                }); break;
                        case MsgType.Restrict:
                            var restriction = Frame.ReadJson<RestrictCmd>(payload);
                            _restrictions.Apply(restriction);
                            if (restriction.Enabled) { _restricted = true; _focusUntil ??= DateTime.UtcNow.AddMinutes(_settings.Policies.MaximumFocusMinutes); }
                            break;
                    }
                    // Input, frames and stream changes run many times a second and would push lesson events out of the bounded log.
                    if (type is not (MsgType.RemoteInput or MsgType.TeacherFrame or MsgType.ScreenRequest or MsgType.RecordingState))
                        Log(AuditEvent.OperationAllowed, permission, true);
                    if (requestId.Length != 0) await ResultAsync(requestId, true, "Operation completed", ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
                {
                    // One command failing, such as a firewall change without administrator rights, does not end the lesson.
                    bool refused = ex is UnauthorizedAccessException;
                    bool invalid = ex is InvalidDataException or ArgumentException or JsonException or FormatException or NullReferenceException;
                    Log(AuditEvent.OperationDenied, ClassroomPermission.None, false, refused ? AuditReason.PermissionDenied : invalid ? AuditReason.MalformedMessage : AuditReason.InternalError);
                    await ResultAsync(requestId.Length == 0 ? "denied" : requestId, false,
                        refused ? "Operation is not authorized by classroom/session policy."
                        : invalid ? "Invalid or unsupported operation."
                        : "This computer could not complete the operation. It may need administrator rights.", ct);
                }
            }
        }

        private IDisposable AcquireFileLease()
        {
            if (!_fileLimits.TryAcquire("lesson-files", out var lease)) throw new UnauthorizedAccessException("File transfer rate exceeded.");
            return lease!;
        }

        private async Task SendLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var r = _request;
                if (r is null || !_authorization.IsActive) { await Task.Delay(100, ct); continue; }
                _authorization.Demand(ClassroomPermission.View);
                byte[] jpeg = await Task.Run(() => _capture(r.MaxWidth, r.Quality, r.MonitorIndex), ct);
                await _videoBudget.WaitAsync(jpeg.Length, ct);
                _authorization.Demand(ClassroomPermission.View);
                await WriteAsync(MsgType.ScreenFrame, jpeg, ct);
                await Task.Delay(r.IntervalMs, ct);
            }
        }

        private async Task WatchdogAsync(CancellationToken ct)
        {
            int checks = 0;
            string policy = JsonSerializer.Serialize(_policy);
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(500, ct);
                if ((_everStarted && !_authorization.IsActive)
                    || DateTime.UtcNow - _lastPeer > TimeSpan.FromSeconds(_settings.Network.HealthTimeoutSeconds)) return;
                if (++checks % 4 == 0 && JsonSerializer.Serialize(_currentPolicy()) != policy) return;
                if (_focusUntil <= DateTime.UtcNow)
                {
                    _restrictions.Clear(); _ui.SetLock(false, ""); _focusUntil = null; _locked = false; _restricted = false;
                }
            }
        }

        private async Task ActivityLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                if (_authorization.IsActive && (_policy.Teacher.Permissions & ClassroomPermission.View) != 0)
                {
                    var (title, process) = ForegroundInfo();
                    await WriteJsonAsync(MsgType.Activity, new ActivityMsg(title, process), ct);
                }
                await Task.Delay(3000, ct);
            }
        }

        private Task SendChatAsync(string text)
        {
            _authorization.Demand(ClassroomPermission.Message);
            if (text.Length > 8000) throw new InvalidDataException("Chat text too long.");
            return WriteJsonAsync(MsgType.Chat, new ChatMsg(Environment.MachineName, text), _ct);
        }

        private Task ResultAsync(string requestId, bool ok, string message, CancellationToken ct)
            => WriteJsonAsync(MsgType.StateResult, new StateResult(requestId, ok, message), ct);
        private async Task WriteJsonAsync<T>(MsgType type, T value, CancellationToken ct)
        {
            await _writeLock.WaitAsync(ct);
            try { await Frame.WriteJsonAsync(_stream, type, value, ct); }
            finally { _writeLock.Release(); }
        }
        private async Task WriteAsync(MsgType type, byte[] value, CancellationToken ct)
        {
            await _writeLock.WaitAsync(ct);
            try
            {
                if (type == MsgType.ScreenFrame) _authorization.Demand(ClassroomPermission.View);
                await Frame.WriteAsync(_stream, type, value, ct);
            }
            finally { _writeLock.Release(); }
        }
        private static (string, string) ForegroundInfo()
        {
            try
            {
                var window = Native.GetForegroundWindow();
                if (window == IntPtr.Zero) return ("", "");
                var text = new char[512]; int length = Native.GetWindowText(window, text, text.Length);
                Native.GetWindowThreadProcessId(window, out uint pid);
                using var process = Process.GetProcessById((int)pid);
                return (length > 0 ? new string(text, 0, length) : "", process.ProcessName);
            }
            catch { return ("", ""); }
        }
    }
}
