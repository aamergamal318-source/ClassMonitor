using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using ClassMonitor.Shared.Sessions;

namespace ClassMonitor.Shared.Management;

/// <summary>Trust is separate from user-editable legacy settings and must be administrator-owned.</summary>
[SupportedOSPlatform("windows")]
public static class ManagedEnrollmentStore
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClassMonitor.Managed");

    /// <summary>One policy per device identity, so each Windows account on a computer enrolls separately.</summary>
    public static string PolicyPath(string devicePin)
    {
        if (!CertificateTrust.IsFingerprint(devicePin)) throw new InvalidDataException("A device fingerprint is required.");
        return Path.Combine(Root, $"enrollment-{devicePin.ToLowerInvariant()}.json");
    }
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemAccount = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static DeviceEnrollment? Load(string devicePin)
    {
        string path = PolicyPath(devicePin);
        if (!File.Exists(path)) return null;
        ValidateAcl(new DirectoryInfo(Root).GetAccessControl());
        ValidateAcl(new FileInfo(path).GetAccessControl());
        if ((File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Enrollment policy cannot be a link.");
        if (new FileInfo(path).Length > 128 * 1024) throw new InvalidDataException("Enrollment policy is too large.");
        var document = JsonSerializer.Deserialize<ManagedEnrollment>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Enrollment policy is empty.");
        if (!string.Equals(document.DeviceFingerprint, devicePin, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Enrollment policy belongs to another device identity.");
        return EnrollmentManager.ValidatePackage(document.Package, document.DeviceFingerprint, document.TeacherFingerprint);
    }

    public static DeviceEnrollment Import(ManagedEnrollment document)
    {
        if (!IsAdministrator()) throw new UnauthorizedAccessException("A local school administrator must import device enrollment.");
        var enrollment = EnrollmentManager.ValidatePackage(document.Package, document.DeviceFingerprint, document.TeacherFingerprint);
        if (Directory.Exists(Root))
        {
            // A folder made earlier by a student could be swapped for a link while its permissions are being rewritten.
            if ((File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Enrollment directory cannot be a link.");
            ValidateAcl(new DirectoryInfo(Root).GetAccessControl());
        }
        var directorySecurity = new DirectorySecurity();
        directorySecurity.SetOwner(Administrators);
        directorySecurity.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { Administrators, SystemAccount })
            directorySecurity.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directorySecurity.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (Directory.Exists(Root)) new DirectoryInfo(Root).SetAccessControl(directorySecurity);
        else new DirectoryInfo(Root).Create(directorySecurity);
        string temporary = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
            var fileSecurity = new FileSecurity();
            fileSecurity.SetOwner(Administrators);
            fileSecurity.SetAccessRuleProtection(true, false);
            fileSecurity.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, AccessControlType.Allow));
            fileSecurity.AddAccessRule(new FileSystemAccessRule(SystemAccount, FileSystemRights.FullControl, AccessControlType.Allow));
            fileSecurity.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Read, AccessControlType.Allow));
            new FileInfo(temporary).SetAccessControl(fileSecurity);
            File.Move(temporary, PolicyPath(document.DeviceFingerprint), true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return Load(document.DeviceFingerprint) ?? throw new IOException("Enrollment could not be verified after import.");
    }

    private static void ValidateAcl(FileSystemSecurity security)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner != Administrators && owner != SystemAccount)
            throw new UnauthorizedAccessException("Enrollment policy must be owned by Administrators or SYSTEM.");
        const FileSystemRights writes = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes
            | FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership
            | (FileSystemRights)0x40000000 /* GENERIC_WRITE */ | (FileSystemRights)0x10000000 /* GENERIC_ALL */;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & writes) != 0
                && rule.IdentityReference != Administrators && rule.IdentityReference != SystemAccount)
                throw new UnauthorizedAccessException("Unprivileged accounts may not modify device enrollment.");
    }
}
