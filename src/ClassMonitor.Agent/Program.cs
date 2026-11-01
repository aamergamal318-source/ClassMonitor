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

        // Install/uninstall/status verbs: do the action and exit (no agent UI).
        if (args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal))
        {
            Environment.Exit(AgentInstaller.Run(args[0]));
            return;
        }

        var config = AppConfig.Load(AppConfig.AgentConfigPath);

        using var ui = new AgentUi(config.AgentVisible);
        using var server = new AgentServer(ui, config);
        server.Start();

        Application.Run(ui); // runs until the tray "Exit" item is chosen
    }
}
