using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ClassMonitor.Shared;
using ClassMonitor.Shared.Management;
using ClassMonitor.Shared.Sessions;
using ClassMonitor.Shared.Settings;
using ClassMonitor.Teacher.Presentation;

namespace ClassMonitor.Teacher.Management;

/// <summary>Reviews a student's public request, chooses exact capabilities and writes the signed document for the administrator.</summary>
internal sealed class EnrollDeviceForm : Form
{
    private static readonly (ClassroomPermission Permission, string Label, bool Default)[] Choices =
    {
        (ClassroomPermission.View, "View screens", true), (ClassroomPermission.Message, "Messages and chat", true),
        (ClassroomPermission.Present, "Present to students", true), (ClassroomPermission.Files, "Lesson files", true),
        (ClassroomPermission.Focus, "Lock and focus", true), (ClassroomPermission.Assist, "Remote assistance", true),
        (ClassroomPermission.Record, "Recording", false), (ClassroomPermission.Admin, "Power and programs (school administrator)", false),
    };

    private readonly X509Certificate2 _teacher;
    private readonly TextBox _request = new() { ReadOnly = true, Width = 460 };
    private readonly TextBox _review = new() { ReadOnly = true, Multiline = true, Width = 560, Height = 74, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox _host = new() { Width = 260, AccessibleName = "Device address" };
    private readonly NumericUpDown _port = new() { Minimum = 1024, Maximum = 65535, Value = Net.AgentTcpPort, Width = 90, AccessibleName = "Device port" };
    private readonly TextBox _school = new() { Width = 260, AccessibleName = "School" };
    private readonly TextBox _classroom = new() { Width = 260, AccessibleName = "Classroom" };
    private readonly TextBox _teacherName = new() { Width = 260, AccessibleName = "Teacher name" };
    private readonly CheckBox _schoolAdmin = new() { Text = "School administrator role", AutoSize = true };
    private readonly NumericUpDown _days = new() { Minimum = 1, Maximum = 720, Value = 180, Width = 90, AccessibleName = "Enrollment days" };
    private readonly CheckedListBox _permissions = new() { Width = 560, Height = 150, CheckOnClick = true, AccessibleName = "Permissions" };
    private DeviceEnrollmentRequest? _loaded;

    public PairedDevice? Enrolled { get; private set; }

    public EnrollDeviceForm(X509Certificate2 teacher, SchoolSettings settings)
    {
        _teacher = teacher;
        Text = "Enroll device";
        ClientSize = new Size(620, 660);
        MinimumSize = new Size(640, 700);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9.5F);
        _teacherName.Text = Environment.UserName;
        foreach (var choice in Choices) _permissions.Items.Add(choice.Label, choice.Default);
        _permissions.ItemCheck += (_, e) =>
        {
            if (Choices[e.Index].Permission == ClassroomPermission.Admin && e.NewValue == CheckState.Checked && !_schoolAdmin.Checked)
                e.NewValue = CheckState.Unchecked;
        };
        _schoolAdmin.CheckedChanged += (_, _) =>
        {
            if (!_schoolAdmin.Checked) _permissions.SetItemChecked(Array.FindIndex(Choices, c => c.Permission == ClassroomPermission.Admin), false);
        };

        var browse = new Button { Text = "Choose request…", AutoSize = true };
        browse.Click += (_, _) => ChooseRequest();
        var create = new Button { Text = "Create enrollment file…", AutoSize = true };
        create.Click += (_, _) => CreateDocument();
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        CancelButton = cancel;

        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(14) };
        var teacherPin = new TextBox { ReadOnly = true, Multiline = true, Width = 560, Height = 44, Text = Grouped(CertificateTrust.Fingerprint(teacher)), AccessibleName = "Teacher fingerprint" };
        body.Controls.AddRange(new Control[]
        {
            Caption("Teacher fingerprint (the administrator compares this on the student computer)"), teacherPin,
            Caption("Device request"), Row(_request, browse), _review,
            Caption("Device address and port"), Row(_host, _port),
            Caption("School"), _school, Caption("Classroom"), _classroom,
            Caption("Teacher name"), _teacherName, _schoolAdmin,
            Caption("Enrollment days"), _days, Caption("Permissions"), _permissions,
            Row(create, cancel),
        });
        Controls.Add(body);
        TeacherExperience.Apply(this, settings);
    }

    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 8, 3, 2) };
    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        row.Controls.AddRange(controls);
        return row;
    }
    private static string Grouped(string pin) => string.Join(' ', Enumerable.Range(0, pin.Length / 4).Select(i => pin.Substring(i * 4, 4)));

    private void ChooseRequest()
    {
        using var dialog = new OpenFileDialog { Title = "Choose the device enrollment request", Filter = "Enrollment request (*.json)|*.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            _loaded = EnrollmentWorkflow.ReadRequest(dialog.FileName);
            _request.Text = dialog.FileName;
            _review.Text = $"{_loaded.DisplayName}{Environment.NewLine}{Grouped(_loaded.CertificateSha256)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            _loaded = null;
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void CreateDocument()
    {
        if (_loaded is null) { MessageBox.Show(this, "Choose a device request first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        var permissions = ClassroomPermission.None;
        foreach (int index in _permissions.CheckedIndices) permissions |= Choices[index].Permission;
        try
        {
            var expiry = DateTimeOffset.UtcNow.AddDays((double)_days.Value);
            var (document, device) = EnrollmentWorkflow.Create(_loaded, _teacher, new EnrollmentChoice(_host.Text, (int)_port.Value,
                _school.Text.Trim(), _classroom.Text.Trim(), _teacherName.Text.Trim(),
                _schoolAdmin.Checked ? TeacherRole.SchoolAdmin : TeacherRole.Teacher, permissions, expiry));
            using var dialog = new SaveFileDialog
            {
                Title = "Save the enrollment file for the student computer",
                Filter = "Enrollment (*.json)|*.json",
                FileName = $"{_host.Text.Trim()}-enrollment.json",
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            EnrollmentWorkflow.WriteDocument(dialog.FileName, document);
            Enrolled = device;
            MessageBox.Show(this, "An administrator imports this file on the student computer. It contains pairing material: deliver it by an approved channel and delete copies afterwards.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Security.Authentication.AuthenticationException or System.Security.Cryptography.CryptographicException)
        { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
