using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ClassMonitor.Shared.Management;
using ClassMonitor.Shared.Sessions;

namespace ClassMonitor.Teacher.Management;

public sealed record EnrollmentChoice(string Host, int Port, string SchoolId, string ClassroomId, string TeacherName,
    TeacherRole Role, ClassroomPermission Permissions, DateTimeOffset ExpiresAtUtc);

/// <summary>Signs a reviewed device request; the document contains pairing material and must travel by an approved channel.</summary>
public static class EnrollmentWorkflow
{
    private const long MaximumRequestBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static DeviceEnrollmentRequest ReadRequest(string path)
    {
        if (new FileInfo(path).Length > MaximumRequestBytes) throw new InvalidDataException("Enrollment request is too large.");
        return JsonSerializer.Deserialize<DeviceEnrollmentRequest>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Enrollment request is empty.");
    }

    public static (ManagedEnrollment Document, PairedDevice Device) Create(DeviceEnrollmentRequest request,
        X509Certificate2 teacherIdentity, EnrollmentChoice choice)
    {
        if (string.IsNullOrWhiteSpace(choice.Host) || choice.Host.Length > 253 || choice.Port is < 1024 or > 65535)
            throw new InvalidDataException("A valid device address and port are required.");
        var package = EnrollmentManager.CreatePackage(request, teacherIdentity, choice.SchoolId, choice.ClassroomId,
            choice.TeacherName, choice.Role, choice.Permissions, choice.ExpiresAtUtc);
        string teacherPin = CertificateTrust.Fingerprint(teacherIdentity);
        var enrollment = EnrollmentManager.ValidatePackage(package, request.CertificateSha256, teacherPin);
        return (new ManagedEnrollment(package, request.CertificateSha256, teacherPin),
            new PairedDevice(choice.Host.Trim(), choice.Port, enrollment, request.PublicCertificateBase64));
    }

    public static void WriteDocument(string path, ManagedEnrollment document) =>
        File.WriteAllText(path, JsonSerializer.Serialize(document, Indented));

    /// <summary>A re-enrolled identity replaces its record. Another account on the same computer keeps its own.</summary>
    public static void Save(List<PairedDevice> devices, PairedDevice device)
    {
        devices.RemoveAll(existing => existing.Enrollment.DeviceId == device.Enrollment.DeviceId);
        devices.Add(device);
    }
}
