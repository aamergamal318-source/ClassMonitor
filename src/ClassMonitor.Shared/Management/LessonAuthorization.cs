using System.Security.Authentication;
using System.Security.Cryptography;

namespace ClassMonitor.Shared.Management;

/// <summary>Per-connection authoritative permission and bounded lesson state. Reconnection requires a fresh lesson ID.</summary>
public sealed class LessonAuthorization
{
    private readonly DeviceEnrollment _enrollment;
    private readonly TeacherGrant _grant;
    private readonly TimeProvider _clock;
    private readonly HashSet<string> _usedSessions = new(StringComparer.Ordinal);
    private readonly LessonReplayCache _replayCache;
    private readonly object _gate = new();
    private string? _sessionId;
    private DateTimeOffset _expires;
    private long _started;
    private TimeSpan _duration;

    public LessonAuthorization(DeviceEnrollment enrollment, string peerFingerprint, TimeProvider? clock = null,
        LessonReplayCache? replayCache = null)
    {
        EnrollmentManager.ValidateEnrollment(enrollment);
        if (!CertificateTrust.IsFingerprint(peerFingerprint) || !CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(peerFingerprint), Convert.FromHexString(enrollment.Teacher.CertificateSha256)))
            throw new AuthenticationException("peer.certificate.mismatch");
        _grant = enrollment.Teacher with { ClassroomIds = (string[])enrollment.Teacher.ClassroomIds.Clone() };
        _enrollment = enrollment with { Teacher = _grant };
        _clock = clock ?? TimeProvider.System;
        _replayCache = replayCache ?? LessonReplayCache.ProcessCache;
    }

    public TeacherGrant Grant => _grant with { ClassroomIds = (string[])_grant.ClassroomIds.Clone() };
    public string? SessionId { get { lock (_gate) return _sessionId; } }
    public DateTimeOffset ExpiresAtUtc { get { lock (_gate) return _expires; } }
    public bool IsActive { get { lock (_gate) return Active(); } }

    public void Start(LessonRequest request)
    {
        lock (_gate)
        {
            if (request is null || !Guid.TryParseExact(request.SessionId, "N", out _))
                throw new UnauthorizedAccessException("lesson.id.invalid");
            if (request.SchoolId != _enrollment.SchoolId || request.ClassroomId != _enrollment.ClassroomId
                || request.SchoolId != _grant.SchoolId || !_grant.ClassroomIds.Contains(request.ClassroomId, StringComparer.Ordinal))
                throw new UnauthorizedAccessException("lesson.scope.denied");
            var now = _clock.GetUtcNow();
            if (_enrollment.ExpiresAtUtc <= now || _grant.ExpiresAtUtc <= now)
                throw new UnauthorizedAccessException("enrollment.expired");
            var expiry = request.ExpiresAtUtc.ToUniversalTime();
            if (expiry <= now || expiry > now.AddHours(8) || expiry > _enrollment.ExpiresAtUtc || expiry > _grant.ExpiresAtUtc)
                throw new UnauthorizedAccessException("lesson.expiration.invalid");
            // Starting another lesson must not silently extend an active lesson or its restrictions.
            if (Active()) throw new UnauthorizedAccessException("lesson.already.active");
            if (_usedSessions.Count >= 128 || _usedSessions.Contains(request.SessionId)
                || !_replayCache.TryUse(_enrollment.DeviceId + ":" + _grant.TeacherId + ":" + request.SessionId, expiry, now))
                throw new UnauthorizedAccessException("lesson.replay.denied");
            _usedSessions.Add(request.SessionId);
            _sessionId = request.SessionId;
            _expires = expiry;
            _started = _clock.GetTimestamp();
            _duration = expiry - now;
        }
    }

    public void Demand(ClassroomPermission permission)
    {
        lock (_gate)
        {
            if (!Active()) throw new UnauthorizedAccessException("lesson.inactive.or.expired");
            if (permission == ClassroomPermission.None || (permission & ~ClassroomPermission.All) != 0
                || (_grant.Permissions & permission) != permission
                || (permission.HasFlag(ClassroomPermission.Admin) && _grant.Role != TeacherRole.SchoolAdmin))
                throw new UnauthorizedAccessException("operation.permission.denied");
        }
    }

    public void End()
    {
        lock (_gate) { _sessionId = null; _expires = DateTimeOffset.MinValue; _duration = TimeSpan.Zero; }
    }

    private bool Active()
    {
        var now = _clock.GetUtcNow();
        return _sessionId is not null && _expires > now && _enrollment.ExpiresAtUtc > now && _grant.ExpiresAtUtc > now
            && _clock.GetElapsedTime(_started, _clock.GetTimestamp()) < _duration;
    }
}

/// <summary>Bounded process-wide replay memory shared across reconnects, not a durable school revocation database.</summary>
public sealed class LessonReplayCache
{
    internal static readonly LessonReplayCache ProcessCache = new();
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly int _maximum;
    public LessonReplayCache(int maximum = 4096)
    {
        if (maximum is < 1 or > 65_536) throw new ArgumentOutOfRangeException(nameof(maximum));
        _maximum = maximum;
    }
    internal bool TryUse(string id, DateTimeOffset expires, DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var expired in _seen.Where(p => p.Value <= now).Select(p => p.Key).ToArray()) _seen.Remove(expired);
            if (_seen.Count >= _maximum || _seen.ContainsKey(id)) return false;
            _seen.Add(id, expires);
            return true;
        }
    }
}
