using System.Drawing;

namespace ClassMonitor.Teacher;

/// <summary>
/// One machine's thumbnail tile: live screenshot, name/user caption, a status badge
/// (LIVE / LOCKED), and a selection frame. Clicking selects it (Ctrl toggles); double-click
/// opens the live view.
/// </summary>
internal sealed class AgentTile : Panel
{
    private static readonly Color FrameIdle = Color.FromArgb(210, 213, 219);
    private static readonly Color FrameSelected = Color.FromArgb(0, 120, 215);

    private readonly PictureBox _pic;
    private readonly Label _caption;
    private readonly Label _badge;
    private bool _live;
    private bool _locked;
    private bool _recording;
    private bool _alert;
    private bool _active;
    private DateTime _lastActivityUtc = DateTime.MinValue;

    /// <summary>Raised on click; the argument is true when Ctrl was held (toggle selection).</summary>
    public event Action<bool>? Clicked;
    public event Action? OpenRequested;

    public AgentTile(AgentConnection conn, Size size)
    {
        Size = size;
        Margin = new Padding(6);
        Padding = new Padding(3);               // the padding band is the selection frame
        BackColor = FrameIdle;

        _pic = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Black,
            Cursor = Cursors.Hand,
        };
        _caption = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(40, 40, 46),
            Text = $"{conn.MachineName}   ·   {conn.Host}",
        };
        _badge = new Label
        {
            AutoSize = true,
            Location = new Point(6, 6),
            Font = new Font("Segoe UI", 8F, FontStyle.Bold),
            ForeColor = Color.White,
            Padding = new Padding(5, 2, 5, 2),
            Visible = false,
        };

        Controls.Add(_pic);
        Controls.Add(_caption);
        _pic.Controls.Add(_badge);

        foreach (Control c in new Control[] { this, _pic, _caption, _badge })
        {
            c.MouseDown += (_, _) => Clicked?.Invoke((ModifierKeys & Keys.Control) == Keys.Control);
            c.DoubleClick += (_, _) => OpenRequested?.Invoke();
        }

        var tip = new ToolTip();
        tip.SetToolTip(_pic, "Double-click for live view & remote control");
    }

    public void SetFrame(byte[] jpeg)
    {
        try
        {
            using var ms = new MemoryStream(jpeg);
            using var tmp = Image.FromStream(ms);
            _pic.Image?.Dispose();
            _pic.Image = new Bitmap(tmp);
        }
        catch { /* skip bad frame */ }
    }

    public void SetSelected(bool selected) =>
        BackColor = selected ? FrameSelected : FrameIdle;

    public void SetLive(bool live) { _live = live; UpdateBadge(); }
    public void SetLocked(bool locked) { _locked = locked; UpdateBadge(); }
    public void SetRecording(bool recording) { _recording = recording; UpdateBadge(); }
    public void SetAlert(bool alert) { _alert = alert; UpdateBadge(); }
    public void SetActive(DateTime? lastActivityUtc)
    {
        _lastActivityUtc = lastActivityUtc ?? DateTime.MinValue;
        _active = _lastActivityUtc != DateTime.MinValue && (DateTime.UtcNow - _lastActivityUtc) <= TimeSpan.FromSeconds(15);
        UpdateBadge();
    }

    public void SetTileSize(Size size) => Size = size;

    private void UpdateBadge()
    {
        var tags = new List<string>(5);
        if (_alert) tags.Add("⚠ ALERT");
        if (_recording) tags.Add("● REC");
        if (_active) tags.Add("ACTIVE");
        if (_locked) tags.Add("LOCKED");
        if (_live) tags.Add("LIVE");

        if (tags.Count == 0) { _badge.Visible = false; return; }

        _badge.Text = string.Join("  ·  ", tags);
        _badge.BackColor = _alert ? Color.FromArgb(205, 110, 0)       // alert wins visually
                         : _recording ? Color.FromArgb(170, 30, 30)
                         : _locked ? Color.FromArgb(200, 60, 60)
                         : _active ? Color.FromArgb(22, 125, 58)
                         : Color.FromArgb(0, 150, 70);
        _badge.Visible = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _pic.Image?.Dispose();
        base.Dispose(disposing);
    }
}
