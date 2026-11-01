using System.Drawing;

namespace ClassMonitor.Teacher;

/// <summary>Minimal single-line text prompt dialog.</summary>
internal static class Prompt
{
    public static string? Show(string title, string label, string initial = "")
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(420, 130),
            MinimizeBox = false,
            MaximizeBox = false,
        };

        var lbl = new Label { Text = label, Left = 12, Top = 12, Width = 396 };
        var box = new TextBox { Left = 12, Top = 38, Width = 396, Text = initial };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 252, Top = 78, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 333, Top = 78, Width = 75 };

        form.Controls.AddRange([lbl, box, ok, cancel]);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        return form.ShowDialog() == DialogResult.OK ? box.Text : null;
    }
}
