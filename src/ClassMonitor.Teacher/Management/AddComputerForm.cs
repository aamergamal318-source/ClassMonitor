using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using ClassMonitor.Shared;
using ClassMonitor.Shared.Management;
using ClassMonitor.Shared.Sessions;
using ClassMonitor.Shared.Settings;
using ClassMonitor.Teacher.Presentation;

namespace ClassMonitor.Teacher.Management;

/// <summary>Adds one computer using its address and the password chosen when its student agent was installed.</summary>
public static class AddComputer
{
    public const ClassroomPermission Granted = ClassroomPermission.View | ClassroomPermission.Message | ClassroomPermission.Present
        | ClassroomPermission.Files | ClassroomPermission.Focus | ClassroomPermission.Assist | ClassroomPermission.Record;

    public static async Task<PairedDevice> RunAsync(string host, string password, string classroom, X509Certificate2 teacher,
        SchoolSettings settings, int pairingPort = PasswordPairing.Port, CancellationToken ct = default)
    {
        PasswordPairing.RequireAcceptable(password);
        host = host.Trim();
        using var client = new TcpClient { NoDelay = true };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.Network.ConnectTimeoutSeconds + 30));
        await client.ConnectAsync(host, pairingPort, deadline.Token);
        await using var tls = await SecureChannel.ConnectForPairingAsync(client, teacher, TimeSpan.FromSeconds(settings.Network.HandshakeTimeoutSeconds), deadline.Token);
        string teacherPin = CertificateTrust.Fingerprint(teacher);
        return await PasswordPairing.TeacherAsync(tls, password, teacherPin, SecureChannel.RemotePin(tls), (request, agentPort) =>
        {
            var expires = DateTimeOffset.UtcNow.AddDays(365);
            using var device = CertificateTrust.ReadPublicCertificate(request.PublicCertificateBase64);
            var latest = new DateTimeOffset(Min(device.NotAfter, teacher.NotAfter).ToUniversalTime()).AddMinutes(-1);
            if (latest < expires) expires = latest;
            return EnrollmentWorkflow.Create(request, teacher, new EnrollmentChoice(host, agentPort, "School", classroom.Trim(),
                Environment.UserName, TeacherRole.Teacher, Granted, expires));
        }, deadline.Token);
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}

internal sealed class AddComputerForm : Form
{
    private readonly X509Certificate2 _teacher;
    private readonly SchoolSettings _settings;
    private readonly TextBox _host = new() { Width = 360, AccessibleName = "Computer name or IP address" };
    private readonly TextBox _password = new() { Width = 360, UseSystemPasswordChar = true, AccessibleName = "Computer password" };
    private readonly TextBox _classroom = new() { Width = 360, AccessibleName = "Classroom", Text = "Classroom 1" };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(380, 0), Margin = new Padding(3, 10, 3, 3) };
    private readonly Button _add = new() { Text = "Add computer", AutoSize = true };

    public PairedDevice? Added { get; private set; }

    public AddComputerForm(X509Certificate2 teacher, SchoolSettings settings, string? classroom = null)
    {
        _teacher = teacher;
        _settings = settings;
        Text = "Add computer";
        ClientSize = new Size(420, 320);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9.5F);
        if (!string.IsNullOrWhiteSpace(classroom)) _classroom.Text = classroom;
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        CancelButton = cancel;
        AcceptButton = _add;
        _add.Click += async (_, _) => await AddAsync();
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.AddRange(new Control[] { _add, cancel });
        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(14) };
        body.Controls.AddRange(new Control[]
        {
            Caption("Computer name or IP address"), _host, Caption("Computer password"), _password, Caption("Classroom"), _classroom, buttons, _status,
        });
        Controls.Add(body);
        TeacherExperience.Apply(this, settings);
    }

    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 8, 3, 2) };

    private async Task AddAsync()
    {
        string language = _settings.General.Language;
        if (string.IsNullOrWhiteSpace(_host.Text) || string.IsNullOrWhiteSpace(_classroom.Text) || _password.Text.Length < PasswordPairing.MinimumLength)
        {
            _status.Text = UiText.Text("Enter the computer, the classroom and its password.", language);
            return;
        }
        _add.Enabled = false;
        _status.Text = UiText.Text("Adding computer…", language);
        try
        {
            Added = await AddComputer.RunAsync(_host.Text, _password.Text, _classroom.Text, _teacher, _settings);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (PairingRefusedException ex)
        {
            _status.Text = UiText.Text(ex.Reason switch
            {
                "wrong-password" => "The password is not correct.",
                "locked" => "Too many wrong passwords. Try again in 15 minutes.",
                _ => "The computer refused this request.",
            }, language);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            _status.Text = UiText.Text("The computer could not be reached. Check the address, and that ClassMonitor is running there and allowed through its firewall.", language);
        }
        catch (Exception ex) when (ex is AuthenticationException or InvalidDataException or System.Security.Cryptography.CryptographicException or FormatException or System.Text.Json.JsonException)
        {
            _status.Text = UiText.Text("This computer could not be verified, so it was not added.", language);
        }
        finally { _add.Enabled = true; _password.Clear(); }
    }
}
