using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using ClassMonitor.Shared;
using ClassMonitor.Shared.Management;
using ClassMonitor.Shared.Sessions;
using ClassMonitor.Shared.Settings;

namespace ClassMonitor.Teacher;

/// <summary>
/// The teacher side of one enrolled agent: mutually authenticated TLS bound to the device pin,
/// a pairing proof, and a bounded lesson. No frame or command flows until a lesson is authorized,
/// and the agent re-checks every operation independently. All sends are serialized with a lock.
/// </summary>
internal sealed class AgentConnection : IDisposable
{
    private static readonly TimeSpan ResultTimeout = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<StateResult>> _pending = new();
    private readonly PairedDevice _pairing;
    private readonly X509Certificate2 _identity;
    private readonly SchoolSettings _settings;
    private readonly AdaptiveStreamProfile _profile = new();
    private TcpClient? _client;
    private Stream? _ns;
    private DateTimeOffset _lessonExpiresAt;
    private DateTime _lastHealth = DateTime.UtcNow;
    private int _disconnected, _pendingFiles;
    private volatile bool _disposed, _lessonActive;

    public string Host { get; }
    public string MachineName { get; private set; }
    public string UserName { get; private set; } = "";
    public MonitorInfo[] Monitors { get; private set; } = Array.Empty<MonitorInfo>();
    public byte[]? LastFrame { get; private set; }
    public DateTime LastFrameAt { get; private set; } = DateTime.MinValue;
    public long FramesReceived { get; private set; }
    public string LastReadError { get; private set; } = "";
    public ActivityMsg? LastActivity { get; private set; }
    public DateTime LastActivityAt { get; private set; } = DateTime.MinValue;
    public bool LessonActive => _lessonActive && DateTimeOffset.UtcNow < _lessonExpiresAt;
    public bool Recording { get; private set; }
    public DeviceEnrollment Enrollment => _pairing.Enrollment;
    public bool EndedByStudent { get; private set; }

    /// <summary>Raised on a background thread with the latest JPEG frame.</summary>
    public event Action<AgentConnection, byte[]>? FrameReceived;
    public event Action<AgentConnection>? Disconnected;

    /// <summary>Raised on a background thread when a collected file arrives from this agent.</summary>
    public event Action<AgentConnection, FileDataMsg>? FileCollected;

    /// <summary>Raised on a background thread when a chat line arrives from this agent.</summary>
    public event Action<AgentConnection, ChatMsg>? ChatReceived;

    /// <summary>Raised on a background thread when a student answers a question.</summary>
    public event Action<AgentConnection, AnswerMsg>? AnswerReceived;

    /// <summary>Raised when the agent reports its monitor layout.</summary>
    public event Action<AgentConnection>? MonitorsReceived;

    /// <summary>Raised (background thread) when the agent reports its foreground activity.</summary>
    public event Action<AgentConnection, ActivityMsg>? ActivityReceived;

    /// <summary>Raised (background thread) when the lesson or its visible state changes.</summary>
    public event Action<AgentConnection>? StatusChanged;

    /// <summary>Raised (background thread) with a message explaining a refused operation.</summary>
    public event Action<AgentConnection, string>? CommandFeedback;

    public AgentConnection(string host, string machineName, PairedDevice pairing, X509Certificate2 identity, SchoolSettings settings)
    {
        settings.EnsureValid();
        Host = host;
        MachineName = machineName;
        _pairing = pairing;
        _identity = identity;
        _settings = settings.Clone();
    }

    public bool HasPermission(ClassroomPermission permission) =>
        LessonActive && permission != ClassroomPermission.None
        && (_pairing.Enrollment.Teacher.Permissions & permission) == permission;

    public async Task ConnectAsync()
    {
        var ct = _cts.Token;
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(_settings.Network.ConnectTimeoutSeconds));
            _client = new TcpClient { NoDelay = true };
            await _client.ConnectAsync(Host, _pairing.Port, connect.Token);
        }
        var timeout = TimeSpan.FromSeconds(_settings.Network.HandshakeTimeoutSeconds);
        var tls = await SecureChannel.ConnectAsync(_client, _identity, _pairing.Enrollment.DeviceCertificateSha256, timeout, ct);
        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            handshake.CancelAfter(timeout);
            await SecureChannel.AuthenticateTeacherAsync(tls, _pairing.Enrollment.PairingSecret, handshake.Token);
        }
        _ns = tls;
        _ = Task.Run(() => ReadLoopAsync(ct));
        _ = Task.Run(() => HealthLoopAsync(ct));
    }

    /// <summary>Starts a bounded lesson. Returns false, with feedback, when the agent refuses it.</summary>
    public async Task<bool> StartLessonAsync(DateTimeOffset expiresAt)
    {
        if (_ns is null || _disposed) return false;
        var enrollment = _pairing.Enrollment;
        if (expiresAt > enrollment.ExpiresAtUtc) expiresAt = enrollment.ExpiresAtUtc;
        var lesson = new LessonRequest(Guid.NewGuid().ToString("N"), enrollment.SchoolId, enrollment.ClassroomId, expiresAt);
        var result = await RequestAsync(lesson.SessionId, () => WriteAsync(MsgType.LessonStart, Frame.ToJson(lesson)));
        if (!result.Ok) { Feedback(result.Message); return false; }
        _lessonExpiresAt = expiresAt;
        _lessonActive = true;
        StatusChanged?.Invoke(this);
        return true;
    }

    /// <summary>The agent ends the session, restores owned restrictions and closes the connection.</summary>
    public async Task EndLessonAsync()
    {
        _lessonActive = false;
        Recording = false;
        StatusChanged?.Invoke(this);
        try { await WriteAsync(MsgType.LessonEnd, Array.Empty<byte>()); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException) { }
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

                // An enrolled device is still a student's computer: outside a lesson only its identity and status are accepted.
                if (!LessonActive && type is MsgType.ScreenFrame or MsgType.Activity or MsgType.FileData or MsgType.Chat or MsgType.Answer) continue;

                switch (type)
                {
                    case MsgType.Hello:
                        var h = Frame.ReadJson<HelloInfo>(payload);
                        MachineName = Label(h.MachineName, MachineName);
                        UserName = Label(h.UserName, "");
                        break;

                    case MsgType.SessionEnded:
                        EndedByStudent = Frame.ReadJson<SessionEndNotice>(payload).EndedByStudent;
                        break;

                    case MsgType.ScreenFrame:
                        LastFrame = payload;
                        LastFrameAt = DateTime.UtcNow;
                        FramesReceived++;
                        if (payload.Length > 0) _profile.ObserveFrame(payload.Length);
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
                        // Only an answer to a request this console made; a device cannot push files here.
                        if (Interlocked.Decrement(ref _pendingFiles) < 0) { Interlocked.Exchange(ref _pendingFiles, 0); break; }
                        var file = Frame.ReadJson<FileDataMsg>(payload);
                        FileCollected?.Invoke(this, file.Ok && (file.DataBase64 ?? "").Length > 4 * ((_settings.Files.MaximumTransferBytes + 2) / 3)
                            ? new FileDataMsg("", "", false, "The file exceeds the configured transfer limit.") : file);
                        break;

                    case MsgType.Chat:
                        ChatReceived?.Invoke(this, Frame.ReadJson<ChatMsg>(payload));
                        break;

                    case MsgType.Answer:
                        AnswerReceived?.Invoke(this, Frame.ReadJson<AnswerMsg>(payload));
                        break;

                    case MsgType.SessionHealth:
                        _lastHealth = DateTime.UtcNow;
                        break;

                    case MsgType.StateResult:
                        var result = Frame.ReadJson<StateResult>(payload);
                        if (_pending.TryRemove(result.RequestId, out var waiter)) waiter.TrySetResult(result);
                        else if (!result.Ok) Feedback(result.Message);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                LastReadError = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally { OnDisconnected(); }
    }

    /// <summary>Keeps the agent's peer watchdog satisfied and detects a silent agent.</summary>
    private async Task HealthLoopAsync(CancellationToken ct)
    {
        // Fixed and shorter than the smallest timeout an agent can be configured with.
        var interval = TimeSpan.FromSeconds(2);
        var limit = TimeSpan.FromSeconds(_settings.Network.HealthTimeoutSeconds);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct);
                if (DateTime.UtcNow - _lastHealth > limit)
                {
                    LastReadError = "Device stopped responding.";
                    _client?.Close();
                    return;
                }
                await WriteAsync(MsgType.HealthPing, Array.Empty<byte>());
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException or AuthenticationException) { }
    }

    private void OnDisconnected()
    {
        if (Interlocked.Exchange(ref _disconnected, 1) != 0) return;
        _lessonActive = false;
        Recording = false;
        foreach (var waiter in _pending.Values)
            waiter.TrySetResult(new StateResult("", false, "The device connection closed."));
        _pending.Clear();
        Disconnected?.Invoke(this);
    }

    public Task RequestScreenAsync(int maxWidth, int quality, int intervalMs, int monitorIndex = -1) =>
        HasPermission(ClassroomPermission.View)
            ? SendAsync(MsgType.ScreenRequest, new ScreenRequest(maxWidth, quality, intervalMs, monitorIndex))
            : Task.CompletedTask;

    /// <summary>Requests the thumbnail stream sized to the observed frames and this console's byte budget.</summary>
    public Task RequestAdaptiveStreamAsync(int activeDevices)
    {
        var r = _profile.CreateRequest(_settings.Display, Math.Clamp(activeDevices, 1, 500));
        return RequestScreenAsync(r.MaxWidth, r.Quality, r.IntervalMs, r.MonitorIndex);
    }

    public Task SendMessageAsync(string text, int durationMs) =>
        SendAsync(MsgType.ShowMessage, new MessageCmd(text, durationMs));

    public Task LockAsync(bool locked, string text) =>
        SendAsync(MsgType.Lock, new LockCmd(locked, text));

    public Task<bool> SetAssistanceAsync(bool active)
    {
        string id = Guid.NewGuid().ToString("N");
        return SendAwaitedAsync(id, MsgType.AssistanceState, new StateRequest(id, active));
    }

    /// <summary>The agent shows the recording state; a recorder must not start unless this returns true.</summary>
    public async Task<bool> SetRecordingAsync(bool active)
    {
        string id = Guid.NewGuid().ToString("N");
        bool ok = await SendAwaitedAsync(id, MsgType.RecordingState, new StateRequest(id, active));
        if (ok) { Recording = active; StatusChanged?.Invoke(this); }
        return ok;
    }

    public Task RemoteInputAsync(RemoteInputCmd cmd) =>
        HasPermission(ClassroomPermission.Assist) ? WriteQuietlyAsync(MsgType.RemoteInput, Frame.ToJson(cmd)) : Task.CompletedTask;

    public Task PowerAsync(string action) =>
        SendAsync(MsgType.Power, new PowerCmd(action));

    public Task RunAsync(string kind, string target) =>
        SendAsync(MsgType.Run, new RunCmd(kind, target));

    public Task SendFileAsync(string fileName, byte[] bytes) =>
        SendAsync(MsgType.FileSend, new FilePush(fileName, Convert.ToBase64String(bytes)));

    public Task RequestFileAsync(string path)
    {
        if (HasPermission(ClassroomPermission.Files)) Interlocked.Increment(ref _pendingFiles);
        return SendAsync(MsgType.FileRequest, new FileRequestCmd(path));
    }

    /// <summary>The student sees that this screen is shown to the class before any frame is forwarded.</summary>
    public Task<bool> SetSharedAsync(bool active)
    {
        string id = Guid.NewGuid().ToString("N");
        return SendAwaitedAsync(id, MsgType.StudentShared, new StateRequest(id, active));
    }

    public Task SendChatAsync(string text) =>
        SendAsync(MsgType.Chat, new ChatMsg("Teacher", text));

    public Task AskQuestionAsync(string text) =>
        SendAsync(MsgType.Question, new QuestionCmd(Guid.NewGuid().ToString("N"), text));

    public Task RestrictAsync(string kind, bool enabled, string[] items, string? mode = null) =>
        SendAsync(MsgType.Restrict, new RestrictCmd(kind, enabled, items, mode));

    public Task SetBroadcastAsync(bool active) =>
        HasPermission(ClassroomPermission.Present) ? SendAsync(MsgType.TeacherBroadcast, new BroadcastCmd(active)) : Task.CompletedTask;

    public Task SendTeacherFrameAsync(byte[] jpeg) =>
        HasPermission(ClassroomPermission.Present) ? WriteQuietlyAsync(MsgType.TeacherFrame, jpeg) : Task.CompletedTask;

    private async Task SendAsync<T>(MsgType type, T value)
    {
        byte[] payload = Frame.ToJson(value);
        if (!Allowed(type, payload)) return;
        string id = Guid.NewGuid().ToString("N");
        await WriteQuietlyAsync(MsgType.ManagedCommand, Frame.ToJson(new ManagedCommand(id, type, payload)));
    }

    private async Task<bool> SendAwaitedAsync<T>(string id, MsgType type, T value)
    {
        byte[] payload = Frame.ToJson(value);
        if (!Allowed(type, payload)) return false;
        var result = await RequestAsync(id, () => WriteAsync(MsgType.ManagedCommand, Frame.ToJson(new ManagedCommand(id, type, payload))));
        if (!result.Ok) Feedback(result.Message);
        return result.Ok;
    }

    private bool Allowed(MsgType type, byte[] payload)
    {
        if (HasPermission(CommandPolicy.Required(type, payload))) return true;
        Feedback("Operation is not authorized by classroom/session policy.");
        return false;
    }

    private async Task<StateResult> RequestAsync(string id, Func<Task> write)
    {
        var waiter = new TaskCompletionSource<StateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;
        try
        {
            await write();
            return await waiter.Task.WaitAsync(ResultTimeout, _cts.Token);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException)
        {
            return new StateResult(id, false, "The device did not confirm the operation.");
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task WriteQuietlyAsync(MsgType type, byte[] payload)
    {
        try { await WriteAsync(type, payload); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException)
        { /* The read loop reports the disconnect. */ }
    }

    private async Task WriteAsync(MsgType type, byte[] payload)
    {
        if (_ns is null || _disposed) throw new ObjectDisposedException(nameof(AgentConnection));
        await _writeLock.WaitAsync(_cts.Token);
        try { await Frame.WriteAsync(_ns, type, payload, _cts.Token); }
        finally { _writeLock.Release(); }
    }

    /// <summary>Records why a connection attempt failed, for the device tile.</summary>
    public void Fail(string message) => LastReadError = message;

    private static string Label(string? value, string fallback)
    {
        string text = new((value ?? "").Where(c => !char.IsControl(c)).Take(128).ToArray());
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }

    private void Feedback(string message) => CommandFeedback?.Invoke(this, $"{MachineName}: {message}");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _ns?.Dispose();
        _client?.Dispose();
    }
}
