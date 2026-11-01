using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

// Turns every failure of an automated run into stderr text plus an exit code, never a crash dialog.
internal static partial class CheckHarness
{
    public const int ExitChecksFailed = 1, ExitSetupFailed = 2, ExitUnexpectedFailure = 3;
    private const string SelfTestPrefix = "--harness-self-test=";
    private const uint SemFailCriticalErrors = 0x0001, SemNoGpFaultErrorBox = 0x0002;
    private static int _unobservedFailures;

    public static string? Install(string[] args)
    {
        string? selfTest = args.FirstOrDefault(arg => arg.StartsWith(SelfTestPrefix, StringComparison.Ordinal))?[SelfTestPrefix.Length..];
        // Without this, an uncaught exception or native fault starts WerFault, which can show the
        // error dialog or offer the registered AeDebug debugger and block the run until dismissed.
        if (OperatingSystem.IsWindows()) SetErrorMode(SemFailCriticalErrors | SemNoGpFaultErrorBox);
        if (selfTest == "unguarded-crash") return selfTest;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Console.Error.WriteLine("FATAL: unhandled exception outside a check" + Environment.NewLine + e.ExceptionObject);
            Environment.Exit(ExitUnexpectedFailure);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Interlocked.Increment(ref _unobservedFailures);
            Console.Error.WriteLine("FAIL: unobserved task exception" + Environment.NewLine + e.Exception);
            e.SetObserved();
        };
        return selfTest;
    }

    public static int SetupFailed(Exception ex)
    {
        Console.Error.WriteLine("SETUP FAILED: no security check ran, so this run proves nothing.");
        Console.Error.WriteLine("Temporary directories or test identities could not be created. Windows must allow this user to " +
            "store temporary private keys; run outside the restriction instead of weakening certificate validation.");
        Console.Error.WriteLine(ex);
        return ExitSetupFailed;
    }

    public static int Summarize(int passed, IReadOnlyList<string> failures, string? artifacts)
    {
        // Finalizes abandoned tasks so their failures are counted before the verdict.
        GC.Collect(); GC.WaitForPendingFinalizers();
        int unobserved = Volatile.Read(ref _unobservedFailures);
        if (failures.Count == 0 && unobserved == 0)
        {
            TryDelete(artifacts);
            return 0;
        }
        Console.Error.WriteLine($"FAIL: {failures.Count} of {passed + failures.Count} checks failed; {unobserved} unobserved task failures.");
        foreach (string name in failures) Console.Error.WriteLine("  - " + name);
        if (artifacts is not null) Console.Error.WriteLine("Audit files kept in " + artifacts);
        return ExitChecksFailed;
    }

    public static void TryDelete(string? directory)
    {
        if (directory is null || !Directory.Exists(directory)) return;
        try { Directory.Delete(directory, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"WARNING: could not remove {directory}: {ex.Message}"); }
    }

    public static async Task<(int ExitCode, string Output, string Error)> RunSelfTestAsync(string mode)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        // "dotnet SecurityChecks.dll" hosts the checks in dotnet.exe, which needs the assembly path.
        if (Path.GetFileNameWithoutExtension(start.FileName) == "dotnet") start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        start.ArgumentList.Add(SelfTestPrefix + mode);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            // A child left waiting here is the blocked-by-dialog failure this harness exists to prevent.
            process.Kill(true);
            throw new TimeoutException("Self-test child did not exit: " + mode);
        }
        return (process.ExitCode, await output, await error);
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint SetErrorMode(uint mode);
}
