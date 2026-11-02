using System.Runtime.InteropServices;
using ClassMonitor.Shared.Settings;
using ClassMonitor.Teacher.Presentation;

namespace ClassMonitor.Teacher;

/// <summary>A device card with explicit enrollment/health state and no silent black placeholder.</summary>
internal sealed class AgentTile : Panel
{
    private readonly PictureBox _pic;
    private readonly Label _caption;
    private readonly Label _badge;
    private readonly Label _empty;
    private readonly ToolTip _tip = new();
    private readonly string _name;
    private bool _live, _locked, _recording, _alert, _active, _selected;
    private string _language = "en", _theme = "system", _detail = "";
    private Point _mouseDown;
    private bool _dragging;
    public string Presence { get; private set; } = "connecting";
    public event Action<bool>? Clicked;
    public event Action? OpenRequested;

    public AgentTile(AgentConnection conn, Size size) : this(conn.Host, conn.MachineName, size) { }
    public AgentTile(string host, string machineName, Size size)
    {
        _name = machineName;
        Size = size;
        Margin = new Padding(7);
        Padding = new Padding(3);
        DoubleBuffered = true;
        AccessibleRole = AccessibleRole.Grouping;
        _pic = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(35, 43, 55), Cursor = Cursors.Hand };
        _caption = new Label { Dock = DockStyle.Bottom, Height = 38, TextAlign = ContentAlignment.MiddleCenter, Text = $"{machineName} · {host}", AutoEllipsis = true };
        _badge = new Label { AutoSize = true, Location = new Point(7, 7), Font = new Font("Segoe UI", 8F, FontStyle.Bold), ForeColor = Color.White, Padding = new Padding(5, 3, 5, 3) };
        _empty = new Label { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(223, 232, 245), TextAlign = ContentAlignment.MiddleCenter, Padding = new Padding(10), BackColor = _pic.BackColor };
        Controls.Add(_pic);
        Controls.Add(_caption);
        _pic.Controls.Add(_empty);
        _pic.Controls.Add(_badge);
        foreach (Control control in new Control[] { this, _pic, _caption, _badge, _empty })
        {
            control.MouseDown += (_, e) =>
            {
                _mouseDown = PointToClient(Cursor.Position);
                _dragging = e.Button == MouseButtons.Left;
                Clicked?.Invoke((ModifierKeys & Keys.Control) == Keys.Control);
            };
            control.MouseUp += (_, _) => _dragging = false;
            control.MouseMove += (_, e) =>
            {
                if (!_dragging || e.Button != MouseButtons.Left || Tag is not string host) return;
                var current = PointToClient(Cursor.Position);
                if (Math.Abs(current.X - _mouseDown.X) < SystemInformation.DragSize.Width && Math.Abs(current.Y - _mouseDown.Y) < SystemInformation.DragSize.Height) return;
                _dragging = false;
                DoDragDrop(host, DragDropEffects.Move);
            };
            control.DoubleClick += (_, _) => OpenRequested?.Invoke();
        }
        ApplyPreferences(new SchoolSettings());
    }

    public void SetFrame(byte[] jpeg)
    {
        try
        {
            using var stream = new MemoryStream(jpeg);
            using var decoded = Image.FromStream(stream);
            var next = new Bitmap(decoded);
            var old = _pic.Image;
            _pic.Image = next;
            old?.Dispose();
            _empty.Visible = false;
            Presence = "online";
            _detail = "";
            UpdateBadge();
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or ExternalException)
        {
            Presence = "unhealthy";
            _detail = "Image unavailable";
            UpdateBadge();
        }
    }

    public void SetPresence(string state, string detail = "")
    {
        Presence = state;
        _detail = detail.Length > 256 ? detail[..256] : detail;
        if (state is "offline" or "pending")
        {
            _pic.Image?.Dispose();
            _pic.Image = null;
            _empty.Visible = true;
            _active = _recording = _locked = false;
        }
        UpdateBadge();
    }
    public void ApplyPreferences(SchoolSettings settings)
    {
        _language = settings.General.Language;
        _theme = settings.General.Theme;
        var palette = ThemePalette.For(_theme);
        _caption.BackColor = palette.Surface;
        _caption.ForeColor = palette.Text;
        _tip.SetToolTip(_pic, UiText.Text("Double-click for live view & remote control", _language));
        RightToLeft = UiText.IsRightToLeft(_language) ? RightToLeft.Yes : RightToLeft.No;
        SetSelected(_selected);
        UpdateBadge();
    }
    public void SetSelected(bool selected)
    {
        _selected = selected;
        var palette = ThemePalette.For(_theme);
        BackColor = selected ? palette.Accent : palette.Border;
    }
    public void SetLive(bool value) { _live = value; UpdateBadge(); }
    public void SetLocked(bool value) { _locked = value; UpdateBadge(); }
    public void SetRecording(bool value) { _recording = value; UpdateBadge(); }
    public void SetAlert(bool value) { _alert = value; UpdateBadge(); }
    public void SetActive(DateTime? value, int healthTimeoutSeconds = 15)
    {
        _active = value is { } at && at != DateTime.MinValue && (DateTime.UtcNow - at) <= TimeSpan.FromSeconds(healthTimeoutSeconds);
        UpdateBadge();
    }
    public void SetTileSize(Size size) => Size = size;
    private void UpdateBadge()
    {
        string status = UiText.Text(Presence switch { "online" => "Online", "offline" => "Offline", "pending" => "Pending enrollment", "unhealthy" => "Unhealthy", _ => "Connecting" }, _language);
        var tags = new List<string> { status };
        if (_alert) tags.Add(UiText.Text("Alert", _language));
        if (_recording) tags.Add(UiText.Text("Recording", _language));
        if (_locked) tags.Add(UiText.Text("Locked", _language));
        if (_live) tags.Add(UiText.Text("Live view", _language));
        _badge.Text = string.Join(" · ", tags);
        _badge.BackColor = _alert ? Color.FromArgb(151, 84, 0) : _recording ? Color.FromArgb(154, 33, 45)
            : Presence == "online" && _active ? Color.FromArgb(20, 115, 74) : Color.FromArgb(58, 77, 103);
        _badge.BringToFront();
        _empty.Text = status + (_detail.Length == 0 ? "" : "\n\n" + UiText.Text(_detail, _language));
        AccessibleName = $"{_name}: {_badge.Text}";
        _pic.AccessibleName = AccessibleName;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _pic.Image?.Dispose(); _tip.Dispose(); }
        base.Dispose(disposing);
    }
}
