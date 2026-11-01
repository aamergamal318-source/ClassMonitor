using System.Drawing;
using ClassMonitor.Shared;

namespace ClassMonitor.Teacher;

/// <summary>
/// Teacher console: auto-discovers agents, shows a live thumbnail grid with selection and
/// per-machine status, and sends commands (message, lock/unlock, screen broadcast) to the
/// selected machines (or all of them when nothing is selected). Double-click a tile for a
/// full live view with remote control.
/// </summary>
internal sealed class MainForm : Form
{
    private static readonly Color Accent = Color.FromArgb(0, 120, 215);

    private readonly AppConfig _config = AppConfig.Load(AppConfig.TeacherConfigPath);
    private byte[] _key;
    private readonly Discovery _discovery = new();
    private readonly Dictionary<string, AgentConnection> _conns = new();
    private readonly Dictionary<string, AgentTile> _tiles = new();
    private readonly HashSet<string> _selected = new();
    private readonly HashSet<string> _locked = new();
    private readonly Dictionary<string, Recorder> _recorders = new();
    private readonly GroupsStore _groups = GroupsStore.Load();
    private ListBox _groupList = null!;
    private readonly UsageTracker _usage = new();
    private readonly List<string> _alertKeywords = new();
    private AlertsForm? _alertsForm;
    private readonly System.Windows.Forms.Timer _snapTimer = new();
    private string? _snapFolder;

    private readonly FlowLayoutPanel _grid;
    private readonly ToolStripButton _broadcastBtn;
    private readonly ToolStripButton _studentBroadcastBtn;
    private readonly ToolStripComboBox _sizeCombo;
    private readonly ToolStripComboBox _refreshCombo;
    private readonly ToolStripStatusLabel _status;
    private readonly ToolStripStatusLabel _selStatus;
    private readonly ClassMonitor.Desktop.UpdateController _updates;
    private readonly System.Windows.Forms.Timer _broadcastTimer = new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer _studentBroadcastTimer = new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer _activityTimer = new() { Interval = 4000 };
    private bool _broadcasting;
    private bool _studentBroadcasting;
    private AgentConnection? _studentBroadcastSource;
    private volatile bool _capturing;
    private Size _tileSize = new(280, 205);
    private string? _collectFolder;   // where collected files are saved (set when a collect starts)
    private int _collectCount;         // files saved during the current collect

    public MainForm()
    {
        _key = Security.DeriveKey(_config.Password);
        Text = "ClassMonitor — Teacher Console";
        ClientSize = new Size(1180, 760);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.White;
        if (Branding.AppIcon is not null) Icon = Branding.AppIcon;

        // ---- Toolbar -------------------------------------------------------
        var tools = new ToolStrip
        {
            GripStyle = ToolStripGripStyle.Hidden,
            ImageScalingSize = new Size(20, 20),
            Padding = new Padding(6, 4, 6, 4),
            Renderer = new ToolStripProfessionalRenderer { RoundedEdges = false },
            BackColor = Color.FromArgb(246, 247, 249),
        };

        tools.Items.Add(TextButton("🔍  Live view", "Open live view & remote control for the selected machine", (_, _) => OpenLiveForSelection()));
        tools.Items.Add(new ToolStripSeparator());
        tools.Items.Add(TextButton("💬  Message", "Show a message on the target machines", (_, _) => MessageTargets()));
        tools.Items.Add(TextButton("🔒  Lock", "Lock the target screens", (_, _) => LockTargets(true)));
        tools.Items.Add(TextButton("🔓  Unlock", "Unlock the target screens", (_, _) => LockTargets(false)));
        tools.Items.Add(new ToolStripSeparator());
        var power = new ToolStripDropDownButton("⏻  Power")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = "Power actions for the target machines (never affects this console)",
        };
        power.DropDownItems.Add("Shut down", null, (_, _) => PowerTargets("shutdown", "shut down"));
        power.DropDownItems.Add("Restart", null, (_, _) => PowerTargets("restart", "restart"));
        power.DropDownItems.Add("Log off", null, (_, _) => PowerTargets("logoff", "log off"));
        tools.Items.Add(power);
        var run = new ToolStripDropDownButton("▶  Run")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = "Run a program or open a website on the target machines",
        };
        run.DropDownItems.Add("Run program…", null, (_, _) => RunTargets("program"));
        run.DropDownItems.Add("Open website…", null, (_, _) => RunTargets("website"));
        tools.Items.Add(run);
        tools.Items.Add(new ToolStripSeparator());
        var restrict = new ToolStripDropDownButton("🚫  Restrict")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = "Block applications, websites, or internet on the target machines",
        };
        restrict.DropDownItems.Add("Block apps…", null, (_, _) => BlockAppsPrompt());
        restrict.DropDownItems.Add("Allow only these apps…", null, (_, _) => AllowAppsPrompt());
        restrict.DropDownItems.Add("Unblock apps", null, (_, _) => RestrictTargets("apps", false, Array.Empty<string>()));
        restrict.DropDownItems.Add(new ToolStripSeparator());
        restrict.DropDownItems.Add("Block websites…", null, (_, _) => BlockSitesPrompt());
        restrict.DropDownItems.Add("Unblock websites", null, (_, _) => RestrictTargets("sites", false, Array.Empty<string>()));
        restrict.DropDownItems.Add(new ToolStripSeparator());
        restrict.DropDownItems.Add("Block internet (web)", null, (_, _) => RestrictTargets("internet", true, Array.Empty<string>()));
        restrict.DropDownItems.Add("Allow internet", null, (_, _) => RestrictTargets("internet", false, Array.Empty<string>()));
        tools.Items.Add(restrict);
        tools.Items.Add(new ToolStripSeparator());

        var files = new ToolStripDropDownButton("📁  Files")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = "Send a file to, or collect a file from, the target machines",
        };
        files.DropDownItems.Add("Send file to students…", null, (_, _) => SendFileTargets());
        files.DropDownItems.Add("Collect file from students…", null, (_, _) => CollectFileTargets());
        tools.Items.Add(files);
        tools.Items.Add(new ToolStripSeparator());

        _broadcastBtn = new ToolStripButton("📡  Broadcast my screen")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            CheckOnClick = true,
            ToolTipText = "Show your screen on all student machines",
        };
        _broadcastBtn.CheckedChanged += (_, _) => ToggleBroadcast(_broadcastBtn.Checked);
        tools.Items.Add(_broadcastBtn);

        _studentBroadcastBtn = new ToolStripButton("📺  Show selected student")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            CheckOnClick = true,
            ToolTipText = "Show the selected student's screen on all other machines",
        };
        _studentBroadcastBtn.CheckedChanged += (_, _) => ToggleStudentBroadcast(_studentBroadcastBtn.Checked);
        tools.Items.Add(_studentBroadcastBtn);

        tools.Items.Add(new ToolStripSeparator());
        var mon = new ToolStripDropDownButton("📈  Monitoring")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = "Scheduled snapshots, keyword alerts, usage report",
        };
        mon.DropDownItems.Add("Snapshots: Off", null, (_, _) => SetSnapshots(0));
        mon.DropDownItems.Add("Snapshots: every 1 min", null, (_, _) => SetSnapshots(1));
        mon.DropDownItems.Add("Snapshots: every 5 min", null, (_, _) => SetSnapshots(5));
        mon.DropDownItems.Add("Snapshots: every 15 min", null, (_, _) => SetSnapshots(15));
        mon.DropDownItems.Add(new ToolStripSeparator());
        mon.DropDownItems.Add("Alert keywords…", null, (_, _) => EditKeywords());
        mon.DropDownItems.Add("Show alerts window", null, (_, _) => ShowAlerts());
        mon.DropDownItems.Add(new ToolStripSeparator());
        mon.DropDownItems.Add("Usage report…", null, (_, _) => ShowUsage());
        tools.Items.Add(mon);

        tools.Items.Add(new ToolStripSeparator());
        tools.Items.Add(TextButton("🔌  Connect manually", "Connect to a specific machine by host/IP", (_, _) => ConnectManualHost()));
        tools.Items.Add(TextButton("☑  Select all", "Select every machine", (_, _) => SelectAll(true)));
        tools.Items.Add(TextButton("☐  Clear", "Clear the selection", (_, _) => SelectAll(false)));
        tools.Items.Add(new ToolStripSeparator());
        tools.Items.Add(TextButton("⚙  Settings", "Set the class password (must match the agents)", (_, _) => EditSettings()));

        // right-aligned thumbnail-size control
        _sizeCombo = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
        _sizeCombo.Items.AddRange(["Small", "Medium", "Large"]);
        _sizeCombo.SelectedIndex = 1;
        _sizeCombo.SelectedIndexChanged += (_, _) => ApplyTileSize(_sizeCombo.SelectedIndex);
        var sizeLabel = new ToolStripLabel("Thumbnail size:") { Alignment = ToolStripItemAlignment.Right };
        _sizeCombo.Alignment = ToolStripItemAlignment.Right;
        tools.Items.Add(_sizeCombo);
        tools.Items.Add(sizeLabel);

        _refreshCombo = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
        _refreshCombo.Items.AddRange(["Performance", "Balanced", "Quality"]);
        _refreshCombo.SelectedIndex = 1;
        _refreshCombo.SelectedIndexChanged += (_, _) => ApplyRefreshProfile(_refreshCombo.SelectedIndex);
        var refreshLabel = new ToolStripLabel("Refresh:") { Alignment = ToolStripItemAlignment.Right };
        _refreshCombo.Alignment = ToolStripItemAlignment.Right;
        tools.Items.Add(_refreshCombo);
        tools.Items.Add(refreshLabel);

        // ---- Grid ----------------------------------------------------------
        _grid = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(10),
            BackColor = Color.FromArgb(238, 240, 243),
        };
        _grid.MouseDown += (_, _) => SelectAll(false); // click empty space clears selection

        // ---- Status bar ----------------------------------------------------
        var statusStrip = new StatusStrip { BackColor = Color.FromArgb(246, 247, 249) };
        _status = new ToolStripStatusLabel("Discovering agents…") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _selStatus = new ToolStripStatusLabel("0 selected");
        statusStrip.Items.Add(_status);
        statusStrip.Items.Add(_selStatus);
        var updates = new ToolStripButton("التحديثات / Updates");
        statusStrip.Items.Add(updates);
        _updates = new ClassMonitor.Desktop.UpdateController(ClassMonitor.Shared.Updates.UpdateComponent.Teacher,
            this, updates, Close);
        Shown += async (_, _) => await _updates.CheckAtStartupAsync();

        Controls.Add(_grid);
        Controls.Add(BuildGroupsPanel());
        Controls.Add(tools);
        Controls.Add(BuildHeader());   // added after tools so it sits above the toolbar
        Controls.Add(statusStrip);

        _broadcastTimer.Tick += (_, _) => BroadcastTick();
        _studentBroadcastTimer.Tick += (_, _) => StudentBroadcastTick();
        _activityTimer.Tick += (_, _) => RefreshActivityState();
        _snapTimer.Tick += (_, _) => SnapshotTick();
        _discovery.AgentSeen += OnAgentSeen;
        Load += (_, _) =>
        {
            _activityTimer.Start();
            _discovery.Start();
            foreach (var host in _config.ManualHosts.Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.OrdinalIgnoreCase))
                AddOrConnect(host.Trim(), host.Trim(), Net.AgentTcpPort);
        };
    }

    private static Panel BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Color.White };
        header.Paint += (_, e) =>
            e.Graphics.DrawLine(new Pen(Color.FromArgb(225, 227, 232)), 0, header.Height - 1, header.Width, header.Height - 1);

        var pic = new PictureBox
        {
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(40, 40),
            Location = new Point(14, 8),
            Image = Branding.Logo,
        };
        var title = new Label
        {
            Text = "ClassMonitor",
            Font = new Font("Segoe UI", 15F, FontStyle.Bold),
            ForeColor = Color.FromArgb(23, 52, 160),
            AutoSize = true,
            Location = new Point(62, 7),
        };
        var subtitle = new Label
        {
            Text = "Classroom monitoring console",
            Font = new Font("Segoe UI", 8.5F),
            ForeColor = Color.Gray,
            AutoSize = true,
            Location = new Point(64, 33),
        };
        header.Controls.Add(pic);
        header.Controls.Add(title);
        header.Controls.Add(subtitle);
        return header;
    }

    private Panel BuildGroupsPanel()
    {
        var panel = new Panel { Dock = DockStyle.Left, Width = 200, Padding = new Padding(8), BackColor = Color.FromArgb(248, 249, 251) };

        var title = new Label { Dock = DockStyle.Top, Height = 24, Text = "Groups (classes)", Font = new Font("Segoe UI", 9.5F, FontStyle.Bold) };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 64, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        var newBtn = new Button { Text = "New from selection", Width = 176, Height = 26 };
        var delBtn = new Button { Text = "Delete group", Width = 176, Height = 26 };
        newBtn.Click += (_, _) => NewGroupFromSelection();
        delBtn.Click += (_, _) => DeleteSelectedGroup();
        buttons.Controls.Add(newBtn);
        buttons.Controls.Add(delBtn);

        _groupList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        _groupList.SelectedIndexChanged += (_, _) => SelectGroup();

        panel.Controls.Add(_groupList);
        panel.Controls.Add(buttons);
        panel.Controls.Add(title);
        RefreshGroupList();
        return panel;
    }

    private void RefreshGroupList()
    {
        _groupList.BeginUpdate();
        _groupList.Items.Clear();
        foreach (var name in _groups.Groups.Keys.OrderBy(n => n)) _groupList.Items.Add(name);
        _groupList.EndUpdate();
    }

    private void SelectGroup()
    {
        if (_groupList.SelectedItem is not string name) return;
        if (!_groups.Groups.TryGetValue(name, out var machines)) return;

        var wanted = new HashSet<string>(machines, StringComparer.OrdinalIgnoreCase);
        _selected.Clear();
        foreach (var (host, conn) in _conns)
            if (wanted.Contains(conn.MachineName)) _selected.Add(host);
        RefreshSelectionVisuals();
        _status.Text = $"Group \"{name}\": selected {_selected.Count} of {machines.Count} machine(s) online.";
    }

    private void NewGroupFromSelection()
    {
        if (_selected.Count == 0)
        {
            MessageBox.Show("Select one or more machines first (click their tiles).", "New group",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var name = Prompt.Show("New group", "Name for this group / class:");
        if (string.IsNullOrWhiteSpace(name)) return;

        var machines = _selected.Where(_conns.ContainsKey).Select(h => _conns[h].MachineName).Distinct().ToList();
        _groups.Groups[name] = machines;
        _groups.Save();
        RefreshGroupList();
        _status.Text = $"Saved group \"{name}\" with {machines.Count} machine(s).";
    }

    private void DeleteSelectedGroup()
    {
        if (_groupList.SelectedItem is not string name) return;
        if (MessageBox.Show($"Delete group \"{name}\"?", "Delete group",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _groups.Groups.Remove(name);
        _groups.Save();
        RefreshGroupList();
    }

    private static ToolStripButton TextButton(string text, string tip, EventHandler onClick)
    {
        var b = new ToolStripButton(text)
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = tip,
            Padding = new Padding(4, 2, 4, 2),
        };
        b.Click += onClick;
        return b;
    }

    // ---- Discovery / connection lifecycle ---------------------------------

    private void OnAgentSeen(string host, string machine, string user, int port)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() => AddOrConnect(host, machine, port));
    }

    private void AddOrConnect(string host, string machine, int port)
    {
        if (_conns.ContainsKey(host)) return;

        var conn = new AgentConnection(host, machine, port, _key);
        _conns[host] = conn;

        var tile = new AgentTile(conn, _tileSize);
        tile.Clicked += ctrl => OnTileClicked(host, ctrl);
        tile.OpenRequested += () => OpenLive(conn);
        tile.ContextMenuStrip = BuildTileMenu(conn);
        _tiles[host] = tile;
        _grid.Controls.Add(tile);

        conn.FrameReceived += OnFrame;
        conn.Disconnected += OnDisconnected;
        conn.FileCollected += OnFileCollected;
        conn.ActivityReceived += OnActivity;

        UpdateStatus();
        _ = ConnectAsync(conn);
    }

    private async Task ConnectAsync(AgentConnection conn)
    {
        try
        {
            await conn.ConnectAsync();
            if (_broadcasting) await conn.SetBroadcastAsync(true);
        }
        catch { OnDisconnected(conn); }
    }

    private void OnFrame(AgentConnection conn, byte[] jpeg)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() =>
        {
            if (_tiles.TryGetValue(conn.Host, out var tile))
                tile.SetFrame(jpeg);
        });
    }

    private void OnDisconnected(AgentConnection conn)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() =>
        {
            if (_recorders.Remove(conn.Host, out var rec)) rec.Dispose();
            if (_tiles.Remove(conn.Host, out var tile)) { _grid.Controls.Remove(tile); tile.Dispose(); }
            if (_conns.Remove(conn.Host, out var c)) c.Dispose();
            _selected.Remove(conn.Host);
            _locked.Remove(conn.Host);

            if (_studentBroadcastSource == conn)
            {
                ToggleStudentBroadcast(false);
            }

            UpdateStatus();
        });
    }

    // ---- Selection --------------------------------------------------------

    private void OnTileClicked(string host, bool ctrl)
    {
        if (ctrl)
        {
            if (!_selected.Add(host)) _selected.Remove(host);
        }
        else
        {
            _selected.Clear();
            _selected.Add(host);
        }

        if (_studentBroadcasting)
        {
            var source = ResolveStudentBroadcastSource();
            if (source is null)
            {
                ToggleStudentBroadcast(false);
            }
            else
            {
                _studentBroadcastSource = source;
            }
        }

        RefreshSelectionVisuals();
    }

    private void SelectAll(bool select)
    {
        _selected.Clear();
        if (select) foreach (var h in _tiles.Keys) _selected.Add(h);
        RefreshSelectionVisuals();
    }

    private void RefreshSelectionVisuals()
    {
        foreach (var (host, tile) in _tiles) tile.SetSelected(_selected.Contains(host));
        UpdateStatus();
    }

    /// <summary>Hosts a command targets: the selection, or everyone when nothing is selected.</summary>
    private IEnumerable<AgentConnection> Targets() =>
        (_selected.Count == 0 ? _conns.Keys.ToList() : _selected.ToList())
        .Where(_conns.ContainsKey).Select(h => _conns[h]);

    private void UpdateStatus()
    {
        _status.Text = _conns.Count == 0 ? "Discovering agents…" : $"{_conns.Count} agent(s) connected";
        _selStatus.Text = _selected.Count == 0 ? "targets: all" : $"{_selected.Count} selected";
    }

    // ---- Per-tile context menu -------------------------------------------

    private ContextMenuStrip BuildTileMenu(AgentConnection conn)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Live view && remote control", null, (_, _) => OpenLive(conn));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Send message…", null, (_, _) => { var t = Prompt.Show("Message", $"Message for {conn.MachineName}:"); if (!string.IsNullOrWhiteSpace(t)) _ = conn.SendMessageAsync(t, 8000); });
        menu.Items.Add("Lock screen", null, (_, _) => { _locked.Add(conn.Host); _tiles[conn.Host].SetLocked(true); _ = conn.LockAsync(true, "Screen locked by the teacher."); });
        menu.Items.Add("Unlock screen", null, (_, _) => { _locked.Remove(conn.Host); _tiles[conn.Host].SetLocked(false); _ = conn.LockAsync(false, ""); });
        menu.Items.Add(new ToolStripSeparator());

        var pm = new ToolStripMenuItem("Power");
        pm.DropDownItems.Add("Shut down", null, (_, _) => ConfirmPowerOne(conn, "shutdown", "shut down"));
        pm.DropDownItems.Add("Restart", null, (_, _) => ConfirmPowerOne(conn, "restart", "restart"));
        pm.DropDownItems.Add("Log off", null, (_, _) => ConfirmPowerOne(conn, "logoff", "log off"));
        menu.Items.Add(pm);

        var rm = new ToolStripMenuItem("Run");
        rm.DropDownItems.Add("Run program…", null, (_, _) => RunOne(conn, "program"));
        rm.DropDownItems.Add("Open website…", null, (_, _) => RunOne(conn, "website"));
        menu.Items.Add(rm);

        var fm = new ToolStripMenuItem("Files");
        fm.DropDownItems.Add("Send file…", null, (_, _) => SendFileOne(conn));
        fm.DropDownItems.Add("Collect file…", null, (_, _) => CollectFileOne(conn));
        menu.Items.Add(fm);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Chat…", null, (_, _) => new ChatForm(conn) { Owner = this }.Show());
        menu.Items.Add(new ToolStripSeparator());

        var recm = new ToolStripMenuItem("Record screen");
        var recStart = new ToolStripMenuItem("Start recording…", null, (_, _) => StartRecording(conn));
        var recStop = new ToolStripMenuItem("Stop recording", null, (_, _) => StopRecording(conn));
        recm.DropDownOpening += (_, _) =>
        {
            bool on = _recorders.ContainsKey(conn.Host);
            recStart.Enabled = !on;
            recStop.Enabled = on;
        };
        recm.DropDownItems.Add(recStart);
        recm.DropDownItems.Add(recStop);
        menu.Items.Add(recm);

        return menu;
    }

    // ---- Commands ---------------------------------------------------------

    private void OpenLiveForSelection()
    {
        var conn = _selected.Count == 1 ? _conns.GetValueOrDefault(_selected.First())
                 : _conns.Count == 1 ? _conns.Values.First()
                 : null;
        if (conn is null)
            MessageBox.Show("Select a single machine first (click its tile).", "Live view",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        else
            OpenLive(conn);
    }

    private void OpenLive(AgentConnection conn)
    {
        if (_tiles.TryGetValue(conn.Host, out var tile)) tile.SetLive(true);
        var form = new LiveViewForm(conn) { Owner = this };
        form.FormClosed += (_, _) => { if (_tiles.TryGetValue(conn.Host, out var t)) t.SetLive(false); };
        form.Show();
    }

    private void MessageTargets()
    {
        var text = Prompt.Show("Message", "Message to show on the target screens:");
        if (string.IsNullOrWhiteSpace(text)) return;
        foreach (var c in Targets()) _ = c.SendMessageAsync(text, 8000);
    }

    private void LockTargets(bool locked)
    {
        string text = locked ? "Screen locked by the teacher." : "";
        foreach (var c in Targets())
        {
            if (locked) _locked.Add(c.Host); else _locked.Remove(c.Host);
            if (_tiles.TryGetValue(c.Host, out var t)) t.SetLocked(locked);
            _ = c.LockAsync(locked, text);
        }
    }

    private void PowerTargets(string action, string human)
    {
        var list = Targets().ToList();
        if (list.Count == 0) return;
        var r = MessageBox.Show(
            $"Are you sure you want to {human} {list.Count} machine(s)?\n\n" +
            "The machine running this console is never affected.",
            "Confirm power action", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (r != DialogResult.Yes) return;
        foreach (var c in list) _ = c.PowerAsync(action);
    }

    private void ConfirmPowerOne(AgentConnection conn, string action, string human)
    {
        var r = MessageBox.Show($"{char.ToUpper(human[0]) + human[1..]} {conn.MachineName}?",
            "Confirm power action", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (r == DialogResult.Yes) _ = conn.PowerAsync(action);
    }

    private void RunTargets(string kind)
    {
        var list = Targets().ToList();
        if (list.Count == 0) return;
        string title = kind == "website" ? "Open website" : "Run program";
        string label = kind == "website"
            ? "Website URL to open on the target machines:"
            : "Program to run on the target machines (e.g. notepad):";
        var target = Prompt.Show(title, label);
        if (string.IsNullOrWhiteSpace(target)) return;
        foreach (var c in list) _ = c.RunAsync(kind, target);
    }

    private void RunOne(AgentConnection conn, string kind)
    {
        string title = kind == "website" ? "Open website" : "Run program";
        string label = kind == "website"
            ? $"Website URL to open on {conn.MachineName}:"
            : $"Program to run on {conn.MachineName} (e.g. notepad):";
        var target = Prompt.Show(title, label);
        if (!string.IsNullOrWhiteSpace(target)) _ = conn.RunAsync(kind, target);
    }

    private void SendFileOne(AgentConnection conn)
    {
        using var dlg = new OpenFileDialog { Title = $"Choose a file to send to {conn.MachineName}" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        byte[] bytes;
        try { bytes = File.ReadAllBytes(dlg.FileName); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Send file", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        if (bytes.Length > 12 * 1024 * 1024)
        {
            MessageBox.Show("File is too large (max ~12 MB in this version).", "Send file",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _ = conn.SendFileAsync(Path.GetFileName(dlg.FileName), bytes);
        _status.Text = $"Sent \"{Path.GetFileName(dlg.FileName)}\" to {conn.MachineName}.";
    }

    private void CollectFileOne(AgentConnection conn)
    {
        var path = Prompt.Show("Collect file", $"Full path of the file to collect from {conn.MachineName}:",
            @"C:\Users\Public\Documents\assignment.docx");
        if (string.IsNullOrWhiteSpace(path)) return;
        using var dlg = new FolderBrowserDialog { Description = "Where to save the collected file" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        _collectFolder = dlg.SelectedPath;
        _collectCount = 0;
        _ = conn.RequestFileAsync(path);
        _status.Text = $"Requested file from {conn.MachineName}…";
    }

    private void SendFileTargets()
    {
        var list = Targets().ToList();
        if (list.Count == 0) return;

        using var dlg = new OpenFileDialog { Title = "Choose a file to send to students" };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        byte[] bytes;
        try { bytes = File.ReadAllBytes(dlg.FileName); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Send file", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }

        if (bytes.Length > 12 * 1024 * 1024)
        {
            MessageBox.Show("File is too large (max ~12 MB in this version).", "Send file",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string name = Path.GetFileName(dlg.FileName);
        foreach (var c in list) _ = c.SendFileAsync(name, bytes);
        _status.Text = $"Sent \"{name}\" to {list.Count} machine(s).";
    }

    private void CollectFileTargets()
    {
        var list = Targets().ToList();
        if (list.Count == 0) return;

        var path = Prompt.Show("Collect file", "Full path of the file to collect from each student:",
            @"C:\Users\Public\Documents\assignment.docx");
        if (string.IsNullOrWhiteSpace(path)) return;

        using var dlg = new FolderBrowserDialog { Description = "Where to save the collected files" };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        _collectFolder = dlg.SelectedPath;
        _collectCount = 0;
        foreach (var c in list) _ = c.RequestFileAsync(path);
        _status.Text = $"Requested file from {list.Count} machine(s)…";
    }

    private void OnFileCollected(AgentConnection conn, FileDataMsg data)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() =>
        {
            if (_collectFolder is null) return;
            if (!data.Ok)
            {
                _status.Text = $"{conn.MachineName}: {data.Error}";
                return;
            }
            try
            {
                Directory.CreateDirectory(_collectFolder);
                string safeMachine = string.Concat(conn.MachineName.Split(Path.GetInvalidFileNameChars()));
                string dest = Path.Combine(_collectFolder, $"{safeMachine}_{data.FileName}");
                File.WriteAllBytes(dest, Convert.FromBase64String(data.DataBase64));
                _collectCount++;
                _status.Text = $"Collected {_collectCount} file(s) → {_collectFolder}";
            }
            catch (Exception ex) { _status.Text = $"{conn.MachineName}: save failed — {ex.Message}"; }
        });
    }

    private void StartRecording(AgentConnection conn)
    {
        if (_recorders.ContainsKey(conn.Host)) return;

        using var dlg = new SaveFileDialog
        {
            Title = "Save recording",
            Filter = "AVI video (*.avi)|*.avi",
            FileName = $"{conn.MachineName}_{DateTime.Now:yyyyMMdd_HHmmss}.avi",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        // Make sure frames keep flowing at a decent rate while recording.
        _ = conn.RequestScreenAsync(maxWidth: 1280, quality: 60, intervalMs: 200);

        _recorders[conn.Host] = new Recorder(conn, dlg.FileName);
        if (_tiles.TryGetValue(conn.Host, out var tile)) tile.SetRecording(true);
        _status.Text = $"Recording {conn.MachineName} → {dlg.FileName}";
    }

    private void StopRecording(AgentConnection conn)
    {
        if (!_recorders.Remove(conn.Host, out var rec)) return;
        int frames = rec.FrameCount;
        string path = rec.Path;
        rec.Dispose();
        if (_tiles.TryGetValue(conn.Host, out var tile)) tile.SetRecording(false);
        _status.Text = $"Saved recording ({frames} frames) → {path}";
    }

    private void ConnectManualHost()
    {
        var host = Prompt.Show("Connect manually",
            "Host or IP address to connect to (optionally include :port):",
            "192.168.1.50");
        if (string.IsNullOrWhiteSpace(host)) return;

        string cleaned = host.Trim();
        int port = Net.AgentTcpPort;

        if (cleaned.Contains(':') && cleaned.Count(ch => ch == ':') == 1 && !cleaned.StartsWith("[") && !cleaned.Contains("]"))
        {
            var last = cleaned.LastIndexOf(':');
            if (int.TryParse(cleaned[(last + 1)..], out var parsedPort))
            {
                port = parsedPort;
                cleaned = cleaned[..last];
            }
        }

        cleaned = cleaned.Trim();
        if (cleaned.Length == 0) return;

        if (!_config.ManualHosts.Contains(cleaned, StringComparer.OrdinalIgnoreCase))
        {
            _config.ManualHosts.Add(cleaned);
            try { _config.Save(AppConfig.TeacherConfigPath); } catch { }
        }

        AddOrConnect(cleaned, cleaned, port);
        _status.Text = $"Connecting to {cleaned}:{port}…";
    }

    private void EditSettings()
    {
        var pwd = Prompt.Show("Settings",
            "Class password (must match the password configured on the agents):",
            _config.Password);
        if (pwd is null) return;

        _config.Password = pwd;
        try { _config.Save(AppConfig.TeacherConfigPath); } catch { }
        _key = Security.DeriveKey(pwd);

        // Reconnect everything with the new key (discovery will re-add the agents).
        foreach (var c in _conns.Values.ToList()) c.Dispose();
        _status.Text = "Password updated — reconnecting…";
    }

    private void RestrictTargets(string kind, bool enabled, string[] items, string? mode = null)
    {
        var list = Targets().ToList();
        if (list.Count == 0) return;
        foreach (var c in list) _ = c.RestrictAsync(kind, enabled, items, mode);
        string label = kind == "internet" ? "internet (web)" : kind;
        _status.Text = $"{(enabled ? (string.Equals(mode, "allowlist", StringComparison.OrdinalIgnoreCase) ? "Allowlist set" : "Blocked") : "Unblocked")} {label} on {list.Count} machine(s).";
    }

    private void BlockAppsPrompt()
    {
        var s = Prompt.Show("Block applications",
            "Process names to block (comma-separated, e.g. chrome, msedge, game):");
        if (string.IsNullOrWhiteSpace(s)) return;
        RestrictTargets("apps", true, ParseList(s));
    }

    private void AllowAppsPrompt()
    {
        var s = Prompt.Show("Allow only these applications",
            "Process names allowed to stay open (comma-separated, e.g. chrome, notepad, teams):");
        if (string.IsNullOrWhiteSpace(s)) return;
        RestrictTargets("apps", true, ParseList(s), "allowlist");
    }

    private void BlockSitesPrompt()
    {
        var s = Prompt.Show("Block websites",
            "Domains to block (comma-separated, e.g. youtube.com, facebook.com):");
        if (string.IsNullOrWhiteSpace(s)) return;
        RestrictTargets("sites", true, ParseList(s));
    }

    private static string[] ParseList(string s) =>
        s.Split(new[] { ',', ';', ' ', '\n', '\t', '\r' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // ---- Advanced monitoring -------------------------------------------------

    private void OnActivity(AgentConnection conn, ActivityMsg act)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() =>
        {
            _usage.Add(conn.MachineName, act.ProcessName, 3); // ~3s sampling interval

            bool hit = _alertKeywords.Count > 0 && _alertKeywords.Any(k =>
                (!string.IsNullOrEmpty(act.WindowTitle) && act.WindowTitle.Contains(k, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(act.ProcessName) && act.ProcessName.Contains(k, StringComparison.OrdinalIgnoreCase)));

            if (_tiles.TryGetValue(conn.Host, out var tile))
            {
                tile.SetAlert(hit);
                tile.SetActive(conn.LastActivityAt);
            }
            if (hit)
            {
                string text = $"{act.WindowTitle} [{act.ProcessName}]";
                _alertsForm?.Add(DateTime.Now, conn.MachineName, text);
                _status.Text = $"⚠ Alert — {conn.MachineName}: {act.WindowTitle}";
            }
        });
    }

    private void RefreshActivityState()
    {
        foreach (var (host, conn) in _conns)
        {
            if (_tiles.TryGetValue(host, out var tile))
                tile.SetActive(conn.LastActivityAt);
        }
    }

    private void SetSnapshots(int minutes)
    {
        if (minutes <= 0) { _snapTimer.Stop(); _status.Text = "Snapshots off."; return; }

        if (_snapFolder is null)
        {
            using var dlg = new FolderBrowserDialog { Description = "Where to save periodic snapshots" };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            _snapFolder = dlg.SelectedPath;
        }
        _snapTimer.Interval = minutes * 60 * 1000;
        _snapTimer.Start();
        _status.Text = $"Snapshots every {minutes} min → {_snapFolder}";
    }

    private void SnapshotTick()
    {
        if (_snapFolder is null) return;
        int n = 0;
        foreach (var c in _conns.Values)
        {
            var frame = c.LastFrame;
            if (frame is null) continue;
            try
            {
                Directory.CreateDirectory(_snapFolder);
                string safe = string.Concat(c.MachineName.Split(Path.GetInvalidFileNameChars()));
                File.WriteAllBytes(Path.Combine(_snapFolder, $"{safe}_{DateTime.Now:yyyyMMdd_HHmmss}.jpg"), frame);
                n++;
            }
            catch { }
        }
        if (n > 0) _status.Text = $"Saved {n} snapshot(s) → {_snapFolder}";
    }

    private void EditKeywords()
    {
        var s = Prompt.Show("Alert keywords",
            "Comma-separated keywords to alert on (window title or app). Empty to clear:",
            string.Join(", ", _alertKeywords));
        if (s is null) return;

        _alertKeywords.Clear();
        _alertKeywords.AddRange(ParseList(s));
        if (_alertKeywords.Count == 0)
        {
            foreach (var t in _tiles.Values) t.SetAlert(false);
            _status.Text = "Alerts cleared.";
        }
        else
        {
            _status.Text = $"Alerting on {_alertKeywords.Count} keyword(s).";
        }
    }

    private void ShowAlerts()
    {
        _alertsForm ??= new AlertsForm();
        if (!_alertsForm.Visible) _alertsForm.Show(this);
        else _alertsForm.BringToFront();
    }

    private void ShowUsage() => new UsageReportForm(_usage.Rows()) { Owner = this }.Show();

    private void ToggleBroadcast(bool on)
    {
        if (_studentBroadcasting)
        {
            ToggleStudentBroadcast(false);
        }

        _broadcasting = on;
        _broadcastBtn.Text = on ? "📡  Stop broadcast" : "📡  Broadcast my screen";
        foreach (var c in _conns.Values) _ = c.SetBroadcastAsync(on);
        if (on) _broadcastTimer.Start(); else _broadcastTimer.Stop();
    }

    private void ToggleStudentBroadcast(bool on)
    {
        if (on)
        {
            if (_broadcasting) ToggleBroadcast(false);

            var source = ResolveStudentBroadcastSource();
            if (source is null)
            {
                _studentBroadcastBtn.Checked = false;
                MessageBox.Show("Select a single student first, then use this option.", "Show selected student",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _studentBroadcastSource = source;
            _studentBroadcasting = true;
            _studentBroadcastBtn.Text = "📺  Stop selected-student broadcast";
            foreach (var c in _conns.Values.Where(c => c != source)) _ = c.SetBroadcastAsync(true);
            _studentBroadcastTimer.Start();
            return;
        }

        _studentBroadcasting = false;
        _studentBroadcastSource = null;
        _studentBroadcastBtn.Text = "📺  Show selected student";
        foreach (var c in _conns.Values) _ = c.SetBroadcastAsync(false);
        _studentBroadcastTimer.Stop();
    }

    private AgentConnection? ResolveStudentBroadcastSource()
    {
        if (_selected.Count == 1)
            return _conns.GetValueOrDefault(_selected.First());

        if (_selected.Count == 0 && _conns.Count == 1)
            return _conns.Values.FirstOrDefault();

        return null;
    }

    private void StudentBroadcastTick()
    {
        if (!_studentBroadcasting || _studentBroadcastSource is null || _studentBroadcastSource.LastFrame is null)
            return;

        byte[] jpeg = _studentBroadcastSource.LastFrame;
        foreach (var c in _conns.Values.Where(c => c != _studentBroadcastSource))
            _ = c.SendTeacherFrameAsync(jpeg);
    }

    private void BroadcastTick()
    {
        if (_capturing) return;
        _capturing = true;
        _ = Task.Run(() =>
        {
            try
            {
                byte[] jpeg = TeacherCapture.CaptureJpeg(maxWidth: 1280, quality: 55);
                foreach (var c in _conns.Values) _ = c.SendTeacherFrameAsync(jpeg);
            }
            catch { }
            finally { _capturing = false; }
        });
    }

    private void ApplyRefreshProfile(int index)
    {
        var profile = index switch
        {
            0 => (maxWidth: 400, quality: 30, intervalMs: 1500),
            2 => (maxWidth: 1280, quality: 70, intervalMs: 500),
            _ => (maxWidth: 640, quality: 50, intervalMs: 1000)
        };

        foreach (var c in _conns.Values)
            _ = c.RequestScreenAsync(profile.maxWidth, profile.quality, profile.intervalMs);
    }

    private void ApplyTileSize(int index)
    {
        _tileSize = index switch { 0 => new Size(200, 150), 2 => new Size(360, 260), _ => new Size(280, 205) };
        _grid.SuspendLayout();
        foreach (var tile in _tiles.Values) tile.SetTileSize(_tileSize);
        _grid.ResumeLayout();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _updates.Dispose();
        _broadcastTimer.Stop();
        _studentBroadcastTimer.Stop();
        _activityTimer.Stop();
        _snapTimer.Stop();
        _alertsForm?.Dispose();
        _discovery.Dispose();
        foreach (var r in _recorders.Values) r.Dispose();
        foreach (var c in _conns.Values) c.Dispose();
        base.OnFormClosed(e);
    }
}
