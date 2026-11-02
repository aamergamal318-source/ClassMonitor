namespace ClassMonitor.Agent;

/// <summary>One per monitor while the teacher controls, records or shares this screen. The student cannot close it.</summary>
internal sealed class SessionBanner : Form
{
    private bool _closing;
    private readonly Label _label = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Color.White,
        Font = new Font("Segoe UI", 10F, FontStyle.Bold),
    };

    public SessionBanner(Screen screen)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        var area = screen.WorkingArea;
        int width = Math.Min(620, area.Width);
        Bounds = new Rectangle(area.Left + (area.Width - width) / 2, area.Top, width, 30);
        Controls.Add(_label);
    }

    protected override bool ShowWithoutActivation => true;

    public void SetState(bool assisting, bool recording, bool shared)
    {
        BackColor = recording ? Color.FromArgb(154, 33, 45) : Color.FromArgb(151, 84, 0);
        var parts = new List<string>();
        if (assisting) parts.Add("Remote control / تحكم عن بعد");
        if (recording) parts.Add("Recording / تسجيل");
        if (shared) parts.Add("Shown to the class / معروضة للصف");
        _label.Text = string.Join(" · ", parts);
    }

    /// <summary>Stays above the lock and presentation windows, which are also topmost.</summary>
    public void KeepOnTop()
    {
        if (!Visible) Show();
        Native.SetWindowPos(Handle, Native.HwndTopmost, 0, 0, 0, 0, Native.SwpNoMove | Native.SwpNoSize | Native.SwpNoActivate);
    }

    public void CloseByProgram() { _closing = true; Close(); }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_closing && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
        base.OnFormClosing(e);
    }
}
