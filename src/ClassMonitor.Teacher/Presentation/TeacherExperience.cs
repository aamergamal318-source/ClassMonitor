using System.Runtime.CompilerServices;
using ClassMonitor.Shared.Settings;
using Microsoft.Win32;

namespace ClassMonitor.Teacher.Presentation;

public sealed record ThemePalette(Color Background, Color Surface, Color Text, Color Muted, Color Accent, Color Border)
{
    public static ThemePalette For(string theme)
    {
        if (theme is not ("system" or "light" or "dark")) throw new ArgumentOutOfRangeException(nameof(theme));
        if (SystemInformation.HighContrast)
            return new(SystemColors.Window, SystemColors.Window, SystemColors.WindowText, SystemColors.GrayText, SystemColors.Highlight, SystemColors.ActiveBorder);
        bool dark = theme == "dark";
        if (theme == "system")
        {
            try { dark = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0; }
            catch { dark = false; }
        }
        return dark
            ? new(Color.FromArgb(22, 26, 34), Color.FromArgb(32, 38, 49), Color.FromArgb(239, 243, 249), Color.FromArgb(174, 186, 202), Color.FromArgb(101, 179, 255), Color.FromArgb(61, 72, 89))
            : new(Color.FromArgb(241, 245, 250), Color.White, Color.FromArgb(27, 42, 65), Color.FromArgb(83, 101, 124), Color.FromArgb(17, 105, 193), Color.FromArgb(207, 218, 231));
    }
}

/// <summary>Apply explicitly when a form opens or preferences change. Does not open windows or alter enrollment.</summary>
public static class TeacherExperience
{
    private sealed record Original(string Value);
    private static readonly ConditionalWeakTable<Control, Original> ControlText = new();
    private static readonly ConditionalWeakTable<ToolStripItem, Original> ItemText = new();
    private static readonly ConditionalWeakTable<ToolStripItem, OwnedIcon> Icons = new();
    private static readonly ConditionalWeakTable<ToolStripComboBox, ComboPresentation> Combos = new();
    private sealed class ComboPresentation
    {
        public string Language { get; set; } = "en";
        public ThemePalette Palette { get; set; } = ThemePalette.For("light");
    }
    private sealed class OwnedIcon
    {
        public Image? Image { get; set; }
    }

    public static void Apply(Control root, SchoolSettings settings)
    {
        settings.EnsureValid();
        var palette = ThemePalette.For(settings.General.Theme);
        string language = settings.General.Language;
        bool rtl = UiText.IsRightToLeft(language);
        ApplyControl(root, false);
        if (root is Form form) { form.RightToLeftLayout = rtl; form.AutoScaleMode = AutoScaleMode.Dpi; }

        void ApplyControl(Control control, bool preserveColors)
        {
            preserveColors |= control is ClassMonitor.Teacher.AgentTile;
            control.RightToLeft = rtl ? RightToLeft.Yes : RightToLeft.No;
            var source = ControlText.GetValue(control, c => new Original(c.Text));
            string translated = UiText.Text(source.Value, language);
            // Unknown/dynamic captions belong to their workflow; do not overwrite changing device/status text.
            if (UiText.IsKnown(source.Value)) control.Text = translated;
            if (!preserveColors && control is not PictureBox && control is not ProgressBar)
            {
                control.BackColor = control is Form or FlowLayoutPanel or SplitContainer ? palette.Background : palette.Surface;
                control.ForeColor = palette.Text;
            }
            if ((UiText.IsKnown(source.Value) || string.IsNullOrWhiteSpace(control.AccessibleName)) && control.Text.Length > 0) control.AccessibleName = control.Text;
            if (control is Button button) { button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderColor = palette.Border; button.MinimumSize = new Size(72, 30); }
            if (control is ToolStrip strip)
            {
                strip.Renderer = new ToolStripProfessionalRenderer(new PaletteTable(palette));
                foreach (ToolStripItem item in strip.Items) ApplyItem(item);
            }
            foreach (Control child in control.Controls) ApplyControl(child, preserveColors);
        }
        void ApplyItem(ToolStripItem item)
        {
            var source = ItemText.GetValue(item, i => new Original(i.Text ?? ""));
            string translated = UiText.Text(source.Value, language);
            if (UiText.IsKnown(source.Value) && item is not ToolStripComboBox) item.Text = translated;
            if (item is ToolStripComboBox combo)
            {
                var state = Combos.GetValue(combo, key =>
                {
                    var presentation = new ComboPresentation();
                    key.ComboBox.DrawMode = DrawMode.OwnerDrawFixed;
                    key.ComboBox.DrawItem += (_, e) =>
                    {
                        string original = e.Index >= 0 && e.Index < key.Items.Count ? key.Items[e.Index]?.ToString() ?? "" : key.Text ?? "";
                        bool selected = (e.State & DrawItemState.Selected) != 0;
                        using var background = new SolidBrush(selected ? presentation.Palette.Accent : presentation.Palette.Surface);
                        e.Graphics.FillRectangle(background, e.Bounds);
                        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
                        flags |= UiText.IsRightToLeft(presentation.Language) ? TextFormatFlags.RightToLeft | TextFormatFlags.Right : TextFormatFlags.Left;
                        TextRenderer.DrawText(e.Graphics, UiText.Text(original, presentation.Language), e.Font ?? key.Font,
                            e.Bounds, selected ? presentation.Palette.Surface : presentation.Palette.Text, flags);
                        e.DrawFocusRectangle();
                    };
                    return presentation;
                });
                state.Language = language;
                state.Palette = palette;
                combo.ComboBox.Invalidate();
            }
            if (CommandIcons.TryResolve(source.Value, out var glyph))
            {
                var owned = Icons.GetValue(item, key =>
                {
                    var holder = new OwnedIcon();
                    key.Disposed += (_, _) => holder.Image?.Dispose();
                    return holder;
                });
                Image? old = owned.Image;
                owned.Image = CommandIcons.Create(glyph, palette.Accent);
                item.Image = owned.Image;
                old?.Dispose();
                item.Text = UiText.Text(UiText.Normalize(source.Value), language);
                item.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            }
            item.ForeColor = palette.Text;
            item.BackColor = palette.Surface;
            if (string.IsNullOrWhiteSpace(item.AccessibleName)) item.AccessibleName = item.Text;
            if (item is ToolStripDropDownItem drop) foreach (ToolStripItem child in drop.DropDownItems) ApplyItem(child);
        }
    }

    private sealed class PaletteTable(ThemePalette palette) : ProfessionalColorTable
    {
        public override Color ToolStripGradientBegin => palette.Surface;
        public override Color ToolStripGradientMiddle => palette.Surface;
        public override Color ToolStripGradientEnd => palette.Surface;
        public override Color MenuStripGradientBegin => palette.Surface;
        public override Color MenuStripGradientEnd => palette.Surface;
        public override Color ToolStripDropDownBackground => palette.Surface;
        public override Color ImageMarginGradientBegin => palette.Surface;
        public override Color ImageMarginGradientMiddle => palette.Surface;
        public override Color ImageMarginGradientEnd => palette.Surface;
        public override Color MenuItemSelected => palette.Border;
        public override Color MenuBorder => palette.Border;
        public override Color ToolStripBorder => palette.Border;
    }
}

public sealed class DashboardFilterBar : UserControl
{
    private readonly TextBox _search = new() { Width = 290, PlaceholderText = "Search devices" };
    private readonly ComboBox _presence = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    private readonly Label _count = new() { AutoSize = true, Margin = new Padding(14, 7, 0, 0) };
    private readonly string[] _states = { "all", "online", "offline", "pending", "unhealthy" };
    public string SearchText => _search.Text;
    public string PresenceFilter => _states[Math.Max(0, _presence.SelectedIndex)];
    public event EventHandler? FilterChanged;
    public DashboardFilterBar()
    {
        Dock = DockStyle.Top;
        Height = 52;
        Padding = new Padding(10, 8, 10, 8);
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        _search.AccessibleName = "Search devices";
        _presence.AccessibleName = "Device presence filter";
        _presence.Items.AddRange(new object[] { "All devices", "Online", "Offline", "Pending enrollment", "Unhealthy" });
        _presence.SelectedIndex = 0;
        _search.TextChanged += (_, _) => FilterChanged?.Invoke(this, EventArgs.Empty);
        _presence.SelectedIndexChanged += (_, _) => FilterChanged?.Invoke(this, EventArgs.Empty);
        layout.Controls.AddRange(new Control[] { _search, _presence, _count });
        Controls.Add(layout);
    }
    public void SetResultCount(int visible, int total) => _count.Text = $"{visible} / {total}";
    public void ApplyPreferences(SchoolSettings settings)
    {
        string language = settings.General.Language;
        _search.PlaceholderText = UiText.Text("Search devices", language);
        _search.AccessibleName = _search.PlaceholderText;
        int selected = _presence.SelectedIndex;
        _presence.Items.Clear();
        _presence.Items.AddRange(new object[] { "All devices", "Online", "Offline", "Pending enrollment", "Unhealthy" }.Select(t => (object)UiText.Text((string)t, language)).ToArray());
        _presence.SelectedIndex = Math.Max(0, selected);
        TeacherExperience.Apply(this, settings);
    }
}
