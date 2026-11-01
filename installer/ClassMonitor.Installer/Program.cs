using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ClassMonitor.Installation;

internal static class Program
{
    private static Stream? Payload() => Assembly.GetExecutingAssembly().GetManifestResourceStream("ClassMonitor.Payload.zip");
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--verify-payload")
        {
            using var payload = Payload();
            File.WriteAllText(args[1], JsonSerializer.Serialize(new { PayloadPresent = payload is not null, Bytes = payload?.Length ?? 0, Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() }));
            return payload is not null ? 0 : 2;
        }
        ApplicationConfiguration.Initialize();
        if (args.Length == 1 && args[0] == "--uninstall") return Uninstall();
        Application.Run(new SetupForm());
        return 0;
    }

    private static int Uninstall()
    {
        try
        {
            string root = AppContext.BaseDirectory;
            var receipt = InstallerCore.ReadReceipt(root);
            if (MessageBox.Show($"Remove ClassMonitor {receipt.Component}? User settings, school enrollment and recordings will be retained.", "ClassMonitor uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 0;
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell/v1.0/powershell.exe")) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root, "Uninstall.ps1"), "-Confirmed", "-WaitForProcessId", Environment.ProcessId.ToString() }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new IOException("Cannot start uninstall worker.");
            return 0;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Uninstall failed", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
    }

    private sealed class SetupForm : Form
    {
        private readonly ComboBox _component = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 310 };
        private readonly CheckBox _consent = new() { Text = "Install this component for my Windows account", AutoSize = true };
        private readonly Label _status = new() { AutoSize = false, Height = 85, Dock = DockStyle.Bottom };
        private readonly Button _install = new() { Text = "Install", Width = 130, Enabled = false };
        public SetupForm()
        {
            Text = "ClassMonitor setup"; ClientSize = new Size(640, 390); StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
            var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(22), WrapContents = false };
            body.Controls.Add(new Label { Text = "ClassMonitor · Classroom tools", Font = new Font("Segoe UI", 20, FontStyle.Bold), AutoSize = true });
            body.Controls.Add(new Label { Text = "Choose Teacher console or Student agent. Installation does not enroll a device,\nstart monitoring, open firewall ports or enable automatic startup.", AutoSize = true, Margin = new Padding(3, 12, 3, 18) });
            _component.Items.AddRange(new object[] { "Teacher console", "Student agent" }); _component.SelectedIndex = 0;
            _component.AccessibleName = "Component to install";
            _consent.AccessibleName = "Installation consent";
            body.Controls.Add(_component); body.Controls.Add(_consent); body.Controls.Add(_install);
            _status.Padding = new Padding(22, 5, 22, 5);
            using var payload = Payload();
            bool available = payload is not null;
            _status.Text = available ? "MIT-licensed. Unsigned local build: signing credentials were not provided.\nManaged enrollment is a separate administrator-approved workflow." : "This development bootstrapper has no payload. Build with -p:PayloadPath=<absolute product ZIP>.";
            _consent.CheckedChanged += (_, _) => _install.Enabled = available && _consent.Checked;
            _install.Click += async (_, _) =>
            {
                _install.Enabled = _component.Enabled = _consent.Enabled = false;
                try
                {
                    var component = _component.SelectedIndex == 0 ? InstallComponent.Teacher : InstallComponent.Agent;
                    string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ClassMonitor", component.ToString());
                    _status.Text = "Installing verified package entries…";
                    await Task.Run(() =>
                    {
                        using var zip = Payload() ?? throw new InvalidDataException("Missing embedded package.");
                        using var reader = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("ClassMonitor.Uninstall.ps1")!);
                        string version = Assembly.GetExecutingAssembly().GetName().Version!.ToString(3);
                        InstallerCore.Install(zip, component, root, version, Environment.ProcessPath!, reader.ReadToEnd(), new WindowsRegistration());
                    });
                    _status.Text = $"Installed in {root}\nLaunch {InstallerCore.ApplicationName(component)} when ready. School enrollment is still required.";
                }
                catch (Exception ex) { _status.Text = "Installation failed; previous files were restored. " + ex.Message; }
                finally { _component.Enabled = _consent.Enabled = true; _install.Enabled = _consent.Checked; }
            };
            Controls.Add(body); Controls.Add(_status);
        }
    }
}
