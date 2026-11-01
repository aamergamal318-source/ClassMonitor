using System.Buffers.Binary;
using System.Text.Json;

namespace ClassMonitor.Shared;

/// <summary>
/// Network constants shared by the teacher console and the student agent.
/// Everything runs on a trusted LAN that the operator administers.
/// </summary>
public static class Net
{
    /// <summary>TCP port the agent listens on; the teacher connects out to it.</summary>
    public const int AgentTcpPort = 5900;

    /// <summary>UDP port the agent broadcasts its presence on so the teacher can auto-discover it.</summary>
    public const int DiscoveryPort = 5901;

    /// <summary>Magic prefix for UDP discovery datagrams so we ignore unrelated traffic.</summary>
    public const string DiscoveryMagic = "CLASSMON1";

    /// <summary>Hard ceiling on a single framed message (16 MB) to avoid runaway allocations.</summary>
    public const int MaxMessageBytes = 16 * 1024 * 1024;
}

/// <summary>Type tag carried in the one-byte header of every framed message.</summary>
public enum MsgType : byte
{
    Hello = 1,          // agent -> teacher : HelloInfo (identifies the machine/user)
    ScreenFrame = 2,    // agent -> teacher : raw JPEG bytes of the captured screen
    ScreenRequest = 3,  // teacher -> agent : ScreenRequest (start/adjust frame stream)
    ShowMessage = 16,   // teacher -> agent : MessageCmd (visible notice to the student)
    Lock = 17,          // teacher -> agent : LockCmd (lock/unlock the screen with a note)
    RemoteInput = 32,   // teacher -> agent : RemoteInputCmd (mouse/keyboard injection)
    Power = 33,         // teacher -> agent : PowerCmd (shut down / restart / log off)
    Run = 34,           // teacher -> agent : RunCmd (launch a program or open a website)
    FileSend = 35,      // teacher -> agent : FilePush (a file pushed to the student)
    FileRequest = 36,   // teacher -> agent : FileRequest (collect a file from the student)
    FileData = 37,      // agent -> teacher : FileData (the collected file, or an error)
    Chat = 38,          // both ways        : ChatMsg (two-way text chat)
    Restrict = 40,      // teacher -> agent  : RestrictCmd (block apps / sites / internet)

    // --- advanced monitoring ---
    MonitorsInfo = 60,  // agent -> teacher : MonitorsMsg (the student's display layout)
    Activity = 61,      // agent -> teacher : ActivityMsg (foreground window/app, periodic)
    LessonStart = 64,
    LessonEnd = 65,
    AssistanceState = 66,
    RecordingState = 67,
    StateResult = 68,
    HealthPing = 69,
    SessionHealth = 70,
    Question = 71,
    Answer = 72,

    // --- authentication handshake (sent over TLS, before any command) ---
    AuthHello = 100,    // agent  -> teacher : AuthHello (server nonce)
    AuthRequest = 101,  // teacher -> agent  : AuthRequest (client nonce + client proof)
    AuthResult = 102,   // agent  -> teacher : AuthResult (ok + server proof)
    TeacherFrame = 48,  // teacher -> agent : raw JPEG bytes of the teacher's screen (broadcast)
    TeacherBroadcast = 49, // teacher -> agent : BroadcastCmd (show/hide the broadcast overlay)
}

public sealed record HelloInfo(string MachineName, string UserName);

/// <summary>
/// Teacher asks the agent to stream screenshots at the given cadence/size.
/// <paramref name="MonitorIndex"/> is -1 for the whole desktop, or a 0-based monitor index.
/// </summary>
public sealed record ScreenRequest(int MaxWidth, int Quality, int IntervalMs, int MonitorIndex = -1);

public sealed record MessageCmd(string Text, int DurationMs);

public sealed record LockCmd(bool Locked, string Text);

/// <summary>
/// One remote-input event. Mouse coordinates are normalized 0..1 relative to the
/// agent's full (virtual) desktop so they survive any resolution mismatch.
/// </summary>
public sealed record RemoteInputCmd(
    string Kind,     // mousemove | mousedown | mouseup | scroll | keydown | keyup
    double X = 0,    // normalized X (0..1) for mouse events
    double Y = 0,    // normalized Y (0..1) for mouse events
    int Button = 0,  // 0 left, 1 right, 2 middle
    int Delta = 0,   // wheel delta for scroll
    int VKey = 0);   // Win32 virtual-key code for keyboard events

public sealed record BroadcastCmd(bool Active);

/// <summary>Power action for the student machine. Action: "shutdown" | "restart" | "logoff".</summary>
public sealed record PowerCmd(string Action);

/// <summary>Launch something on the student machine. Kind: "program" | "website".</summary>
public sealed record RunCmd(string Kind, string Target);

/// <summary>A file pushed from the teacher to the student (bytes are Base64-encoded).</summary>
public sealed record FilePush(string FileName, string DataBase64);

/// <summary>Teacher asks the student to send back the file at <paramref name="Path"/>.</summary>
public sealed record FileRequestCmd(string Path);

/// <summary>A collected file returned by the student (or an error if it could not be read).</summary>
public sealed record FileDataMsg(string FileName, string DataBase64, bool Ok, string Error);

/// <summary>A chat line. <paramref name="From"/> is a display name ("Teacher" or the machine name).</summary>
public sealed record ChatMsg(string From, string Text);

/// <summary>
/// A restriction command. Kind: "apps" (block process names) | "sites" (block domains) |
/// "internet" (block all web). Enabled toggles it; Items is the list for apps/sites.
/// </summary>
public sealed record RestrictCmd(string Kind, bool Enabled, string[] Items, string? Mode = null);

// --- advanced monitoring payloads ---
public sealed record MonitorInfo(int Index, int X, int Y, int Width, int Height, bool Primary);
public sealed record MonitorsMsg(MonitorInfo[] Monitors);
public sealed record ActivityMsg(string WindowTitle, string ProcessName);

// --- authentication handshake payloads (mutual challenge-response over TLS) ---
public sealed record AuthHello(string NonceS);
public sealed record AuthRequest(string NonceT, string ProofT);
public sealed record AuthResult(bool Ok, string ProofS);

/// <summary>
/// Length-prefixed message framing over a stream:
/// [4-byte big-endian length][1-byte MsgType][payload...], where length = 1 + payload length.
/// Writes must be serialized per-connection by the caller (e.g. a SemaphoreSlim).
/// </summary>
public static class Frame
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync(Stream s, MsgType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (payload.Length + 1 > Net.MaxMessageBytes)
            throw new InvalidOperationException($"Message too large: {payload.Length} bytes");

        var header = new byte[5];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length + 1);
        header[4] = (byte)type;
        await s.WriteAsync(header, ct).ConfigureAwait(false);
        if (!payload.IsEmpty)
            await s.WriteAsync(payload, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    public static Task WriteJsonAsync<T>(Stream s, MsgType type, T value, CancellationToken ct = default)
        => WriteAsync(s, type, JsonSerializer.SerializeToUtf8Bytes(value, JsonOpts), ct);

    /// <summary>Reads one full message. Returns null on a clean end-of-stream.</summary>
    public static async Task<(MsgType Type, byte[] Payload)?> ReadAsync(Stream s, CancellationToken ct = default, int maximumBytes = Net.MaxMessageBytes)
    {
        if (maximumBytes is < 1 or > Net.MaxMessageBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var header = new byte[5];
        if (!await ReadExactAsync(s, header, ct).ConfigureAwait(false))
            return null;

        int len = BinaryPrimitives.ReadInt32BigEndian(header);
        if (len < 1 || len > maximumBytes)
            throw new InvalidDataException($"Bad frame length: {len}");

        var type = (MsgType)header[4];
        int payloadLen = len - 1;
        var payload = payloadLen == 0 ? Array.Empty<byte>() : new byte[payloadLen];
        if (payloadLen > 0 && !await ReadExactAsync(s, payload, ct).ConfigureAwait(false))
            throw new EndOfStreamException("Truncated message payload");

        return (type, payload);
    }

    public static T ReadJson<T>(byte[] payload) =>
        JsonSerializer.Deserialize<T>(payload, JsonOpts)
        ?? throw new InvalidDataException($"Null {typeof(T).Name} payload");

    private static async Task<bool> ReadExactAsync(Stream s, Memory<byte> buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await s.ReadAsync(buffer[read..], ct).ConfigureAwait(false);
            if (n == 0)
                return read == 0 ? false : throw new EndOfStreamException();
            read += n;
        }
        return true;
    }
}
