using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// Linked into every headless check. Runs before Main, so even a failure ahead of a check's own try block
// is written to stderr and ends with exit code 3 instead of 0xe0434352 and a Windows crash dialog.
internal static partial class CrashFreeReporting
{
    private const uint SemFailCriticalErrors = 0x0001, SemNoGpFaultErrorBox = 0x0002;

    [ModuleInitializer]
    internal static void Install()
    {
        if (OperatingSystem.IsWindows()) SetErrorMode(SemFailCriticalErrors | SemNoGpFaultErrorBox);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Console.Error.WriteLine("FATAL: unhandled exception outside a check" + Environment.NewLine + e.ExceptionObject);
            Environment.Exit(3);
        };
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint SetErrorMode(uint mode);
}
