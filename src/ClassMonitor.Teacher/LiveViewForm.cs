using System.Drawing;
using ClassMonitor.Shared;

namespace ClassMonitor.Teacher;

/// <summary>
/// Full-size live view of one agent with optional remote control. Mouse and keyboard
/// events are translated to normalized coordinates and forwarded to the agent.
/// </summary>
internal sealed class LiveViewForm : Form
{
    private readonly AgentConnection _conn;
    private readonly PictureBox _pic;
    private readonly ToolStripButton _controlBtn;
    private readonly ToolStripComboBox _monitorCombo;
    private Size _frameSize = new(1, 1);
    private readonly ClassMonitor.Teacher.Presentation.LatestFrameDispatcher _frames;
    private bool _control, _applyingControl;
    private int _monitorIndex = -1;

    public LiveViewForm(AgentConnection conn)
    {
        _conn = conn;
        Text = $"Live — {conn.MachineName} ({conn.UserName})  [{conn.Host}]";
        ClientSize = new Size(1000, 640);
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;

        var tools = new ToolStrip();
        _controlBtn = new ToolStripButton("Take control") { CheckOnClick = true };
        _controlBtn.CheckedChanged += async (_, _) =>
        {
            if (_applyingControl) return;
            bool requested = _controlBtn.Checked;
            // Input flows only after the agent has confirmed it is showing the assistance state.
            bool ok = await _conn.SetAssistanceAsync(requested);
            SetControlState(requested && ok);
        };
        tools.Items.Add(_controlBtn);
        tools.Items.Add(new ToolStripSeparator());
        tools.Items.Add(new ToolStripLabel("Monitor:"));
        _monitorCombo = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
        _monitorCombo.SelectedIndexChanged += (_, _) => OnMonitorChanged();
        tools.Items.Add(_monitorCombo);
        var diagnostics = new ToolStripButton("Copy diagnostics / نسخ التشخيص");
        diagnostics.Click += (_, _) => Clipboard.SetText(BuildDiagnostics());
        tools.Items.Add(diagnostics);

        _pic = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
        _pic.MouseMove += OnMouseMove;
        _pic.MouseDown += OnMouseDown;
        _pic.MouseUp += OnMouseUp;
        _pic.MouseWheel += OnMouseWheel;

        Controls.Add(_pic);
        Controls.Add(tools);

        KeyDown += (_, e) => { if (_control) { _ = _conn.RemoteInputAsync(new RemoteInputCmd("keydown", VKey: e.KeyValue)); e.SuppressKeyPress = true; } };
        KeyUp += (_, e) => { if (_control) { _ = _conn.RemoteInputAsync(new RemoteInputCmd("keyup", VKey: e.KeyValue)); e.SuppressKeyPress = true; } };

        _frames = new ClassMonitor.Teacher.Presentation.LatestFrameDispatcher(this, ShowFrame);
        _conn.FrameReceived += OnFrame;
        _conn.MonitorsReceived += OnMonitors;
        Load += async (_, _) => { PopulateMonitors(); await RequestAsync(); };
    }

    private void SetControlState(bool active)
    {
        _control = active;
        _applyingControl = true;
        _controlBtn.Checked = active;
        _applyingControl = false;
        _controlBtn.Text = active ? "Release control" : "Take control";
    }

    /// <summary>Stops local input when the lesson or permission ended; the agent has already refused it.</summary>
    public void ReleaseAssistance() => SetControlState(false);

    private void OnMonitors(AgentConnection c)
    {
        if (c != _conn || !IsHandleCreated) return;
        BeginInvoke(PopulateMonitors);
    }

    private void PopulateMonitors()
    {
        if (_monitorCombo.Items.Count == _conn.Monitors.Length + 1) return; // already populated
        _monitorCombo.Items.Clear();
        _monitorCombo.Items.Add("All monitors");
        for (int i = 0; i < _conn.Monitors.Length; i++)
        {
            var m = _conn.Monitors[i];
            _monitorCombo.Items.Add($"Monitor {i + 1} ({m.Width}×{m.Height}){(m.Primary ? " *" : "")}");
        }
        _monitorCombo.SelectedIndex = 0;
    }

    private async void OnMonitorChanged()
    {
        _monitorIndex = _monitorCombo.SelectedIndex <= 0 ? -1 : _monitorCombo.SelectedIndex - 1;
        await RequestAsync();
    }

    private Task RequestAsync() =>
        _conn.RequestScreenAsync(maxWidth: 1280, quality: 60, intervalMs: 200, monitorIndex: _monitorIndex);

    internal string BuildDiagnostics()
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine("ClassMonitor teacher receive diagnostic v1");
        text.AppendLine($"Frames received: {_conn.FramesReceived}; monitor selected: {_monitorIndex}");
        text.AppendLine($"Last frame UTC: {_conn.LastFrameAt:O}; JPEG bytes: {_conn.LastFrame?.Length ?? 0}");
        text.AppendLine($"Last receive error: {_conn.LastReadError}");
        text.AppendLine($"Monitors reported: {_conn.Monitors.Length}");
        byte[]? jpeg = _conn.LastFrame;
        if (jpeg is not null)
        {
            try
            {
                using var stream = new MemoryStream(jpeg);
                using var bitmap = new Bitmap(stream);
                int dark = 0, total = 0;
                for (int y = 0; y < bitmap.Height; y += 4)
                for (int x = 0; x < bitmap.Width; x += 4)
                {
                    var c = bitmap.GetPixel(x, y);
                    total++;
                    if (c.R < 16 && c.G < 16 && c.B < 16) dark++;
                }
                text.AppendLine($"Decoded image: {bitmap.Width}x{bitmap.Height}; near-black pixels: {100.0 * dark / Math.Max(1, total):F1}%");
                text.AppendLine($"Image assigned to view: {_pic.Image is not null}");
            }
            catch (Exception ex) { text.AppendLine($"JPEG decoding FAILED: {ex.GetType().Name}: {ex.Message}"); }
        }
        return text.ToString();
    }

    private void OnFrame(AgentConnection c, byte[] jpeg)
    {
        if (c != _conn || IsDisposed) return;
        _frames.Submit(jpeg);
    }

    private void ShowFrame(byte[] jpeg)
    {
        try
        {
            using var ms = new MemoryStream(jpeg);
            using var tmp = Image.FromStream(ms);
            _frameSize = tmp.Size;
            _pic.Image?.Dispose();
            _pic.Image = new Bitmap(tmp);
        }
        catch { /* skip bad frame */ }
    }

    /// <summary>Map a point in the PictureBox to normalized (0..1) image coordinates (Zoom letterbox).</summary>
    private bool MapToNormalized(Point p, out double nx, out double ny)
    {
        nx = ny = 0;
        int cw = _pic.ClientSize.Width, ch = _pic.ClientSize.Height;
        int iw = _frameSize.Width, ih = _frameSize.Height;
        if (cw <= 0 || ch <= 0 || iw <= 0 || ih <= 0) return false;

        double scale = Math.Min((double)cw / iw, (double)ch / ih);
        double dispW = iw * scale, dispH = ih * scale;
        double offX = (cw - dispW) / 2, offY = (ch - dispH) / 2;
        if (p.X < offX || p.X > offX + dispW || p.Y < offY || p.Y > offY + dispH) return false;

        nx = (p.X - offX) / dispW;
        ny = (p.Y - offY) / dispH;
        return true;
    }

    private static int ButtonCode(MouseButtons b) => b switch
    {
        MouseButtons.Right => 1,
        MouseButtons.Middle => 2,
        _ => 0,
    };

    private void OnMouseMove(object? s, MouseEventArgs e)
    {
        if (_control && MapToNormalized(e.Location, out var nx, out var ny))
            _ = _conn.RemoteInputAsync(new RemoteInputCmd("mousemove", nx, ny));
    }

    private void OnMouseDown(object? s, MouseEventArgs e)
    {
        if (_control && MapToNormalized(e.Location, out var nx, out var ny))
            _ = _conn.RemoteInputAsync(new RemoteInputCmd("mousedown", nx, ny, ButtonCode(e.Button)));
    }

    private void OnMouseUp(object? s, MouseEventArgs e)
    {
        if (_control)
            _ = _conn.RemoteInputAsync(new RemoteInputCmd("mouseup", Button: ButtonCode(e.Button)));
    }

    private void OnMouseWheel(object? s, MouseEventArgs e)
    {
        if (_control)
            _ = _conn.RemoteInputAsync(new RemoteInputCmd("scroll", Delta: e.Delta));
    }

    protected override async void OnFormClosed(FormClosedEventArgs e)
    {
        _conn.FrameReceived -= OnFrame;
        _conn.MonitorsReceived -= OnMonitors;
        _frames.Dispose();
        _pic.Image?.Dispose();
        try
        {
            if (_control) await _conn.SetAssistanceAsync(false);
            // Revert to the gentle thumbnail stream for the grid.
            await _conn.RequestScreenAsync(maxWidth: 400, quality: 45, intervalMs: 1000);
        }
        catch { }
        base.OnFormClosed(e);
    }
}
