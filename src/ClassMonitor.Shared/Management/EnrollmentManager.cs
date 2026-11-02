using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassMonitor.Shared.Management;

public static class EnrollmentManager
{
    public const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    public static DeviceEnrollmentRequest CreateRequest(X509Certificate2 identity, string displayName)
    {
        ValidateLabel(displayName);
        if (!CertificateTrust.Matches(identity, CertificateTrust.Fingerprint(identity)) || !CertificateTrust.SupportsMutualTls(identity))
            throw new InvalidDataException("identity.invalid");
        string pin = CertificateTrust.Fingerprint(identity);
        return new DeviceEnrollmentRequest(DeviceId(pin), displayName, Convert.ToBase64String(identity.RawData), pin);
    }

    public static EnrollmentPackage CreatePackage(DeviceEnrollmentRequest request, X509Certificate2 teacherIdentity,
        string schoolId, string classroomId, string teacherDisplayName, TeacherRole role,
        ClassroomPermission permissions, DateTimeOffset expiresAt)
    {
        ValidateRequest(request);
        ValidateIdentifier(schoolId); ValidateIdentifier(classroomId); ValidateLabel(teacherDisplayName);
        string teacherPin = CertificateTrust.Fingerprint(teacherIdentity);
        if (!teacherIdentity.HasPrivateKey || !CertificateTrust.Matches(teacherIdentity, teacherPin)
            || !CertificateTrust.SupportsMutualTls(teacherIdentity)) throw new InvalidDataException("teacher.identity.invalid");
        var now = DateTimeOffset.UtcNow;
        var expiry = expiresAt.ToUniversalTime();
        using var deviceCert = CertificateTrust.ReadPublicCertificate(request.PublicCertificateBase64);
        if (expiry <= now || expiry > teacherIdentity.NotAfter.ToUniversalTime() || expiry > deviceCert.NotAfter.ToUniversalTime())
            throw new InvalidDataException("enrollment.expiration.invalid");
        var grant = new TeacherGrant(teacherPin, teacherPin, teacherDisplayName, role, schoolId,
            new[] { classroomId }, permissions, expiry);
        ValidateGrant(grant);
        var payload = new EnrollmentPayload(SchemaVersion, request, schoolId, classroomId, grant,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), now, expiry);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        using var rsa = teacherIdentity.GetRSAPrivateKey() ?? throw new InvalidDataException("teacher.rsa.required");
        return new EnrollmentPackage(Convert.ToBase64String(bytes),
            Convert.ToBase64String(rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)),
            Convert.ToBase64String(teacherIdentity.RawData));
    }

    public static DeviceEnrollment ValidatePackage(EnrollmentPackage package,
        string expectedDeviceFingerprint, string expectedTeacherFingerprint)
    {
        if (!CertificateTrust.IsFingerprint(expectedDeviceFingerprint) || !CertificateTrust.IsFingerprint(expectedTeacherFingerprint))
            throw new AuthenticationException("enrollment.fingerprint.required");
        if (package is null || package.SignatureAlgorithm != "RSA-PSS-SHA256" || package.PayloadBase64 is not { Length: > 0 and <= 65_536 }
            || package.SignatureBase64 is not { Length: > 0 and <= 2048 }) throw new InvalidDataException("enrollment.package.invalid");
        using var teacher = CertificateTrust.ReadPublicCertificate(package.SigningCertificateBase64);
        if (!CertificateTrust.Matches(teacher, expectedTeacherFingerprint) || !CertificateTrust.SupportsMutualTls(teacher))
            throw new AuthenticationException("enrollment.teacher.fingerprint.mismatch");
        byte[] bytes, signature;
        try { bytes = Convert.FromBase64String(package.PayloadBase64); signature = Convert.FromBase64String(package.SignatureBase64); }
        catch (FormatException ex) { throw new InvalidDataException("enrollment.encoding.invalid", ex); }
        using var rsa = teacher.GetRSAPublicKey() ?? throw new InvalidDataException("teacher.rsa.required");
        if (!rsa.VerifyData(bytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new AuthenticationException("enrollment.signature.invalid");
        EnrollmentPayload payload;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            RejectDuplicateProperties(document.RootElement);
            payload = JsonSerializer.Deserialize<EnrollmentPayload>(bytes, Json) ?? throw new InvalidDataException("enrollment.payload.missing");
        }
        catch (JsonException ex) { throw new InvalidDataException("enrollment.json.invalid", ex); }
        if (payload.SchemaVersion != SchemaVersion || payload.Device is null || payload.Teacher is null)
            throw new InvalidDataException("enrollment.schema.invalid");
        ValidateRequest(payload.Device); ValidateGrant(payload.Teacher);
        using var deviceIdentity = CertificateTrust.ReadPublicCertificate(payload.Device.PublicCertificateBase64);
        ValidateIdentifier(payload.SchoolId); ValidateIdentifier(payload.ClassroomId);
        var now = DateTimeOffset.UtcNow;
        if (payload.IssuedAtUtc > now.AddMinutes(5) || payload.ExpiresAtUtc <= now
            || payload.ExpiresAtUtc <= payload.IssuedAtUtc || payload.ExpiresAtUtc > payload.Teacher.ExpiresAtUtc
            || payload.ExpiresAtUtc > teacher.NotAfter.ToUniversalTime()
            || payload.ExpiresAtUtc > deviceIdentity.NotAfter.ToUniversalTime()) throw new AuthenticationException("enrollment.expired");
        if (!PinsEqual(payload.Device.CertificateSha256, expectedDeviceFingerprint)
            || !PinsEqual(payload.Teacher.CertificateSha256, expectedTeacherFingerprint)
            || payload.Teacher.SchoolId != payload.SchoolId || !payload.Teacher.ClassroomIds.Contains(payload.ClassroomId, StringComparer.Ordinal))
            throw new AuthenticationException("enrollment.scope.mismatch");
        if (!IsStrongPairingSecret(payload.PairingSecret)) throw new InvalidDataException("enrollment.secret.invalid");
        return new DeviceEnrollment(payload.Device.DeviceId, payload.SchoolId, payload.ClassroomId,
            payload.Device.CertificateSha256, payload.PairingSecret,
            payload.Teacher with { ClassroomIds = (string[])payload.Teacher.ClassroomIds.Clone() }, payload.ExpiresAtUtc);
    }

    public static void ValidateEnrollment(DeviceEnrollment enrollment)
    {
        ValidateEnrollmentStructure(enrollment);
        if (enrollment.ExpiresAtUtc <= DateTimeOffset.UtcNow || enrollment.Teacher.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            throw new AuthenticationException("enrollment.expired");
    }

    /// <summary>A saved record stays readable after it expires, so the console can start and ask for re-enrollment.</summary>
    public static void ValidateEnrollmentStructure(DeviceEnrollment enrollment)
    {
        if (enrollment is null || enrollment.Teacher is null) throw new AuthenticationException("enrollment.required");
        ValidateIdentifier(enrollment.SchoolId); ValidateIdentifier(enrollment.ClassroomId);
        ValidateGrant(enrollment.Teacher);
        if (!CertificateTrust.IsFingerprint(enrollment.DeviceCertificateSha256) || enrollment.DeviceId != DeviceId(enrollment.DeviceCertificateSha256)
            || !IsStrongPairingSecret(enrollment.PairingSecret)) throw new InvalidDataException("enrollment.invalid");
        if (enrollment.Teacher.SchoolId != enrollment.SchoolId || !enrollment.Teacher.ClassroomIds.Contains(enrollment.ClassroomId, StringComparer.Ordinal))
            throw new AuthenticationException("enrollment.scope.mismatch");
        if (enrollment.ExpiresAtUtc > enrollment.Teacher.ExpiresAtUtc) throw new InvalidDataException("enrollment.expiration.invalid");
    }

    public static bool IsStrongPairingSecret(string? value)
    {
        if (value is not { Length: 44 }) return false;
        try { byte[] bytes = Convert.FromBase64String(value); return bytes.Length == 32 && bytes.Any(b => b != bytes[0]); }
        catch (FormatException) { return false; }
    }

    private static void ValidateRequest(DeviceEnrollmentRequest request)
    {
        if (request is null) throw new InvalidDataException("enrollment.request.required");
        ValidateLabel(request.DisplayName);
        using var certificate = CertificateTrust.ReadPublicCertificate(request.PublicCertificateBase64);
        if (!CertificateTrust.Matches(certificate, request.CertificateSha256) || !CertificateTrust.SupportsMutualTls(certificate)
            || request.DeviceId != DeviceId(request.CertificateSha256)) throw new AuthenticationException("enrollment.device.fingerprint.mismatch");
    }

    private static void ValidateGrant(TeacherGrant grant)
    {
        ValidateIdentifier(grant.SchoolId); ValidateLabel(grant.DisplayName);
        if (grant.Role is not (TeacherRole.Teacher or TeacherRole.SchoolAdmin) || !CertificateTrust.IsFingerprint(grant.CertificateSha256)
            || !PinsEqual(grant.TeacherId, grant.CertificateSha256) || grant.ClassroomIds is not { Length: > 0 and <= 128 }
            || grant.ClassroomIds.Distinct(StringComparer.Ordinal).Count() != grant.ClassroomIds.Length
            || grant.Permissions == ClassroomPermission.None || (grant.Permissions & ~ClassroomPermission.All) != 0
            || (grant.Role == TeacherRole.Teacher && grant.Permissions.HasFlag(ClassroomPermission.Admin)))
            throw new InvalidDataException("teacher.grant.invalid");
        foreach (string classroom in grant.ClassroomIds) ValidateIdentifier(classroom);
    }

    private static string DeviceId(string pin) => "device-" + pin.ToLowerInvariant();
    private static bool PinsEqual(string? a, string? b) => CertificateTrust.IsFingerprint(a) && CertificateTrust.IsFingerprint(b)
        && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(a!), Convert.FromHexString(b!));
    private static void ValidateIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value != value.Trim() || value.Any(char.IsControl))
            throw new InvalidDataException("identifier.invalid");
    }
    private static void ValidateLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || value.Any(char.IsControl))
            throw new InvalidDataException("label.invalid");
    }
    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new InvalidDataException("enrollment.duplicate.property");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }
}
