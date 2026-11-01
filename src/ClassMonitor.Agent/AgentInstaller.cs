using System.Diagnostics;

namespace ClassMonitor.Agent;

/// <summary>
/// Auto-start management for the agent. Uses a Windows Scheduled Task that runs the agent
/// "at log on". This deliberately runs in the interactive user session (not session 0), which
/// is required for screen capture and on-screen overlays — a classic Windows service cannot do
/// that. Creating/removing the task requires administrator rights.
/// </summary>
internal static class AgentInstaller
{
    public const string TaskName = "ClassMonitor Agent";

    /// <summary>Handle a command-line verb (--install / --uninstall / --status). Returns an exit code.</summary>
    public static int Run(string verb)
    {
        return verb.ToLowerInvariant() switch
        {
            "--install" => Install() ? 0 : 1,
            "--uninstall" => Uninstall() ? 0 : 1,
            "--status" => Status(),
            _ => Usage(),
        };
    }

    private static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;

    public static bool Install(bool showResult = true)
    {
        // /SC ONLOGON = at logon, /RL HIGHEST = run with highest privileges, /F = overwrite.
        var (ok, output) = RunSchtasks(
            $"/Create /TN \"{TaskName}\" /TR \"\\\"{ExePath}\\\"\" /SC ONLOGON /RL HIGHEST /F");
        if (showResult)
            Info(ok
                ? "ClassMonitor Agent will now start automatically at logon."
                : "Install failed (administrator rights are required):\n\n" + output);
        return ok;
    }

    public static bool Uninstall(bool showResult = true)
    {
        var (ok, output) = RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
        if (showResult)
            Info(ok
                ? "Auto-start removed."
                : "Remove failed (administrator rights may be required):\n\n" + output);
        return ok;
    }

    public static bool IsInstalled()
    {
        var (ok, _) = RunSchtasks($"/Query /TN \"{TaskName}\"");
        return ok;
    }

    private static int Status()
    {
        Info(IsInstalled() ? "Auto-start is INSTALLED (runs at logon)." : "Auto-start is NOT installed.");
        return 0;
    }

    private static int Usage()
    {
        Info("Usage: ClassMonitorAgent.exe [--install | --uninstall | --status]");
        return 2;
    }

    private static (bool ok, string output) RunSchtasks(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            })!;
            string outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode == 0, outp.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static void Info(string text) =>
        MessageBox.Show(text, "ClassMonitor Agent", MessageBoxButtons.OK, MessageBoxIcon.Information);
}
