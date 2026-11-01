using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

namespace ClassMonitor.Shared.Management;

public enum AuditEvent
{
    ConnectionAccepted, ConnectionDenied, EnrollmentImported, LessonStarted, LessonEnded,
    OperationAllowed, OperationDenied, RecordingStarted, RecordingStopped
}
public enum AuditReason
{
    None, EnrollmentRequired, CertificateMismatch, SchoolMismatch, ClassroomMismatch,
    PermissionDenied, LessonExpired, Replay, RateLimited, MalformedMessage, SessionEnded, InternalError
}
public sealed record AuditEntry(DateTimeOffset TimestampUtc, AuditEvent Event, string Actor,
    string Device, string School, string Classroom, ClassroomPermission Permission, bool Allowed, AuditReason Reason);

/// <summary>Content-free, bounded audit log. No API accepts free-form messages or operation payloads.</summary>
public sealed class AuditWriter
{
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _retentionDays;
    private readonly TimeProvider _clock;
    private static readonly ConcurrentDictionary<string, object> FileGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate;

    public AuditWriter(string path, long maxBytes = 2 * 1024 * 1024, int retentionDays = 30, TimeProvider? clock = null)
    {
        if (maxBytes is < 4096 or > 64 * 1024 * 1024 || retentionDays is < 1 or > 365)
            throw new ArgumentOutOfRangeException();
        _path = Path.GetFullPath(path); _maxBytes = maxBytes; _retentionDays = retentionDays; _clock = clock ?? TimeProvider.System;
        _gate = FileGates.GetOrAdd(_path, _ => new object());
    }

    public void Record(AuditEvent action, string actorId, string deviceId, string schoolId,
        string classroomId, ClassroomPermission permission, bool allowed, AuditReason reason = AuditReason.None)
    {
        if (!Enum.IsDefined(action) || !Enum.IsDefined(reason) || (permission & ~ClassroomPermission.All) != 0)
            throw new ArgumentOutOfRangeException();
        var entry = new AuditEntry(_clock.GetUtcNow(), action, Pseudonymize(actorId), Pseudonymize(deviceId),
            Pseudonymize(schoolId), Pseudonymize(classroomId), permission, allowed, reason);
        string line = JsonSerializer.Serialize(entry) + Environment.NewLine;
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            DeleteExpired(_path); DeleteExpired(_path + ".1");
            if (File.Exists(_path) && new FileInfo(_path).Length + Encoding.UTF8.GetByteCount(line) > _maxBytes)
                File.Move(_path, _path + ".1", overwrite: true);
            File.AppendAllText(_path, line, new UTF8Encoding(false));
        }
    }

    public static string Pseudonymize(string? value)
    {
        if (value is null || value.Length > 512) throw new ArgumentOutOfRangeException(nameof(value));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];
    }

    private void DeleteExpired(string file)
    {
        if (File.Exists(file) && File.GetLastWriteTimeUtc(file) < _clock.GetUtcNow().UtcDateTime.AddDays(-_retentionDays)) File.Delete(file);
    }
}
