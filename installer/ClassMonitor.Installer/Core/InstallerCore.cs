using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace ClassMonitor.Installation;

public enum InstallComponent { Teacher, Agent }
public sealed record InstallReceipt(string Product, int Schema, InstallComponent Component, string Version, Dictionary<string, string> Files);
public interface IInstallerRegistration
{
    object? Capture(InstallComponent component);
    void Register(InstallReceipt receipt, string root);
    void Restore(InstallComponent component, object? snapshot);
    void Remove(InstallComponent component);
}

/// <summary>Flat, exact-entry extraction and reversible file transactions; no registry or process side effects in this core.</summary>
public static class InstallerCore
{
    public const string Product = "ClassMonitor.ManagedDesktop";
    public const string ManifestName = "install-manifest.json";
    public const long MaximumEntryBytes = 512L * 1024 * 1024;
    public static string ApplicationName(InstallComponent component) => component switch
    {
        InstallComponent.Teacher => "ClassMonitorTeacher.exe", InstallComponent.Agent => "ClassMonitorAgent.exe",
        _ => throw new InvalidDataException("Unknown component")
    };

    public static InstallReceipt Install(Stream payload, InstallComponent component, string root, string version,
        string installerExecutable, string uninstallScript, IInstallerRegistration registration)
    {
        root = ValidateRoot(root);
        ApplicationName(component);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            var previous = ReadReceipt(root);
            if (previous.Component != component) throw new InvalidDataException("The directory belongs to another component.");
        }
        Directory.CreateDirectory(root);
        string staging = Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N"));
        string backup = Path.Combine(root, ".rollback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(backup);
        var moved = new List<string>();
        var applied = new List<string>();
        bool cleanupBackup = false;
        object? registryBefore = registration.Capture(component);
        try
        {
            using var archive = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true);
            string role = component.ToString().ToLowerInvariant();
            Extract($"ClassMonitor/{role}/{ApplicationName(component)}", ApplicationName(component), required: true);
            Extract("ClassMonitor/updater/ClassMonitorUpdater.exe", "ClassMonitorUpdater.exe", required: true);
            foreach (string name in new[] { "LICENSE", "README.md", "UPDATE-GUIDE.md", "DIAGNOSTICS.md" })
                Extract("ClassMonitor/" + name, name, required: name == "LICENSE");
            var info = FileVersionInfo.GetVersionInfo(Path.Combine(staging, ApplicationName(component)));
            if (!Version.TryParse(version, out var expected) ||
                new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart) != new Version(expected.Major, expected.Minor, Math.Max(0, expected.Build)))
                throw new InvalidDataException("Package application version does not match installer version.");
            CheckExecutable(installerExecutable);
            File.Copy(installerExecutable, Path.Combine(staging, "ClassMonitorSetup.exe"));
            File.WriteAllText(Path.Combine(staging, "Uninstall.ps1"), uninstallScript);
            var hashes = Directory.GetFiles(staging).ToDictionary(path => Path.GetFileName(path)!, Hash);
            var receipt = new InstallReceipt(Product, 1, component, version, hashes);
            File.WriteAllText(Path.Combine(staging, ManifestName), JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
            string[] files = Directory.GetFiles(staging).Select(Path.GetFileName).ToArray()!;
            foreach (string name in files)
            {
                string target = Path.Combine(root, name);
                if (File.Exists(target))
                {
                    using (new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                }
            }
            foreach (string name in files)
            {
                string target = Path.Combine(root, name);
                if (File.Exists(target)) { File.Move(target, Path.Combine(backup, name)); moved.Add(name); }
                File.Move(Path.Combine(staging, name), target);
                applied.Add(name);
            }
            registration.Register(receipt, root);
            cleanupBackup = true;
            return receipt;

            void Extract(string entryName, string name, bool required)
            {
                var entries = archive.Entries.Where(e => e.FullName.Replace('\\', '/') == entryName).ToArray();
                if (entries.Length == 0 && !required) return;
                if (entries.Length != 1 || entries[0].Length <= 0 || entries[0].Length > MaximumEntryBytes)
                    throw new InvalidDataException("Invalid package entry: " + entryName);
                string target = Path.Combine(staging, name);
                entries[0].ExtractToFile(target, overwrite: false);
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) CheckExecutable(target);
            }
        }
        catch (Exception failure)
        {
            try
            {
                foreach (string name in applied) if (File.Exists(Path.Combine(root, name))) File.Delete(Path.Combine(root, name));
                foreach (string name in moved) File.Move(Path.Combine(backup, name), Path.Combine(root, name));
                registration.Restore(component, registryBefore);
                cleanupBackup = true;
            }
            catch (Exception recovery) { throw new AggregateException($"Installation failed. Recovery copies retained in {backup}", failure, recovery); }
            throw;
        }
        finally { TryDeleteFlatOwnedDirectory(staging); if (cleanupBackup) TryDeleteFlatOwnedDirectory(backup); }
    }

    public static void Uninstall(string root, IInstallerRegistration registration)
    {
        root = ValidateRoot(root);
        var receipt = ReadReceipt(root);
        string[] names = receipt.Files.Keys.Append(ManifestName).ToArray();
        foreach (string name in names)
            if (File.Exists(Path.Combine(root, name))) using (new FileStream(Path.Combine(root, name), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        string backup = Path.Combine(root, ".uninstall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        var moved = new List<string>();
        bool cleanupBackup = false;
        object? snapshot = registration.Capture(receipt.Component);
        try
        {
            foreach (string name in names)
                if (File.Exists(Path.Combine(root, name))) { File.Move(Path.Combine(root, name), Path.Combine(backup, name)); moved.Add(name); }
            registration.Remove(receipt.Component);
            cleanupBackup = true;
        }
        catch (Exception failure)
        {
            try
            {
                foreach (string name in moved) File.Move(Path.Combine(backup, name), Path.Combine(root, name));
                registration.Restore(receipt.Component, snapshot);
                cleanupBackup = true;
            }
            catch (Exception recovery) { throw new AggregateException($"Uninstall failed. Recovery copies retained in {backup}", failure, recovery); }
            throw;
        }
        finally { if (cleanupBackup) TryDeleteFlatOwnedDirectory(backup); }
        if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
    }

    public static InstallReceipt ReadReceipt(string root)
    {
        root = ValidateRoot(root);
        var receipt = JsonSerializer.Deserialize<InstallReceipt>(File.ReadAllText(Path.Combine(root, ManifestName)))
            ?? throw new InvalidDataException("No installation receipt.");
        if (receipt.Product != Product || receipt.Schema != 1 || receipt.Files is null || receipt.Files.Count == 0)
            throw new InvalidDataException("The directory is not a recognized ClassMonitor installation.");
        var allowed = new HashSet<string>(new[] { ApplicationName(receipt.Component), "ClassMonitorUpdater.exe", "ClassMonitorSetup.exe", "Uninstall.ps1", "LICENSE", "README.md", "UPDATE-GUIDE.md", "DIAGNOSTICS.md" }, StringComparer.Ordinal);
        if (!receipt.Files.ContainsKey(ApplicationName(receipt.Component)) || receipt.Files.Keys.Any(name => !allowed.Contains(name) || Path.GetFileName(name) != name))
            throw new InvalidDataException("The installation receipt contains unsafe file names.");
        return receipt;
    }

    public static string ValidateRoot(string root)
    {
        if (!Path.IsPathFullyQualified(root)) throw new InvalidDataException("An absolute installation directory is required.");
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (full.Length <= Path.GetPathRoot(full)!.Length + 1) throw new InvalidDataException("A filesystem root is not an installation directory.");
        for (var directory = new DirectoryInfo(full); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Installation directories must not pass through links or junctions.");
        return full;
    }

    private static string Hash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void CheckExecutable(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z') throw new InvalidDataException("Invalid Windows executable.");
    }
    private static void TryDeleteFlatOwnedDirectory(string directory)
    {
        try { DeleteFlatOwnedDirectory(directory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { /* Keep transaction remnants for recovery, never recursively delete unknown content. */ }
    }
    private static void DeleteFlatOwnedDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return;
        if (Directory.EnumerateDirectories(directory).Any()) throw new InvalidDataException("Unexpected directory in installation transaction.");
        foreach (string path in Directory.GetFiles(directory)) File.Delete(path);
        Directory.Delete(directory);
    }
}
