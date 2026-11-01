using System.Drawing;
using System.Text;

namespace ClassMonitor.Teacher;

/// <summary>A snapshot of accumulated per-machine application usage, with CSV export.</summary>
internal sealed class UsageReportForm : Form
{
    private readonly List<(string Machine, string App, int Seconds)> _rows;

    public UsageReportForm(List<(string Machine, string App, int Seconds)> rows)
    {
        _rows = rows;
        Text = "Usage report";
        ClientSize = new Size(700, 440);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9F);

        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
        list.Columns.Add("Machine", 170);
        list.Columns.Add("Application", 350);
        list.Columns.Add("Active time", 150);
        foreach (var r in rows)
        {
            var item = new ListViewItem(r.Machine);
            item.SubItems.Add(r.App);
            item.SubItems.Add(FormatDuration(r.Seconds));
            list.Items.Add(item);
        }

        var export = new Button { Dock = DockStyle.Bottom, Height = 32, Text = "Export CSV…" };
        export.Click += (_, _) => ExportCsv();

        Controls.Add(list);
        Controls.Add(export);

        if (rows.Count == 0)
            Controls.Add(new Label { Dock = DockStyle.Top, Height = 28, Text = "  No activity recorded yet.", ForeColor = Color.Gray });
    }

    private static string FormatDuration(int seconds) =>
        seconds >= 60 ? $"{seconds / 60} min {seconds % 60} s" : $"{seconds} s";

    private void ExportCsv()
    {
        using var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"usage_{DateTime.Now:yyyyMMdd_HHmm}.csv" };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        var sb = new StringBuilder("Machine,Application,Seconds\n");
        foreach (var r in _rows)
            sb.AppendLine($"\"{r.Machine}\",\"{r.App}\",{r.Seconds}");
        try { File.WriteAllText(dlg.FileName, sb.ToString()); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Export", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
