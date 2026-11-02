using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ClassMonitor.Installation;
using ClassMonitor.Shared.Management;
using ClassMonitor.Shared.Sessions;
using Microsoft.Win32;

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
        if (args.Length == 2 && args[0] == "--import-enrollment") return ImportEnrollment(args[1]);
        if (args.Length == 1 && args[0] == "--clean-restrictions") return CleanRestrictions();
        Application.Run(new SetupForm());
        return 0;
    }

    /// <summary>
    /// Runs elevated from this copy of setup, never from the student's writable program folder. The administrator
    /// is responsible for running a trusted copy and comparing the teacher fingerprint out of band.
    /// </summary>
    private static int ImportEnrollment(string path)
    {
        const string title = "ClassMonitor enrollment / تسجيل الجهاز";
        try
        {
            if (!ManagedEnrollmentStore.IsAdministrator())
                throw new UnauthorizedAccessException("A local school administrator must import device enrollment.");
            if (new FileInfo(path).Length > 128 * 1024) throw new InvalidDataException("Enrollment file is too large.");
            var document = JsonSerializer.Deserialize<ManagedEnrollment>(File.ReadAllText(path)) ?? throw new InvalidDataException("Enrollment file is empty.");
            var enrollment = EnrollmentManager.ValidatePackage(document.Package, document.DeviceFingerprint, document.TeacherFingerprint);
            string review = $"Teacher: {enrollment.Teacher.DisplayName}\nSchool: {enrollment.SchoolId}\nClassroom: {enrollment.ClassroomId}\n" +
                $"Permissions: {enrollment.Teacher.Permissions}\nExpires: {enrollment.ExpiresAtUtc.LocalDateTime:g}\n\n" +
                $"Teacher fingerprint:\n{Group(document.TeacherFingerprint)}\n\nDevice fingerprint:\n{Group(document.DeviceFingerprint)}\n\n" +
                "Compare both fingerprints with the teacher console and the student's request.\nTrust this teacher on this device?\n" +
                "قارن البصمتين بما يظهر على جهاز المعلم وطلب الطالب. هل تثق بهذا المعلم على هذا الجهاز؟";
            if (MessageBox.Show(review, title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return 3;
            ManagedEnrollmentStore.Import(document);
            MessageBox.Show("Device enrolled. / تم تسجيل الجهاز.", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException
            or System.Security.Authentication.AuthenticationException or System.Security.Cryptography.CryptographicException)
        {
            MessageBox.Show(ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static int CleanRestrictions()
    {
        const string title = "ClassMonitor restrictions";
        try
        {
            if (!ManagedEnrollmentStore.IsAdministrator()) throw new UnauthorizedAccessException("Administrator rights are required.");
            RestrictionCleanup.Sweep();
            MessageBox.Show("Leftover website and internet blocks were removed. / أُزيلت قيود المواقع والإنترنت المتبقية.", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            MessageBox.Show(ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static string Group(string pin) => string.Join(' ', Enumerable.Range(0, pin.Length / 4).Select(i => pin.Substring(i * 4, 4)));

    /// <summary>Windows shows its own elevation prompt for this same trusted copy of setup.</summary>
    private static void RunElevated(params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        try { using var process = Process.Start(start); }
        catch (Win32Exception) { /* The administrator declined; nothing changed. */ }
    }

    private sealed class SetupForm : Form
    {
        private readonly ComboBox _component = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 310 };
        private readonly CheckBox _consent = new() { Text = "Install this component for my Windows account / تثبيت هذا المكوّن لحسابي في Windows", AutoSize = true };
        private readonly CheckBox _desktop = new() { Text = "Desktop shortcut / اختصار على سطح المكتب", AutoSize = true, Checked = true };
        private readonly CheckBox _launch = new() { Text = "Open after installing / فتح البرنامج بعد التثبيت", AutoSize = true, Checked = true };
        private readonly CheckBox _autostart = new() { Text = "Start when I sign in (visible in the tray) / التشغيل عند تسجيل الدخول (يظهر بجانب الساعة)", AutoSize = true };
        private readonly CheckBox _firewall = new() { Text = "Allow classroom connections through Windows Firewall (asks for administrator) / السماح باتصالات الصف عبر جدار الحماية", AutoSize = true };
        private readonly Label _status = new() { AutoSize = false, Height = 110, Dock = DockStyle.Bottom };
        private readonly Button _install = new() { Text = "Install / تثبيت", Width = 130, Enabled = false };
        public SetupForm()
        {
            Text = "ClassMonitor setup"; ClientSize = new Size(760, 600); StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
            var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(22), WrapContents = false };
            body.Controls.Add(new Label { Text = "ClassMonitor · Classroom tools", Font = new Font("Segoe UI", 20, FontStyle.Bold), AutoSize = true });
            body.Controls.Add(new Label { Text = "Choose Teacher console or Student agent. Installing never starts monitoring: a student device\nmust be enrolled by an administrator, and the teacher must start a lesson.\nاختر جهاز المعلم أو الطالب. التثبيت لا يبدأ المراقبة: يجب تسجيل جهاز الطالب وأن يبدأ المعلم درسًا.", AutoSize = true, Margin = new Padding(3, 12, 3, 18) });
            _component.Items.AddRange(new object[] { "Teacher console / جهاز المعلم", "Student agent / جهاز الطالب" }); _component.SelectedIndex = 0;
            _component.AccessibleName = "Component to install";
            _consent.AccessibleName = "Installation consent";
            _desktop.AccessibleName = "Desktop shortcut";
            _launch.AccessibleName = "Open after installing";
            _autostart.AccessibleName = "Start when I sign in";
            _firewall.AccessibleName = "Allow through Windows Firewall";
            var importEnrollment = new Button { Text = "Import enrollment file… / استيراد ملف التسجيل", AutoSize = true, AccessibleName = "Import enrollment file" };
            importEnrollment.Click += (_, _) =>
            {
                using var dialog = new OpenFileDialog { Title = "Choose the enrollment file from the teacher", Filter = "Enrollment (*.json)|*.json" };
                if (dialog.ShowDialog(this) == DialogResult.OK) RunElevated("--import-enrollment", dialog.FileName);
            };
            var cleanRestrictions = new Button { Text = "Remove leftover restrictions / إزالة القيود المتبقية", AutoSize = true, AccessibleName = "Remove leftover restrictions" };
            cleanRestrictions.Click += (_, _) => RunElevated("--clean-restrictions");
            var tools = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 14, 0, 0) };
            tools.Controls.AddRange(new Control[] { importEnrollment, cleanRestrictions });
            body.Controls.AddRange(new Control[] { _component, _desktop, _launch, _autostart, _firewall, _consent, _install,
                new Label { Text = "Administrator tools (run this copy of setup from a location students cannot change) / أدوات المسؤول", AutoSize = true, Margin = new Padding(3, 18, 3, 0) }, tools });
            _status.Padding = new Padding(22, 5, 22, 5);
            _component.SelectedIndexChanged += (_, _) =>
            {
                bool student = _component.SelectedIndex == 1;
                _autostart.Enabled = student;
                _autostart.Checked = student && AutostartIsOn();
                _launch.Checked = !student;
            };
            _autostart.Enabled = false;
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
                    string application = Path.Combine(root, InstallerCore.ApplicationName(component));
                    bool desktop = _desktop.Checked, autostart = _autostart.Checked, firewall = _firewall.Checked, launch = _launch.Checked;
                    _status.Text = "Installing verified package entries…";
                    await Task.Run(() =>
                    {
                        using var zip = Payload() ?? throw new InvalidDataException("Missing embedded package.");
                        using var reader = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("ClassMonitor.Uninstall.ps1")!);
                        string version = Assembly.GetExecutingAssembly().GetName().Version!.ToString(3);
                        InstallerCore.Install(zip, component, root, version, reader.ReadToEnd(), new WindowsRegistration(desktop, autostart));
                    });
                    string notes = firewall ? AllowThroughFirewall(component, application) : "";
                    if (launch)
                    {
                        try { using (Process.Start(new ProcessStartInfo(application) { UseShellExecute = true, WorkingDirectory = root })) { } }
                        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { notes += "\nThe program could not be opened: " + ex.Message; }
                    }
                    _status.Text = $"Installed in {root}\nUse the ClassMonitor shortcut in the Start menu. Later updates come from inside the program.\n{NextStep(component)}{notes}";
                }
                catch (Exception ex) { _status.Text = "Installation failed; previous files were restored. " + ex.Message; }
                finally { _component.Enabled = _consent.Enabled = true; _install.Enabled = _consent.Checked; }
            };
            Controls.Add(body); Controls.Add(_status);
        }

        private static bool AutostartIsOn()
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return run?.GetValue(InstallerCore.RunValueName) is not null;
        }

        private static string NextStep(InstallComponent component) => component == InstallComponent.Teacher
            ? "Next: open the console, then use Enroll device for each student request. / الخطوة التالية: استخدم Enroll device لكل طلب طالب."
            : "Next: from the tray icon choose Export enrollment request and give the file to the teacher. / الخطوة التالية: صدّر طلب التسجيل من أيقونة البرنامج وأعطه للمعلم.";

        /// <summary>Windows shows its own elevation prompt. Declining keeps the install and says what is missing.</summary>
        private static string AllowThroughFirewall(InstallComponent component, string application)
        {
            try
            {
                string netsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe");
                var probe = new ProcessStartInfo(netsh) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string argument in new[] { "advfirewall", "firewall", "show", "rule", "name=" + InstallerCore.FirewallRuleName(component, Environment.UserName) }) probe.ArgumentList.Add(argument);
                using (var existing = Process.Start(probe) ?? throw new IOException("Cannot start netsh."))
                {
                    existing.StandardOutput.ReadToEndAsync(); existing.StandardError.ReadToEndAsync();
                    existing.WaitForExit();
                    if (existing.ExitCode == 0) return "\nThe firewall rule already exists.";
                }
                var start = new ProcessStartInfo(netsh) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                foreach (string argument in InstallerCore.FirewallAddArguments(component, application, Environment.UserName)) start.ArgumentList.Add(argument);
                using var process = Process.Start(start) ?? throw new IOException("Cannot start netsh.");
                process.WaitForExit();
                return process.ExitCode == 0 ? "\nFirewall rule added." : $"\nThe firewall rule was not added (code {process.ExitCode}).";
            }
            catch (Win32Exception) { return "\nThe firewall rule was not added because the administrator prompt was declined."; }
        }
    }
}
