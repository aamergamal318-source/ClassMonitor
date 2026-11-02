using System.Diagnostics;
using Microsoft.Win32;

namespace ClassMonitor.Agent;

/// <summary>
/// Opt-in start at sign-in for this Windows account only, with the same rights and visible tray icon as a
/// normal start. It deliberately needs no administrator rights and never creates an elevated scheduled task,
/// because this program's folder is writable by the student.
/// </summary>
internal static class AgentInstaller
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "ClassMonitorAgent";
    private const string LegacyTaskName = "ClassMonitor Agent";

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
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        run.SetValue(RunValue, '"' + ExePath + '"');
        if (showResult) Info("ClassMonitor Agent will now start when you sign in.");
        return true;
    }

    public static bool Uninstall(bool showResult = true)
    {
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            run?.DeleteValue(RunValue, throwOnMissingValue: false);
        RemoveLegacyTask();
        if (showResult) Info("Auto-start removed.");
        return true;
    }

    public static bool IsInstalled()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        return run?.GetValue(RunValue) is not null;
    }

    private static int Status()
    {
        Info(IsInstalled() ? "Auto-start is ON for this account." : "Auto-start is OFF.");
        return 0;
    }

    private static int Usage()
    {
        Info("Usage: ClassMonitorAgent.exe [--install | --uninstall | --status]");
        return 2;
    }

    /// <summary>Earlier versions created an elevated task. Removing it needs the rights that created it; failure is harmless here.</summary>
    private static void RemoveLegacyTask()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"), $"/Delete /TN \"{LegacyTaskName}\" /F")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            });
            process?.WaitForExit(5000);
        }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static void Info(string text) =>
        MessageBox.Show(text, "ClassMonitor Agent", MessageBoxButtons.OK, MessageBoxIcon.Information);
}
