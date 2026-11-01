using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text;

namespace ClassMonitor.Agent;

/// <summary>Local, user-invoked capture check. Does not start a server or change settings.</summary>
internal sealed class CaptureDiagnostics : Form
{
    private readonly PictureBox _preview = new()
    {
        Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.DimGray
    };
    private readonly TextBox _report = new()
    {
        Dock = DockStyle.Bottom, Height = 200, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical
    };
    private readonly ComboBox _monitor = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    private readonly Button _check = new() { Text = "Check again", AutoSize = true };

    public CaptureDiagnostics()
    {
        Text = "ClassMonitor — Student screen diagnostics / فحص شاشة الطالب";
        ClientSize = new Size(900, 700);
        StartPosition = FormStartPosition.CenterScreen;
        var instructions = new Label
        {
            Dock = DockStyle.Top, Height = 55,
            Text = "شغّل الفحص على جهاز الطالب. هل المعاينة تعرض سطح المكتب أم صورة سوداء؟\n" +
                   "Run on the STUDENT computer. Copy the report below. The preview stays on this computer."
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40 };
        _monitor.Items.Add("All monitors");
        foreach (var screen in Screen.AllScreens)
            _monitor.Items.Add($"Monitor {_monitor.Items.Count}: {screen.Bounds.Width}x{screen.Bounds.Height}{(screen.Primary ? " (primary)" : "")}");
        _monitor.SelectedIndex = 0;
        var copy = new Button { Text = "Copy report / نسخ التقرير", AutoSize = true };
        copy.Click += (_, _) => { if (_report.TextLength > 0) Clipboard.SetText(_report.Text); };
        _check.Click += async (_, _) => await CheckAsync();
        buttons.Controls.AddRange(new Control[] { _monitor, _check, copy });
        Controls.Add(_preview);
        Controls.Add(_report);
        Controls.Add(buttons);
        Controls.Add(instructions);
        Shown += async (_, _) => await CheckAsync();
    }

    private async Task CheckAsync()
    {
        _check.Enabled = false;
        _monitor.Enabled = false;
        int index = _monitor.SelectedIndex - 1;
        var text = new StringBuilder();
        text.AppendLine("ClassMonitor student capture diagnostic v1");
        text.AppendLine($"Windows: {Environment.OSVersion.VersionString}; 64-bit: {Environment.Is64BitProcess}");
        text.AppendLine($"Interactive: {Environment.UserInteractive}; session: {Process.GetCurrentProcess().SessionId}");
        text.AppendLine($"Virtual desktop: {SystemInformation.VirtualScreen}; selected monitor: {index}");
        foreach (var s in Screen.AllScreens) text.AppendLine($"Monitor: {s.Bounds}; primary: {s.Primary}");
        try
        {
            // Hide the diagnostic window briefly so the captured frame shows the desktop,
            // rather than a recursive preview of the diagnostic window itself.
            Hide();
            await Task.Delay(350);
            var result = await Task.Run(() => Collect(index));
            _preview.Image?.Dispose();
            _preview.Image = result.Preview;
            text.Append(result.Report);
        }
        catch (Exception ex)
        {
            text.AppendLine($"FAILED: {ex.GetType().Name}; HRESULT=0x{ex.HResult:X8}; {ex.Message}");
        }
        finally
        {
            _report.Text = text.ToString();
            Show();
            Activate();
            _check.Enabled = true;
            _monitor.Enabled = true;
        }
    }

    internal static (Bitmap Preview, string Report) Collect(int monitorIndex)
    {
        var bounds = monitorIndex >= 0 ? Screen.AllScreens[monitorIndex].Bounds : ScreenCapture.VirtualBounds;
        var report = new StringBuilder();
        // Probe the Windows operation separately: the normal stream substitutes gray
        // on errors, so successful JPEG decoding alone cannot establish capture success.
        try
        {
            using var raw = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
            using var graphics = Graphics.FromImage(raw);
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            report.AppendLine("Windows CopyFromScreen: OK");
        }
        catch (Exception ex)
        {
            report.AppendLine($"Windows CopyFromScreen: FAILED ({ex.GetType().Name}, HRESULT=0x{ex.HResult:X8}, {ex.Message})");
        }
        byte[] jpeg = ScreenCapture.CaptureJpeg(1280, 75, monitorIndex);
        using var stream = new MemoryStream(jpeg);
        using var decoded = new Bitmap(stream);
        var preview = new Bitmap(decoded);
        int dark = 0, total = 0;
        for (int y = 0; y < decoded.Height; y += 4)
        for (int x = 0; x < decoded.Width; x += 4)
        {
            var c = decoded.GetPixel(x, y);
            total++;
            if (c.R < 16 && c.G < 16 && c.B < 16) dark++;
        }
        report.AppendLine($"Agent JPEG: {decoded.Width}x{decoded.Height}; {jpeg.Length} bytes");
        report.AppendLine($"Near-black pixels: {100.0 * dark / Math.Max(1, total):F1}%");
        report.AppendLine(dark >= total * 0.98
            ? "WARNING: the captured image is almost entirely black. Check the selected monitor, session and graphics driver."
            : "The captured image contains visible content. Compare this local preview with the teacher view.");
        report.AppendLine("No passwords, computer names, window titles or screenshot data are included in this text report.");
        return (preview, report.ToString());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _preview.Image?.Dispose();
        base.Dispose(disposing);
    }
}
