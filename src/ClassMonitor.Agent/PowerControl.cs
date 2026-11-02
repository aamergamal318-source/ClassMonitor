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
            throw new InvalidOperationException("This computer is running the teacher console.");

        string? args = action switch
        {
            "shutdown" => "/s /t 0",
            "restart" => "/r /t 0",
            "logoff" => "/l",
            _ => null,
        };
        if (args is null)
            throw new InvalidDataException("Unsupported power action.");

        using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe"), args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
    }
}
