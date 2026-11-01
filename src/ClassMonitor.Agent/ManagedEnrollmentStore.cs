using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using ClassMonitor.Shared.Management;
using ClassMonitor.Shared.Sessions;

namespace ClassMonitor.Agent;

/// <summary>Trust is separate from user-editable legacy settings and must be administrator-owned.</summary>
internal static class ManagedEnrollmentStore
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClassMonitor.Managed");
    public static string PolicyPath => Path.Combine(Root, "enrollment.json");
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemAccount = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static DeviceEnrollment? Load()
    {
        if (!File.Exists(PolicyPath)) return null;
        ValidateAcl(new DirectoryInfo(Root).GetAccessControl());
        ValidateAcl(new FileInfo(PolicyPath).GetAccessControl());
        if ((File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(PolicyPath) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Enrollment policy cannot be a link.");
        if (new FileInfo(PolicyPath).Length > 128 * 1024) throw new InvalidDataException("Enrollment policy is too large.");
        var document = JsonSerializer.Deserialize<ManagedEnrollment>(File.ReadAllText(PolicyPath))
            ?? throw new InvalidDataException("Enrollment policy is empty.");
        return EnrollmentManager.ValidatePackage(document.Package, document.DeviceFingerprint, document.TeacherFingerprint);
    }

    public static DeviceEnrollment Import(ManagedEnrollment document)
    {
        if (!IsAdministrator()) throw new UnauthorizedAccessException("A local school administrator must import device enrollment.");
        var enrollment = EnrollmentManager.ValidatePackage(document.Package, document.DeviceFingerprint, document.TeacherFingerprint);
        if (Directory.Exists(Root) && (File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Enrollment directory cannot be a link.");
        Directory.CreateDirectory(Root);
        var directorySecurity = new DirectorySecurity();
        directorySecurity.SetOwner(Administrators);
        directorySecurity.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { Administrators, SystemAccount })
            directorySecurity.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directorySecurity.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(Root).SetAccessControl(directorySecurity);
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
            File.Move(temporary, PolicyPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return Load() ?? throw new IOException("Enrollment could not be verified after import.");
    }

    private static void ValidateAcl(FileSystemSecurity security)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner != Administrators && owner != SystemAccount)
            throw new UnauthorizedAccessException("Enrollment policy must be owned by Administrators or SYSTEM.");
        const FileSystemRights writes = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes
            | FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & writes) != 0
                && rule.IdentityReference != Administrators && rule.IdentityReference != SystemAccount)
                throw new UnauthorizedAccessException("Unprivileged accounts may not modify device enrollment.");
    }
}
