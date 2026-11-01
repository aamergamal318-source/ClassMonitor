using System.Diagnostics;

namespace ClassMonitor.Agent;

/// <summary>
/// Executes power actions on this machine — with a hard safety interlock: if the teacher
/// console is running on the SAME machine, destructive power commands are ignored so the
/// operator can never shut down their own computer (e.g. while testing on one PC).
/// </summary>
internal static class PowerControl
{
    public static bool TeacherRunsHere() =>
        Process.GetProcessesByName("ClassMonitorTeacher").Length > 0;

    public static void Apply(string action)
    {
        // Safety interlock: never power off the machine that hosts the teacher console.
        if (TeacherRunsHere())
            return;

        string? args = action switch
        {
            "shutdown" => "/s /t 0",
            "restart" => "/r /t 0",
            "logoff" => "/l",
            _ => null,
        };
        if (args is null)
            return;

        try
        {
            Process.Start(new ProcessStartInfo("shutdown", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        catch { /* shutdown may be blocked by policy; ignore */ }
    }
}
