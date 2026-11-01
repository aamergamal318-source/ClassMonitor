using System.Drawing;

namespace ClassMonitor.Agent;

/// <summary>
/// Owns the always-visible tray icon and all on-screen overlays. Every overlay
/// operation is marshaled onto the UI thread. The visible tray icon is deliberate:
/// students should be able to see the agent is running.
/// </summary>
internal sealed class AgentUi : ApplicationContext
{
    private readonly Form _pump;      // hidden window that keeps a handle for marshaling
    private readonly NotifyIcon _tray;
    private LockForm? _lock;
    private BroadcastForm? _broadcast;
    private ChatForm? _chat;

    public AgentUi(bool trayVisible = true)
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
        menu.Opening += (_, _) => autostart.Checked = AgentInstaller.IsInstalled();
        menu.Items.Add(autostart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        Icon appIcon;
        try { appIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application; }
        catch { appIcon = SystemIcons.Application; }
        _pump.Icon = appIcon;

        _tray = new NotifyIcon
        {
            Icon = appIcon,
            Visible = trayVisible,
            Text = "ClassMonitor Agent (classroom monitoring)",
            ContextMenuStrip = menu,
        };
    }

    private void Post(Action action)
    {
        if (_pump.IsHandleCreated && !_pump.IsDisposed)
            _pump.BeginInvoke(action);
    }

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

    public void ShowBroadcastFrame(byte[] jpeg) => Post(() =>
    {
        _broadcast ??= new BroadcastForm();
        if (!_broadcast.Visible)
            _broadcast.Show();
        using var ms = new MemoryStream(jpeg);
        using var tmp = Image.FromStream(ms);
        _broadcast.SetImage(new Bitmap(tmp)); // independent copy; stream can close
    });

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
            _tray.Visible = false;
            _tray.Dispose();
            _lock?.CloseByProgram();
            _broadcast?.Close();
            _chat?.Dispose();
            _pump.Dispose();
        }
        base.Dispose(disposing);
    }
}
