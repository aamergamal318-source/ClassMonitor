using ClassMonitor.Shared;

namespace ClassMonitor.Agent;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Length > 0 && args[0].Equals("--diagnose", StringComparison.OrdinalIgnoreCase))
        {
            Application.Run(new CaptureDiagnostics());
            return;
        }

        if (args.Length == 2 && args[0].Equals("--enrollment-request", StringComparison.OrdinalIgnoreCase))
        {
            AgentEnrollment.WriteRequest(args[1]);
            return;
        }

        // Install/uninstall/status verbs: do the action and exit (no agent UI).
        if (args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal))
        {
            Environment.Exit(AgentInstaller.Run(args[0]));
            return;
        }

        AppConfig config;
        try { config = AppConfig.Load(AppConfig.AgentConfigPath); }
        catch (ArgumentException ex) { Stop(ex.Message); return; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { Stop(ex.Message); return; }

        // Website and internet blocks left by a crash or restart are removed if this start has the rights to do so.
        if (ClassMonitor.Shared.Management.ManagedEnrollmentStore.IsAdministrator())
        {
            try { ClassMonitor.Shared.Management.RestrictionCleanup.Sweep(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
        }

        // The tray icon is always visible; the legacy AgentVisible value is read but cannot hide the agent.
        using var ui = new AgentUi();
        using var server = new AgentServer(ui, config);
        server.Start();

        Application.Run(ui); // runs until the tray "Exit" item is chosen
    }

    private static void Stop(string message) =>
        MessageBox.Show(message, "ClassMonitor Agent", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
