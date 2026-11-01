using ClassMonitor.Shared.Settings;

namespace ClassMonitor.Teacher.Presentation;

/// <summary>Edits a detached draft. The owner atomically persists/applies it before this dialog accepts the change.</summary>
public sealed class SettingsCenterForm : Form
{
    private sealed record Choice(string Value, string Label);
    private sealed record Section(string Key, Panel Page, List<string> SearchTerms)
    {
        public string Caption { get; set; } = Key;
        public override string ToString() => Caption;
    }
    private SchoolSettings _draft;
    private readonly Action<SchoolSettings> _persistAndApply;
    private readonly Action? _openEnrollment;
    private readonly Action? _openUpdates;
    private readonly Action? _openDiagnostics;
    private readonly List<Action> _readers = new();
    private readonly List<Section> _sections = new();
    private readonly ListBox _navigation = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, ItemHeight = 38, IntegralHeight = false };
    private readonly TextBox _search = new() { Dock = DockStyle.Top, Height = 32 };
    private readonly Panel _content = new() { Dock = DockStyle.Fill, Padding = new Padding(18) };
    private readonly FlowLayoutPanel _buttons = new() { Dock = DockStyle.Bottom, Height = 55, Padding = new Padding(8), WrapContents = false };
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterWidth = 1, Size = new Size(930, 550) };

    public SettingsCenterForm(SchoolSettings current, Action<SchoolSettings> persistAndApply,
        Action? openEnrollment = null, Action? openUpdates = null, Action? openDiagnostics = null)
    {
        current.EnsureValid();
        _draft = current.Clone();
        _persistAndApply = persistAndApply ?? throw new ArgumentNullException(nameof(persistAndApply));
        _openEnrollment = openEnrollment;
        _openUpdates = openUpdates;
        _openDiagnostics = openDiagnostics;
        Text = "Settings center";
        ClientSize = new Size(930, 690);
        MinimumSize = new Size(850, 600);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 10F);
        AutoScaleMode = AutoScaleMode.Dpi;
        _split.SplitterDistance = 190;
        var header = new Label
        {
            Text = "Preferences apply to this console. Enrollment and permissions are controlled separately.",
            Dock = DockStyle.Top, Height = 62, Padding = new Padding(18, 12, 18, 8)
        };
        _search.Margin = new Padding(10);
        _search.AccessibleName = "Search settings";
        _navigation.AccessibleName = "Settings categories";
        _navigation.SelectedIndexChanged += (_, _) => ShowSelected();
        _search.TextChanged += (_, _) => FilterSections();
        var navigationPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        navigationPanel.Controls.Add(_navigation);
        navigationPanel.Controls.Add(_search);
        _split.Panel1.Controls.Add(navigationPanel);
        _split.Panel2.Controls.Add(_content);
        Controls.Add(_split);
        Controls.Add(header);
        Controls.Add(_buttons);
        var save = MakeButton("Save and apply", SaveDraft);
        var cancel = MakeButton("Cancel", () => { DialogResult = DialogResult.Cancel; Close(); });
        var import = MakeButton("Import preferences", ImportDraft);
        var export = MakeButton("Export preferences", ExportDraft);
        var reset = MakeButton("Reset preferences", () => ReplaceDraft(new SchoolSettings()));
        _buttons.Controls.AddRange(new Control[] { save, cancel, import, export, reset });
        AcceptButton = save;
        CancelButton = cancel;
        BuildSections();
        ApplyLanguageAndTheme();
        FilterSections();
    }

    public SchoolSettings Draft => ReadDraft().Clone();
    private string T(string key) => UiText.Text(key, _draft.General.Language);
    private Button MakeButton(string key, Action action)
    {
        var button = new Button { Text = key, AutoSize = true, Height = 32, AccessibleName = key };
        button.Click += (_, _) => action();
        return button;
    }
    private Section AddSection(string key)
    {
        var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Visible = false };
        var section = new Section(key, page, new List<string> { key });
        var heading = new Label { Text = T(key), Dock = DockStyle.Top, Height = 45, Font = new Font(Font, FontStyle.Bold), AccessibleRole = AccessibleRole.StaticText };
        page.Controls.Add(heading);
        _sections.Add(section);
        _content.Controls.Add(page);
        return section;
    }
    private void AddAction(string key, Action? action)
    {
        if (action is null) return;
        var section = AddSection(key);
        var open = MakeButton(key, action);
        AddField(section, key, open);
    }
    private void BuildSections()
    {
        var general = AddSection("General");
        Pick(general, "Language", _draft.General.Language, new[] { new Choice("en", "English"), new Choice("ar", "العربية"), new Choice("he", "עברית") }, (s, v) => s.General.Language = v);
        Pick(general, "Theme", _draft.General.Theme, new[] { new Choice("system", T("System")), new Choice("light", T("Light")), new Choice("dark", T("Dark")) }, (s, v) => s.General.Theme = v);
        Check(general, "Enable notifications", _draft.General.NotificationsEnabled, (s, v) => s.General.NotificationsEnabled = v);
        var network = AddSection("Network");
        Number(network, "Agent TCP port", _draft.Network.AgentPort, 1024, 65535, (s, v) => s.Network.AgentPort = (int)v);
        Check(network, "Enable LAN discovery", _draft.Network.DiscoveryEnabled, (s, v) => s.Network.DiscoveryEnabled = v);
        Number(network, "Discovery UDP port", _draft.Network.DiscoveryPort, 1024, 65535, (s, v) => s.Network.DiscoveryPort = (int)v);
        TextField(network, "Local IPv4 interface (empty = all)", _draft.Network.LocalInterfaceAddress, (s, v) => s.Network.LocalInterfaceAddress = v);
        Number(network, "Connection timeout (seconds)", _draft.Network.ConnectTimeoutSeconds, 2, 60, (s, v) => s.Network.ConnectTimeoutSeconds = (int)v);
        Number(network, "Authentication timeout (seconds)", _draft.Network.HandshakeTimeoutSeconds, 2, 60, (s, v) => s.Network.HandshakeTimeoutSeconds = (int)v);
        Number(network, "Health timeout (seconds)", _draft.Network.HealthTimeoutSeconds, 5, 300, (s, v) => s.Network.HealthTimeoutSeconds = (int)v);
        var display = AddSection("Display");
        Number(display, "Thumbnail width", _draft.Display.ThumbnailWidth, 160, 640, (s, v) => s.Display.ThumbnailWidth = (int)v);
        Number(display, "Thumbnail height", _draft.Display.ThumbnailHeight, 120, 480, (s, v) => s.Display.ThumbnailHeight = (int)v);
        Number(display, "Maximum frame width", _draft.Display.MaxFrameWidth, 320, 1920, (s, v) => s.Display.MaxFrameWidth = (int)v);
        Number(display, "JPEG quality", _draft.Display.Quality, 10, 90, (s, v) => s.Display.Quality = (int)v);
        Number(display, "Frames per second", (decimal)_draft.Display.FramesPerSecond, 0.2m, 10, (s, v) => s.Display.FramesPerSecond = (double)v, 1);
        Number(display, "Bandwidth budget (Mbps)", (decimal)_draft.Display.BandwidthMbps, 0.25m, 100, (s, v) => s.Display.BandwidthMbps = (double)v, 2);
        Check(display, "Adapt quality to bandwidth", _draft.Display.AdaptiveQuality, (s, v) => s.Display.AdaptiveQuality = v);
        var files = AddSection("Files");
        DirectoryField(files, "Received lesson directory", _draft.Files.ReceiveDirectory, (s, v) => s.Files.ReceiveDirectory = v);
        DirectoryField(files, "Collected lesson directory", _draft.Files.CollectDirectory, (s, v) => s.Files.CollectDirectory = v);
        Number(files, "Maximum transfer (MiB)", _draft.Files.MaximumTransferMiB, 1, 8, (s, v) => s.Files.MaximumTransferMiB = (int)v);
        var recording = AddSection("Recording");
        AddNotice(recording, "Recording still requires a classroom grant and an explicit recording action.");
        Check(recording, "Allow authorized recording", _draft.Recording.AllowRecording, (s, v) => s.Recording.AllowRecording = v);
        DirectoryField(recording, "Recording directory", _draft.Recording.Directory, (s, v) => s.Recording.Directory = v);
        Number(recording, "Retention (days)", _draft.Recording.RetentionDays, 1, 365, (s, v) => s.Recording.RetentionDays = (int)v);
        Number(recording, "Recording storage limit (MiB)", _draft.Recording.MaximumStorageMiB, 64, 102400, (s, v) => s.Recording.MaximumStorageMiB = (int)v);
        var layout = AddSection("Layout");
        Check(layout, "Remember window size", _draft.Layout.RememberWindowBounds, (s, v) => s.Layout.RememberWindowBounds = v);
        Number(layout, "Window width", _draft.Layout.WindowWidth, 900, 7680, (s, v) => s.Layout.WindowWidth = (int)v);
        Number(layout, "Window height", _draft.Layout.WindowHeight, 600, 4320, (s, v) => s.Layout.WindowHeight = (int)v);
        Number(layout, "Classroom sidebar width", _draft.Layout.SidebarWidth, 160, 480, (s, v) => s.Layout.SidebarWidth = (int)v);
        Pick(layout, "Device ordering", _draft.Layout.DeviceSort, new[] { new Choice("name", T("Name")), new Choice("presence", T("Presence")) }, (s, v) => s.Layout.DeviceSort = v);
        var policies = AddSection("Focus");
        AddNotice(policies, "Restrictions always restore when the authorized session ends.");
        Check(policies, "Confirm before focus mode", _draft.Policies.ConfirmBeforeFocus, (s, v) => s.Policies.ConfirmBeforeFocus = v);
        Number(policies, "Maximum focus duration (minutes)", _draft.Policies.MaximumFocusMinutes, 1, 240, (s, v) => s.Policies.MaximumFocusMinutes = (int)v);
        AddAction("Security", _openEnrollment);
        AddAction("Updates", _openUpdates);
        AddAction("Diagnostics", _openDiagnostics);
    }
    private void AddField(Section section, string key, Control editor)
    {
        section.SearchTerms.Add(key);
        var row = new TableLayoutPanel { Dock = DockStyle.Top, Height = 65, ColumnCount = 2, Padding = new Padding(0, 5, 0, 5), Tag = key };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        var label = new Label { Text = T(key), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AccessibleName = T(key) };
        editor.AccessibleName = T(key);
        editor.Dock = DockStyle.Fill;
        editor.TabIndex = _readers.Count;
        row.Controls.Add(label, 0, 0);
        row.Controls.Add(editor, 1, 0);
        section.Page.Controls.Add(row);
        row.BringToFront();
    }
    private void AddNotice(Section section, string key)
    {
        section.SearchTerms.Add(key);
        var label = new Label { Text = T(key), Dock = DockStyle.Top, Height = 55, Padding = new Padding(0, 5, 0, 5), Tag = key };
        section.Page.Controls.Add(label);
        label.BringToFront();
    }
    private void Number(Section section, string key, decimal value, decimal minimum, decimal maximum, Action<SchoolSettings, decimal> set, int decimals = 0)
    {
        var editor = new NumericUpDown { Minimum = minimum, Maximum = maximum, Value = value, DecimalPlaces = decimals, Increment = decimals == 0 ? 1 : 0.1m };
        _readers.Add(() => set(_draft, editor.Value));
        AddField(section, key, editor);
    }
    private void Check(Section section, string key, bool value, Action<SchoolSettings, bool> set)
    {
        var editor = new CheckBox { Checked = value, AutoSize = true };
        _readers.Add(() => set(_draft, editor.Checked));
        AddField(section, key, editor);
    }
    private void Pick(Section section, string key, string value, Choice[] options, Action<SchoolSettings, string> set)
    {
        var editor = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = nameof(Choice.Label), ValueMember = nameof(Choice.Value) };
        editor.Items.AddRange(options);
        editor.SelectedIndex = Array.FindIndex(options, option => option.Value == value);
        _readers.Add(() => set(_draft, ((Choice)editor.SelectedItem!).Value));
        AddField(section, key, editor);
    }
    private void TextField(Section section, string key, string value, Action<SchoolSettings, string> set)
    {
        var editor = new TextBox { Text = value, MaxLength = 1024 };
        _readers.Add(() => set(_draft, editor.Text.Trim()));
        AddField(section, key, editor);
    }
    private void DirectoryField(Section section, string key, string value, Action<SchoolSettings, string> set)
    {
        var editor = new TextBox { Text = value, Dock = DockStyle.Fill, MaxLength = 1024, AccessibleName = T(key) };
        var browse = MakeButton("Browse", () =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = editor.Text, Description = T(key), UseDescriptionForTitle = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) editor.Text = dialog.SelectedPath;
        });
        browse.Dock = DockStyle.Right;
        browse.Width = 80;
        var composite = new Panel();
        composite.Controls.Add(editor);
        composite.Controls.Add(browse);
        _readers.Add(() => set(_draft, editor.Text.Trim()));
        AddField(section, key, composite);
    }
    private SchoolSettings ReadDraft()
    {
        foreach (var read in _readers) read();
        _draft.EnsureValid();
        return _draft;
    }
    private void SaveDraft()
    {
        try { _persistAndApply(ReadDraft().Clone()); DialogResult = DialogResult.OK; Close(); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        { MessageBox.Show(this, ex.Message, T("Preferences were not saved"), MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
    private void ImportDraft()
    {
        using var dialog = new OpenFileDialog { Filter = "ClassMonitor preferences (*.json)|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { ReplaceDraft(SchoolSettingsStore.Load(dialog.FileName)); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        { MessageBox.Show(this, ex.Message, T("Import failed"), MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
    private void ExportDraft()
    {
        using var dialog = new SaveFileDialog { Filter = "ClassMonitor preferences (*.json)|*.json", FileName = "classmonitor-preferences.json", AddExtension = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { SchoolSettingsStore.SaveAtomic(dialog.FileName, ReadDraft()); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        { MessageBox.Show(this, ex.Message, T("Export failed"), MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
    private void ReplaceDraft(SchoolSettings settings)
    {
        settings.EnsureValid();
        _draft = settings.Clone();
        foreach (var section in _sections) section.Page.Dispose();
        _sections.Clear();
        _readers.Clear();
        BuildSections();
        ApplyLanguageAndTheme();
        FilterSections();
    }
    private void ApplyLanguageAndTheme()
    {
        foreach (var section in _sections) section.Caption = T(section.Key);
        _search.PlaceholderText = T("Search settings");
        TeacherExperience.Apply(this, _draft);
    }
    private void FilterSections()
    {
        string search = _search.Text.Trim();
        string? selected = (_navigation.SelectedItem as Section)?.Key;
        var sections = _sections.Where(section => search.Length == 0 || section.SearchTerms.Any(term =>
            term.Contains(search, StringComparison.OrdinalIgnoreCase) || T(term).Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
        _navigation.BeginUpdate();
        _navigation.Items.Clear();
        _navigation.Items.AddRange(sections);
        _navigation.SelectedIndex = sections.Length == 0 ? -1 : Math.Max(0, Array.FindIndex(sections, x => x.Key == selected));
        _navigation.EndUpdate();
        ShowSelected();
    }
    private void ShowSelected()
    {
        var selected = _navigation.SelectedItem as Section;
        foreach (var section in _sections) section.Page.Visible = section == selected;
        selected?.Page.BringToFront();
        _content.AccessibleName = selected?.Caption ?? T("No matching settings");
    }
}
