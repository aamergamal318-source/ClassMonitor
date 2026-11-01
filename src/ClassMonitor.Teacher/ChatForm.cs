using System.Drawing;
using ClassMonitor.Shared;

namespace ClassMonitor.Teacher;

/// <summary>Teacher-side chat window for one student. Subscribes to that agent's chat events.</summary>
internal sealed class ChatForm : Form
{
    private readonly AgentConnection _conn;
    private readonly TextBox _history;
    private readonly TextBox _input;

    public ChatForm(AgentConnection conn)
    {
        _conn = conn;
        Text = $"Chat — {conn.MachineName} ({conn.UserName})";
        ClientSize = new Size(420, 460);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9F);

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

        _conn.ChatReceived += OnChat;
    }

    private void OnChat(AgentConnection c, ChatMsg msg)
    {
        if (c != _conn || !IsHandleCreated) return;
        BeginInvoke(() => Append($"{c.MachineName}: {msg.Text}"));
    }

    private void DoSend()
    {
        var t = _input.Text.Trim();
        if (t.Length == 0) return;
        Append($"Me: {t}");
        _input.Clear();
        _ = _conn.SendChatAsync(t);
    }

    private void Append(string line) => _history.AppendText(line + Environment.NewLine);

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _conn.ChatReceived -= OnChat;
        base.OnFormClosed(e);
    }
}
