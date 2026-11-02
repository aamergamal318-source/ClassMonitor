using System.Text.Json;
using ClassMonitor.Shared.Sessions;

namespace ClassMonitor.Shared.Management;

/// <summary>
/// The password hash and the teacher added with it, in this Windows account's profile. The student could edit these
/// files, which affects only their own computer: without the password or a matching teacher key nobody else gains anything.
/// </summary>
public static class LocalEnrollmentStore
{
    private const long MaximumBytes = 128 * 1024;
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassMonitor");
    public static string PasswordPath => Path.Combine(Root, "pairing-password.json");
    public static string EnrollmentPath => Path.Combine(Root, "enrollment.json");

    public static PairingPassword? LoadPassword() => Read<PairingPassword>(PasswordPath);
    public static void SavePassword(PairingPassword password) => Write(PasswordPath, password);
    public static void SaveEnrollment(ManagedEnrollment document) => Write(EnrollmentPath, document);

    public static DeviceEnrollment? Load(string devicePin)
    {
        var document = Read<ManagedEnrollment>(EnrollmentPath);
        if (document is null) return null;
        if (!string.Equals(document.DeviceFingerprint, devicePin, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The saved enrollment belongs to another device identity.");
        return EnrollmentManager.ValidatePackage(document.Package, document.DeviceFingerprint, document.TeacherFingerprint);
    }

    private static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("The saved file is too large.");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
    }

    private static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
