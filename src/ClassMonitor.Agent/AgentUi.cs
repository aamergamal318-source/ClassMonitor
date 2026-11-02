using System.Drawing;

namespace ClassMonitor.Agent;

/// <summary>
/// Owns the always-visible tray icon and all on-screen overlays. Every overlay
/// operation is marshaled onto the UI thread. The visible tray icon is deliberate:
/// students should be able to see the agent is running.
/// </summary>
internal sealed class AgentUi : ApplicationContext, IAgentInteraction
{
    private readonly Form _pump;      // hidden window that keeps a handle for marshaling
    private readonly NotifyIcon _tray;
    private readonly ClassMonitor.Desktop.UpdateController _updates;
    private readonly ToolStripMenuItem _endSession;
    private readonly ToolStripMenuItem _enrollmentStatus;
    private LockForm? _lock;
    private BroadcastForm? _broadcast;
    private ChatForm? _chat;
    private readonly List<SessionBanner> _banners = new();
    private readonly System.Windows.Forms.Timer _bannerTimer = new() { Interval = 2000 };
    private readonly Icon _idleIcon;
    private readonly Icon _lessonIcon;
    private bool _sessionActive;
    private int _broadcastQueued;

    public event Action? EndSessionRequested;

    public AgentUi()
    {
        _pump = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            Opacity = 0,
            WindowState = FormWindowState.Minimized,
        };
        _pump.Load += (_, _) => _pump.Hide();
        _ = _pump.Handle; // force handle creation so BeginInvoke works immediately

        var menu = new ContextMenuStrip();
        menu.Items.Add($"ClassMonitor Agent — {Environment.MachineName}").Enabled = false;
        _enrollmentStatus = new ToolStripMenuItem(EnrollmentCaption()) { Enabled = false };
        menu.Items.Add(_enrollmentStatus);
        _endSession = new ToolStripMenuItem("End this session / إنهاء الجلسة") { Enabled = false };
        _endSession.Click += (_, _) => EndSessionRequested?.Invoke();
        menu.Items.Add(_endSession);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Export enrollment request… / تصدير طلب التسجيل", null, (_, _) => ExportEnrollmentRequest());
        menu.Items.Add("Import enrollment… / استيراد التسجيل", null, (_, _) => ExplainImport());
        menu.Items.Add(new ToolStripSeparator());
        var autostart = new ToolStripMenuItem("Start automatically at logon")
        {
            CheckOnClick = false,
        };
        autostart.Click += (_, _) =>
        {
            if (AgentInstaller.IsInstalled()) AgentInstaller.Uninstall();
            else AgentInstaller.Install();
            autostart.Checked = AgentInstaller.IsInstalled();
        };
        menu.Opening += (_, _) =>
        {
            autostart.Checked = AgentInstaller.IsInstalled();
            _enrollmentStatus.Text = EnrollmentCaption();
        };
        menu.Items.Add(autostart);
        menu.Items.Add("Check screen capture... / فحص الشاشة", null, (_, _) => new CaptureDiagnostics().Show());
        var updateItem = new ToolStripMenuItem("التحديثات / Updates");
        menu.Items.Add(updateItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        Icon appIcon;
        try { appIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application; }
        catch { appIcon = SystemIcons.Application; }
        _pump.Icon = appIcon;
        _idleIcon = appIcon;
        _lessonIcon = LessonIcon(appIcon);
        _bannerTimer.Tick += (_, _) => { foreach (var banner in _banners) banner.KeepOnTop(); };

        _tray = new NotifyIcon
        {
            Icon = appIcon,
            Visible = true,
            Text = IdleTrayText,
            ContextMenuStrip = menu,
        };
        _updates = new ClassMonitor.Desktop.UpdateController(ClassMonitor.Shared.Updates.UpdateComponent.Agent,
            _pump, updateItem, ExitThread,
            text => _tray.ShowBalloonTip(8000, "ClassMonitor update", text, ToolTipIcon.Info));
        _ = _updates.CheckAtStartupAsync();
    }

    private const string IdleTrayText = "ClassMonitor Agent (classroom monitoring)";

    private void Post(Action action)
    {
        if (_pump.IsHandleCreated && !_pump.IsDisposed)
            _pump.BeginInvoke(action);
    }

    /// <summary>Tells the student truthfully when a lesson, remote assistance or recording is active.</summary>
    public void SetManagedState(bool active, string teacher, string classroom, bool assisting, bool recording, bool shared) => Post(() =>
    {
        _endSession.Enabled = active;
        string text = !active ? IdleTrayText
            : $"Lesson: {teacher} / {classroom}{(assisting ? " · ASSIST" : "")}{(recording ? " · REC" : "")}{(shared ? " · SHARED" : "")}";
        _tray.Text = text.Length > 127 ? text[..127] : text;
        _tray.Icon = active ? _lessonIcon : _idleIcon;
        if (active && !_sessionActive)
            _tray.ShowBalloonTip(6000, "ClassMonitor", $"Lesson started by {teacher}. / بدأ الدرس.", ToolTipIcon.Info);
        _sessionActive = active;
        if (active && (assisting || recording || shared))
        {
            if (_banners.Count == 0)
            {
                foreach (var screen in Screen.AllScreens) _banners.Add(new SessionBanner(screen));
                _bannerTimer.Start();
            }
            foreach (var banner in _banners)
            {
                banner.SetState(assisting, recording, shared);
                if (!banner.Visible) banner.Show();
            }
        }
        else
        {
            CloseBanners();
        }
        if (!active)
        {
            // A later lesson starts with an empty conversation and no reply path to the old session.
            _chat?.Dispose();
            _chat = null;
        }
    });

    private void CloseBanners()
    {
        _bannerTimer.Stop();
        foreach (var banner in _banners) banner.CloseByProgram();
        _banners.Clear();
    }

    /// <summary>A distinct icon means a lesson is running even when no window or notice is on screen.</summary>
    private static Icon LessonIcon(Icon source)
    {
        using var bitmap = source.ToBitmap();
        using var graphics = Graphics.FromImage(bitmap);
        int size = bitmap.Width / 2;
        using var fill = new SolidBrush(Color.FromArgb(214, 40, 40));
        using var edge = new Pen(Color.White, Math.Max(1, bitmap.Width / 16f));
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.FillEllipse(fill, bitmap.Width - size - 1, bitmap.Height - size - 1, size, size);
        graphics.DrawEllipse(edge, bitmap.Width - size - 1, bitmap.Height - size - 1, size, size);
        IntPtr handle = bitmap.GetHicon();
        try { using var unowned = Icon.FromHandle(handle); return (Icon)unowned.Clone(); }
        finally { Native.DestroyIcon(handle); }
    }

    private static string EnrollmentCaption()
    {
        try
        {
            string pin = AgentEnrollment.DevicePin();
            var enrollment = ClassMonitor.Shared.Management.ManagedEnrollmentStore.Load(pin) ?? ClassMonitor.Shared.Management.LocalEnrollmentStore.Load(pin);
            return enrollment is null
                ? "Waiting for the teacher to add this computer / بانتظار إضافة المعلم"
                : $"Enrolled: {enrollment.Teacher.DisplayName} / {enrollment.ClassroomId}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException
            or System.Security.Authentication.AuthenticationException or System.Security.Cryptography.CryptographicException)
        { return "Enrollment invalid / التسجيل غير صالح"; }
    }

    private void ExportEnrollmentRequest()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Save enrollment request for the teacher",
            Filter = "Enrollment request (*.json)|*.json",
            FileName = $"{Environment.MachineName}-enrollment-request.json",
        };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        try
        {
            AgentEnrollment.WriteRequest(dialog.FileName);
            MessageBox.Show($"Give this file to the teacher.\nأعطِ هذا الملف للمعلم.\n\nDevice fingerprint:\n{AgentEnrollment.Group(AgentEnrollment.DevicePin())}",
                "ClassMonitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Security.Cryptography.CryptographicException)
        { MessageBox.Show(ex.Message, "ClassMonitor", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    /// <summary>This folder is writable by the student, so nothing here may run with administrator rights.</summary>
    private static void ExplainImport() => MessageBox.Show(
        "An administrator imports the enrollment file with the school's trusted copy of ClassMonitorSetup.exe, using its Administrator tools.\n\n" +
        "يستورد المسؤول ملف التسجيل باستخدام النسخة الموثوقة من ClassMonitorSetup.exe.",
        "ClassMonitor", MessageBoxButtons.OK, MessageBoxIcon.Information);

    public void ShowMessage(string text, int durationMs) =>
        Post(() => new MessageForm(text, durationMs).Show());

    public void SetLock(bool locked, string text) => Post(() =>
    {
        if (locked)
        {
            _lock ??= new LockForm();
            _lock.SetText(text);
            _lock.Show();
            _lock.BringToFront();
            _lock.Activate();
        }
        else
        {
            _lock?.CloseByProgram();
            _lock = null;
        }
    });

    /// <summary>At most one frame waits for the UI thread; a newer one replaces nothing and an older queue never builds up.</summary>
    public void ShowBroadcastFrame(byte[] jpeg)
    {
        if (Interlocked.CompareExchange(ref _broadcastQueued, 1, 0) != 0) return;
        Post(() =>
        {
            try
            {
                _broadcast ??= new BroadcastForm();
                if (!_broadcast.Visible)
                    _broadcast.Show();
                using var ms = new MemoryStream(jpeg);
                using var tmp = Image.FromStream(ms);
                _broadcast.SetImage(new Bitmap(tmp)); // independent copy; stream can close
            }
            catch (ArgumentException) { /* An undecodable frame is skipped. */ }
            finally { Interlocked.Exchange(ref _broadcastQueued, 0); }
        });
    }

    public void HideBroadcast() => Post(() =>
    {
        _broadcast?.Close();
        _broadcast = null;
    });

    public void ShowChat(string from, string text, Func<string, Task> sendBack) => Post(() =>
    {
        _chat ??= new ChatForm("Teacher");
        _chat.SetSender(sendBack);
        _chat.AppendIncoming(from, text);
        if (!_chat.Visible) _chat.Show();
        _chat.BringToFront();
        _chat.Activate();
    });

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _updates.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _lock?.CloseByProgram();
            _broadcast?.Close();
            CloseBanners();
            _bannerTimer.Dispose();
            _chat?.Dispose();
            _pump.Dispose();
            _lessonIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
