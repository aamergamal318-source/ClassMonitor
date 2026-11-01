namespace ClassMonitor.Shared.Management;

[Flags]
public enum ClassroomPermission
{
    None = 0, View = 1, Assist = 2, Present = 4, Message = 8,
    Files = 16, Focus = 32, Record = 64, Admin = 128,
    All = View | Assist | Present | Message | Files | Focus | Record | Admin
}

public enum TeacherRole { Teacher = 1, SchoolAdmin = 2 }

public sealed record TeacherGrant(string TeacherId, string CertificateSha256, string DisplayName,
    TeacherRole Role, string SchoolId, string[] ClassroomIds, ClassroomPermission Permissions,
    DateTimeOffset ExpiresAtUtc);

public sealed record DeviceEnrollmentRequest(string DeviceId, string DisplayName,
    string PublicCertificateBase64, string CertificateSha256);

public sealed record EnrollmentPackage(string PayloadBase64, string SignatureBase64,
    string SigningCertificateBase64, string SignatureAlgorithm = "RSA-PSS-SHA256");

public sealed record DeviceEnrollment(string DeviceId, string SchoolId, string ClassroomId,
    string DeviceCertificateSha256, string PairingSecret, TeacherGrant Teacher,
    DateTimeOffset ExpiresAtUtc);

public sealed record LessonRequest(string SessionId, string SchoolId, string ClassroomId,
    DateTimeOffset ExpiresAtUtc);

internal sealed record EnrollmentPayload(int SchemaVersion, DeviceEnrollmentRequest Device,
    string SchoolId, string ClassroomId, TeacherGrant Teacher, string PairingSecret,
    DateTimeOffset IssuedAtUtc, DateTimeOffset ExpiresAtUtc);
