using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace ClassMonitor.Shared.Updates;

public sealed record InstallPlan(string Source, string Target, string Sha256, int ParentId, long ParentStartedUtcTicks);
public sealed record InstallResult(bool Success, string Message);

public static class UpdateInstaller
{
    public static string Start(StagedUpdate staged, string target)
    {
        ValidateTarget(target);
        string probe = Path.Combine(Path.GetDirectoryName(target)!, $".classmonitor-write-check-{Guid.NewGuid():N}");
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write)) { }
        File.Delete(probe);
        using (new FileStream(target + ".update-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        using var parent = Process.GetCurrentProcess();
        var plan = new InstallPlan(staged.Application, Path.GetFullPath(target), staged.Sha256,
            parent.Id, parent.StartTime.ToUniversalTime().Ticks);
        string path = Path.Combine(staged.Directory, "install.json");
        File.WriteAllText(path, JsonSerializer.Serialize(plan));
        var start = new ProcessStartInfo(staged.Worker) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--apply");
        start.ArgumentList.Add(path);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start the update installer.");
        return path;
    }

    public static async Task<InstallResult> ApplyAsync(InstallPlan plan,
        Func<string, Task>? launch = null, CancellationToken ct = default)
    {
        ValidateTarget(plan.Target);
        if (!Path.IsPathFullyQualified(plan.Source) || !Path.IsPathFullyQualified(plan.Target) ||
            Path.GetFileName(plan.Source) != Path.GetFileName(plan.Target) ||
            Path.GetFullPath(plan.Source).Equals(Path.GetFullPath(plan.Target), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid update locations.");
        if (!Hash(plan.Source).Equals(plan.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The staged application changed before installation.");
        // A file handle can be held across awaits; it also prevents two running
        // instances from replacing the same target concurrently.
        using var installationLock = new FileStream(plan.Target + ".update-lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        try
        {
            using var parent = Process.GetProcessById(plan.ParentId);
            if (parent.StartTime.ToUniversalTime().Ticks == plan.ParentStartedUtcTicks)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(TimeSpan.FromSeconds(60));
                await parent.WaitForExitAsync(wait.Token);
            }
        }
        catch (ArgumentException) { /* The initiating application has already exited. */ }

        string backup = plan.Target + $".backup-{Guid.NewGuid():N}";
        bool moved = false;
        try
        {
            File.Move(plan.Target, backup);
            moved = true;
            File.Copy(plan.Source, plan.Target, overwrite: false);
            if (!Hash(plan.Target).Equals(plan.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The installed copy failed verification.");
            await (launch ?? LaunchAsync)(plan.Target);
            return new InstallResult(true, "Update installed. The previous executable is kept beside the application as a backup.");
        }
        catch (Exception ex)
        {
            if (!moved)
            {
                try { await (launch ?? LaunchAsync)(plan.Target); }
                catch (Exception restart) { return new InstallResult(false, $"No changes were made. Restart failed: {restart.Message}"); }
                return new InstallResult(false, $"No changes were made; the previous program was restarted: {ex.Message}");
            }
            try
            {
                // Only remove the replacement created by this transaction.
                if (File.Exists(plan.Target)) File.Delete(plan.Target);
                File.Move(backup, plan.Target);
                await (launch ?? LaunchAsync)(plan.Target);
                return new InstallResult(false, $"The update failed; the previous version was restored: {ex.Message}");
            }
            catch (Exception rollback)
            {
                return new InstallResult(false, $"Recovery required. Previous executable: {backup}. {rollback.Message}");
            }
        }
    }

    private static async Task LaunchAsync(string executable)
    {
        using var child = Process.Start(new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false, CreateNoWindow = true
        }) ?? throw new IOException("Cannot restart the application.");
        var exited = child.WaitForExitAsync();
        if (await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(5))) == exited && child.ExitCode != 0)
            throw new IOException($"The updated application exited with code {child.ExitCode}.");
    }

    private static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }
    private static void ValidateTarget(string target)
    {
        if (Path.GetFileName(target) is not ("ClassMonitorTeacher.exe" or "ClassMonitorAgent.exe"))
            throw new InvalidDataException("Only a ClassMonitor application can be updated.");
    }
}
