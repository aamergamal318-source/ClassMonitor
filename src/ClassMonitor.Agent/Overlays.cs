using System.Drawing;

namespace ClassMonitor.Agent;

/// <summary>Full-screen black cover shown when the teacher locks this machine.</summary>
internal sealed class LockForm : Form
{
    private readonly Label _label;
    private bool _allowClose;

    public LockForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Black;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen;
        KeyPreview = true;

        _label = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 28F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "Screen locked by the teacher.",
        };
        Controls.Add(_label);
    }

    public void SetText(string text) => _label.Text = string.IsNullOrWhiteSpace(text)
        ? "Screen locked by the teacher."
        : text;

    public void CloseByProgram()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Don't let the student Alt+F4 their way out; only programmatic unlock closes it.
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
            e.Cancel = true;
        else
            base.OnFormClosing(e);
    }
}

/// <summary>Full-screen view of the teacher's broadcast screen.</summary>
internal sealed class BroadcastForm : Form
{
    private readonly PictureBox _pic;

    public BroadcastForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Black;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen;

        _pic = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom };
        Controls.Add(_pic);
    }

    public void SetImage(Image img)
    {
        _pic.Image?.Dispose();
        _pic.Image = img;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _pic.Image?.Dispose();
        base.OnFormClosed(e);
    }
}

/// <summary>Two-way chat window shown to the student. Hides (not closes) so it persists for the session.</summary>
internal sealed class ChatForm : Form
{
    private readonly TextBox _history;
    private readonly TextBox _input;
    private Func<string, Task>? _sender;

    public ChatForm(string withWhom)
    {
        Text = $"Chat — {withWhom}";
        ClientSize = new Size(400, 440);
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = true;
        ShowInTaskbar = true;

        _history = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.White,
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 10F),
        };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 76, Padding = new Padding(6) };
        var send = new Button { Dock = DockStyle.Right, Width = 84, Text = "Send" };
        _input = new TextBox { Dock = DockStyle.Fill, Multiline = true, Font = new Font("Segoe UI", 10F) };
        send.Click += (_, _) => DoSend();
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; DoSend(); }
        };
        bottom.Controls.Add(send);
        bottom.Controls.Add(_input);

        Controls.Add(bottom);
        Controls.Add(_history);
    }

    public void SetSender(Func<string, Task> sender) => _sender = sender;

    public void AppendIncoming(string from, string text) => Append($"{from}: {text}");

    private void DoSend()
    {
        var t = _input.Text.Trim();
        if (t.Length == 0) return;
        Append($"Me: {t}");
        _input.Clear();
        _ = _sender?.Invoke(t);
    }

    private void Append(string line) => _history.AppendText(line + Environment.NewLine);

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        else base.OnFormClosing(e);
    }
}

/// <summary>A banner shown at the top of the screen with a teacher message; auto-dismisses.</summary>
internal sealed class MessageForm : Form
{
    private readonly System.Windows.Forms.Timer _timer = new();

    public MessageForm(string text, int durationMs)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(30, 30, 46);
        StartPosition = FormStartPosition.Manual;

        var label = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 16F, FontStyle.Regular),
            TextAlign = ContentAlignment.MiddleCenter,
            Padding = new Padding(24),
            Text = text,
        };
        Controls.Add(label);

        var work = Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.VirtualScreen;
        Width = Math.Min(900, work.Width - 80);
        Height = 120;
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top + 40;

        _timer.Interval = Math.Max(1000, durationMs);
        _timer.Tick += (_, _) => Close();
        _timer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Dispose();
        base.OnFormClosed(e);
    }
}
