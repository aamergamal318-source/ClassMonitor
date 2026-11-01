using System.Drawing;

namespace ClassMonitor.Teacher;

/// <summary>Running log of keyword alerts (a flagged foreground window/app on a student machine).</summary>
internal sealed class AlertsForm : Form
{
    private readonly ListView _list;

    public AlertsForm()
    {
        Text = "Keyword alerts";
        ClientSize = new Size(660, 380);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9F);

        _list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
        _list.Columns.Add("Time", 110);
        _list.Columns.Add("Machine", 150);
        _list.Columns.Add("Window / application", 380);
        Controls.Add(_list);
    }

    public void Add(DateTime when, string machine, string text)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() =>
        {
            var item = new ListViewItem(when.ToString("HH:mm:ss"));
            item.SubItems.Add(machine);
            item.SubItems.Add(text);
            item.ForeColor = Color.FromArgb(170, 70, 0);
            _list.Items.Insert(0, item);
        });
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        else base.OnFormClosing(e);
    }
}
