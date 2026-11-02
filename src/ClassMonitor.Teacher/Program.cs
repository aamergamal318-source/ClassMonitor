using System.Security.Authentication;
using ClassMonitor.Shared;

namespace ClassMonitor.Teacher;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        AppConfig config;
        try { config = AppConfig.Load(AppConfig.TeacherConfigPath); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException or UnauthorizedAccessException or AuthenticationException)
        {
            MessageBox.Show($"ClassMonitor could not read its settings.\n\n{AppConfig.TeacherConfigPath}\n\n{ex.Message}", "ClassMonitor", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Application.Run(new MainForm(config));
    }
}
