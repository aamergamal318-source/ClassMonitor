using System.Drawing;
using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using ClassMonitor.Shared;
using ClassMonitor.Shared.Settings;
using ClassMonitor.Shared.Management;
using ClassMonitor.Shared.Sessions;
using ClassMonitor.Teacher.Management;
using ClassMonitor.Teacher.Presentation;

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

    private readonly AppConfig _config;
    private readonly Discovery _discovery = new();
    private readonly Dictionary<string, AgentConnection> _conns = new();
    private readonly Dictionary<string, AgentTile> _tiles = new();
    private readonly HashSet<string> _selected = new();
    private readonly HashSet<string> _locked = new();
    private readonly Dictionary<string, Recorder> _recorders = new();
    private readonly GroupsStore _groups;
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
    private readonly DashboardFilterBar _filter = new();
    private Panel _classroomPanel = null!;
    private readonly Dictionary<string, (string Machine, string DeviceId, int Port)> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PairedDevice> _pairings = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LatestFrameDispatcher> _renderers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LiveViewForm> _liveViews = new(StringComparer.OrdinalIgnoreCase);
    private readonly ReconnectSchedule _reconnect = new();
    private readonly System.Windows.Forms.Timer _retryTimer = new() { Interval = 2000 };
    private readonly Dictionary<string, DateTime> _focusExpires = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime = new();
    private X509Certificate2? _teacherIdentity;
    private bool _closing, _applyingPreferences;
    private readonly ToolStripButton _lessonBtn;
    private readonly ToolStripButton _endLessonBtn;
    private DateTimeOffset? _lessonUntil;
    private string? _lessonClassroom;
    private int _maintenanceTicks;
    private readonly HashSet<string> _endedByStudent = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _startServices;
    private X509Certificate2 TeacherIdentity => _teacherIdentity ??= WindowsIdentityStore.GetOrCreate("Teacher");

    public MainForm(AppConfig? config = null, bool startServices = true, GroupsStore? groups = null)
    {
        _config = config ?? AppConfig.Load(AppConfig.TeacherConfigPath);
        _config.Settings.EnsureValid();
        _groups = groups ?? GroupsStore.Load();
        _startServices = startServices;
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

        _lessonBtn = TextButton("Start lesson", "Authorize a bounded lesson on the target machines; nothing is viewed or controlled without one", (_, _) => StartLesson());
        tools.Items.Add(_lessonBtn);
        _endLessonBtn = TextButton("End lesson", "End the lesson: capture stops and restrictions are restored", (_, _) => EndLesson());
        _endLessonBtn.Visible = false;
        tools.Items.Add(_endLessonBtn);
        tools.Items.Add(new ToolStripSeparator());
        tools.Items.Add(TextButton("🔍  Live view", "Open live view & remote control for the selected machine", (_, _) => OpenLiveForSelection()));
        tools.Items.Add(new ToolStripSeparator());
        tools.Items.Add(TextButton("💬  Message", "Show a message on the target machines", (_, _) => MessageTargets()));
        tools.Items.Add(TextButton("Question", "Ask the target students a question; answers appear in the alerts window", (_, _) => AskTargets()));
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
        tools.Items.Add(TextButton("Add computer", "Add a computer with its address and password", (_, _) => AddComputerDialog()));
        tools.Items.Add(TextButton("☑  Select all", "Select every machine", (_, _) => SelectAll(true)));
        tools.Items.Add(TextButton("☐  Clear", "Clear the selection", (_, _) => SelectAll(false)));
        tools.Items.Add(new ToolStripSeparator());
        tools.Items.Add(TextButton("⚙  Settings", "Validated console and classroom preferences", (_, _) => EditSettings()));

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
        _grid.AllowDrop = true;
        _grid.DragEnter += (_, e) => e.Effect = e.Data?.GetData(DataFormats.Text) is string host && _tiles.ContainsKey(host) ? DragDropEffects.Move : DragDropEffects.None;
        _grid.DragDrop += OnTileDropped;
        _filter.FilterChanged += (_, _) => ApplyDashboardFilter();

        // ---- Status bar ----------------------------------------------------
        var statusStrip = new StatusStrip { BackColor = Color.FromArgb(246, 247, 249) };
        _status = new ToolStripStatusLabel("Use Add computer to add a student computer.") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _selStatus = new ToolStripStatusLabel("0 selected");
        statusStrip.Items.Add(_status);
        statusStrip.Items.Add(_selStatus);
        var updates = new ToolStripButton("التحديثات / Updates");
        statusStrip.Items.Add(updates);
        _updates = new ClassMonitor.Desktop.UpdateController(ClassMonitor.Shared.Updates.UpdateComponent.Teacher,
            this, updates, Close);
        Shown += async (_, _) => { if (_startServices) await _updates.CheckAtStartupAsync(); };

        Controls.Add(_grid);
        Controls.Add(_filter);
        _classroomPanel = BuildGroupsPanel();
        Controls.Add(_classroomPanel);
        Controls.Add(tools);
        Controls.Add(BuildHeader());   // added after tools so it sits above the toolbar
        Controls.Add(statusStrip);

        _broadcastTimer.Tick += (_, _) => BroadcastTick();
        _studentBroadcastTimer.Tick += (_, _) => StudentBroadcastTick();
        _activityTimer.Tick += (_, _) =>
        {
            RefreshActivityState();
            if (++_maintenanceTicks % 15 == 0 || _recorders.Count > 0) MaintainRecordings();
        };
        _snapTimer.Tick += (_, _) => SnapshotTick();
        _retryTimer.Tick += (_, _) => RetryKnownDevices();
        _discovery.AgentSeen += OnAgentSeen;
        Load += (_, _) =>
        {
            ApplyPreferences();
            if (!_startServices) return;
            _activityTimer.Start();
            _retryTimer.Start();
            RestartDiscovery();
            MaintainRecordings();
            foreach (var peer in _config.PairedDevices) AddOrConnect(peer.Host, peer.Host, peer.Port, peer.Enrollment.DeviceId);
        };
        ApplyPreferences();
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

    /// <summary>Only computers the teacher added appear. A beacon just helps find an added computer whose address changed; its pin is still checked.</summary>
    private void OnAgentSeen(string host, string machine, string status, int port, string deviceId)
        => QueueUi(() =>
        {
            var known = _config.PairedDevices.FirstOrDefault(p => p.Enrollment.DeviceId == deviceId);
            if (known is null) return;
            if (!string.Equals(known.Host, host, StringComparison.OrdinalIgnoreCase) && !_conns.ContainsKey(known.Host))
            {
                RemoveTile(known.Host);
                _config.PairedDevices[_config.PairedDevices.IndexOf(known)] = known with { Host = host };
                SavePreferencesQuietly();
            }
            AddOrConnect(host, machine, port, deviceId);
        });

    private void RemoveTile(string host)
    {
        if (_tiles.Remove(host, out var tile)) { _grid.Controls.Remove(tile); tile.Dispose(); }
        _pairings.Remove(host);
        _selected.Remove(host);
    }

    private void RemoveComputer(string host)
    {
        if (MessageBox.Show(this, $"Remove {DeviceName(host)} from this console?", "Remove computer", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (_conns.Remove(host, out var conn))
        {
            if (_recorders.Remove(host, out var recorder)) recorder.Dispose();
            if (_renderers.TryRemove(host, out var renderer)) renderer.Dispose();
            if (_liveViews.Remove(host, out var live)) live.Close();
            conn.Dispose();
        }
        _config.PairedDevices.RemoveAll(p => string.Equals(p.Host, host, StringComparison.OrdinalIgnoreCase));
        SavePreferencesQuietly();
        RemoveTile(host);
        _reconnect.Connected(host);
        ApplyDashboardFilter();
    }

    private void AddComputerDialog()
    {
        X509Certificate2 identity;
        try { identity = TeacherIdentity; }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or InvalidOperationException)
        {
            MessageBox.Show(this, ex.Message, "Add computer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        using var form = new AddComputerForm(identity, _config.Settings, _config.PairedDevices.LastOrDefault()?.Enrollment.ClassroomId);
        if (form.ShowDialog(this) != DialogResult.OK || form.Added is not { } device) return;
        SaveAddedComputer(device);
    }

    private void SaveAddedComputer(PairedDevice device)
    {
        EnrollmentWorkflow.Save(_config.PairedDevices, device);
        try { _config.Save(AppConfig.TeacherConfigPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(this, ex.Message, "Add computer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        _reconnect.Connected(device.Host);
        AddOrConnect(device.Host, device.Host, device.Port, device.Enrollment.DeviceId);
    }

    private void AddOrConnect(string host, string machine, int port, string? deviceId = null)
    {
        if (_closing || _conns.ContainsKey(host) || host.Length > 253) return;
        if (!_config.PairedDevices.Any(p => p.Enrollment.DeviceId == deviceId || string.Equals(p.Host, host, StringComparison.OrdinalIgnoreCase))) return;
        if (!_seen.ContainsKey(host) && _seen.Count >= 500) return;
        _seen[host] = (machine, deviceId ?? "", port);
        var pairing = PairingLookup.Find(_config.PairedDevices, host, deviceId);
        if (!_tiles.TryGetValue(host, out var tile))
        {
            tile = new AgentTile(host, machine, _tileSize) { Tag = host };
            tile.Clicked += ctrl => OnTileClicked(host, ctrl);
            tile.OpenRequested += () =>
            {
                if (_conns.TryGetValue(host, out var current) && current.HasPermission(ClassroomPermission.View)) OpenLive(current);
                else _status.Text = UiText.Text("Enrollment and an active lesson are required", _config.Settings.General.Language);
            };
            tile.ApplyPreferences(_config.Settings);
            var removeOnly = new ContextMenuStrip();
            removeOnly.Items.Add("Remove computer", null, (_, _) => RemoveComputer(host));
            tile.ContextMenuStrip = removeOnly;
            _tiles[host] = tile;
            _grid.Controls.Add(tile);
        }
        if (pairing is null)
        {
            bool expired = _config.PairedDevices.Any(p => (p.Enrollment.DeviceId == deviceId || string.Equals(p.Host, host, StringComparison.OrdinalIgnoreCase)) && PairingLookup.IsExpired(p));
            tile.SetPresence("pending", expired ? "Enrollment expired. Enroll this device again." : "Enrollment and an active lesson are required");
            ApplyDashboardFilter();
            return;
        }
        _pairings[host] = pairing;
        if (!_startServices || !_reconnect.CanTry(host, DateTimeOffset.UtcNow)) return;
        AgentConnection conn;
        try { conn = new AgentConnection(host, machine, pairing, TeacherIdentity, _config.Settings); }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or InvalidOperationException)
        {
            tile.SetPresence("unhealthy", ex.Message);
            _reconnect.Failed(host, DateTimeOffset.UtcNow);
            return;
        }
        _conns[host] = conn;
        tile.SetPresence("connecting");
        tile.ContextMenuStrip?.Dispose();
        tile.ContextMenuStrip = BuildTileMenu(conn);
        _renderers[host] = new LatestFrameDispatcher(this, bytes =>
        {
            if (_conns.TryGetValue(host, out var current) && ReferenceEquals(current, conn) && _tiles.TryGetValue(host, out var target)) target.SetFrame(bytes);
        });
        conn.FrameReceived += OnFrame;
        conn.Disconnected += OnDisconnected;
        conn.FileCollected += OnFileCollected;
        conn.ActivityReceived += OnActivity;
        conn.AnswerReceived += OnAnswer;
        conn.StatusChanged += OnConnectionStatusChanged;
        conn.CommandFeedback += OnCommandFeedback;
        ApplyDashboardFilter();
        _ = ConnectAsync(conn);
    }

    private async Task ConnectAsync(AgentConnection conn)
    {
        try
        {
            await conn.ConnectAsync();
            if (_closing) return;
            _reconnect.Connected(conn.Host);
            if (_tiles.TryGetValue(conn.Host, out var tile)) tile.SetPresence("online");
            // A reconnect during a running lesson asks for a new session; the agent never resumes an old one.
            if (_lessonUntil > DateTimeOffset.UtcNow && !_endedByStudent.Contains(conn.Host)) await BeginLessonAsync(conn);
            ApplyDashboardFilter();
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            conn.Fail(ex.Message);
            QueueUi(() => _status.Text = $"{conn.MachineName}: {ex.Message}");
            OnDisconnected(conn);
        }
    }

    private async Task BeginLessonAsync(AgentConnection conn)
    {
        if (_lessonUntil is not { } until || conn.Enrollment.ClassroomId != _lessonClassroom || !await conn.StartLessonAsync(until)) return;
        if (conn.HasPermission(ClassroomPermission.View)) await conn.RequestAdaptiveStreamAsync(_conns.Count);
        if (_broadcasting) await conn.SetBroadcastAsync(true);
        if (_snapTimer.Enabled && conn.HasPermission(ClassroomPermission.Record)) await conn.SetRecordingAsync(true);
    }

    private void StartLesson()
    {
        if (_conns.Count == 0)
        {
            _status.Text = UiText.Text("Enrollment and an active lesson are required", _config.Settings.General.Language);
            return;
        }
        var classrooms = _conns.Values.Select(c => c.Enrollment.ClassroomId).Distinct().OrderBy(c => c).ToList();
        string classroom = classrooms[0];
        if (classrooms.Count > 1)
        {
            var chosen = Prompt.Show("Start lesson", $"Classroom for this lesson ({string.Join(", ", classrooms)}):", classroom);
            if (chosen is null) return;
            if (!classrooms.Contains(chosen.Trim()))
            {
                _status.Text = "Choose one of the listed classrooms.";
                return;
            }
            classroom = chosen.Trim();
        }
        var text = Prompt.Show("Start lesson", $"Lesson length for {classroom} in minutes (1-480):", "45");
        if (text is null) return;
        if (!int.TryParse(text, out int minutes) || minutes is < 1 or > 480)
        {
            _status.Text = "Lesson length must be between 1 and 480 minutes.";
            return;
        }
        _lessonUntil = DateTimeOffset.UtcNow.AddMinutes(minutes);
        _lessonClassroom = classroom;
        _endedByStudent.Clear();
        _lessonBtn.Visible = false;
        _endLessonBtn.Visible = true;
        foreach (var conn in _conns.Values.ToList()) _ = BeginLessonAsync(conn);
        _status.Text = $"Lesson running until {_lessonUntil.Value.LocalDateTime:t}.";
    }

    /// <summary>Each agent stops capture, restores what the session owns and closes; thumbnails are cleared.</summary>
    private void EndLesson()
    {
        _lessonUntil = null;
        _lessonClassroom = null;
        _endedByStudent.Clear();
        _lessonBtn.Visible = true;
        _endLessonBtn.Visible = false;
        if (_broadcastBtn.Checked) _broadcastBtn.Checked = false;
        if (_studentBroadcastBtn.Checked) _studentBroadcastBtn.Checked = false;
        _snapTimer.Stop();
        foreach (var conn in _recorders.Keys.Where(_conns.ContainsKey).Select(h => _conns[h]).ToList()) StopRecording(conn);
        foreach (var conn in _conns.Values.ToList()) _ = conn.EndLessonAsync();
        _status.Text = "Lesson ended.";
    }

    private void OnFrame(AgentConnection conn, byte[] jpeg)
    {
        if (_renderers.TryGetValue(conn.Host, out var dispatcher)) dispatcher.Submit(jpeg);
    }

    private void OnDisconnected(AgentConnection conn)
    {
        QueueUi(() =>
        {
            if (!_conns.TryGetValue(conn.Host, out var current) || !ReferenceEquals(current, conn)) return;
            if (_recorders.Remove(conn.Host, out var rec)) rec.Dispose();
            if (_renderers.TryRemove(conn.Host, out var renderer)) renderer.Dispose();
            if (_liveViews.Remove(conn.Host, out var live)) live.Close();
            if (conn.EndedByStudent)
            {
                // The student's decision stands until the teacher starts another lesson.
                _endedByStudent.Add(conn.Host);
                _status.Text = $"{conn.MachineName}: the student ended this session.";
            }
            if (_tiles.TryGetValue(conn.Host, out var tile)) { tile.SetPresence("offline", conn.EndedByStudent ? "The student ended this session." : conn.LastReadError); tile.SetLive(false); }
            if (_conns.Remove(conn.Host, out var c)) c.Dispose();
            _reconnect.Failed(conn.Host, DateTimeOffset.UtcNow);
            _selected.Remove(conn.Host);
            _locked.Remove(conn.Host);
            _focusExpires.Remove(conn.Host);

            if (_studentBroadcastSource == conn)
            {
                ToggleStudentBroadcast(false);
            }

            ApplyDashboardFilter();
        });
    }

    private void QueueUi(Action action)
    {
        if (_closing || IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(() => { if (!_closing && !IsDisposed) action(); }); }
        catch (InvalidOperationException) { }
    }

    private void OnCommandFeedback(AgentConnection conn, string message)
        => QueueUi(() => _status.Text = message);

    private void OnConnectionStatusChanged(AgentConnection conn)
        => QueueUi(() =>
        {
            if (!conn.HasPermission(ClassroomPermission.Record) && _recorders.ContainsKey(conn.Host)) StopRecording(conn);
            if (!conn.HasPermission(ClassroomPermission.Assist) && _liveViews.TryGetValue(conn.Host, out var live)) live.ReleaseAssistance();
            RefreshActivityState();
        });

    private void RetryKnownDevices()
    {
        if (_closing || !_startServices) return;
        if (_lessonUntil <= DateTimeOffset.UtcNow) EndLesson();
        foreach (var pair in _config.PairedDevices)
        {
            string host = _seen.FirstOrDefault(x => x.Value.DeviceId == pair.Enrollment.DeviceId).Key ?? pair.Host;
            if (_conns.ContainsKey(host) || !_reconnect.CanTry(host, DateTimeOffset.UtcNow)) continue;
            // Another enrolled account is signed in on that computer; its pin would not match this record.
            if (_seen.TryGetValue(host, out var current) && current.DeviceId.Length != 0 && current.DeviceId != pair.Enrollment.DeviceId) continue;
            AddOrConnect(host, _seen.TryGetValue(host, out var seen) ? seen.Machine : pair.Enrollment.DeviceId, pair.Port, pair.Enrollment.DeviceId);
        }
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
            if (source != _studentBroadcastSource) _studentBroadcastBtn.Checked = false;
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
        _status.Text = _config.PairedDevices.Count == 0 ? "Use Add computer to add a student computer."
            : $"{_conns.Count} of {_config.PairedDevices.Count} computer(s) connected";
        _selStatus.Text = _selected.Count == 0 ? "targets: all" : $"{_selected.Count} selected";
    }

    // ---- Per-tile context menu -------------------------------------------

    private ContextMenuStrip BuildTileMenu(AgentConnection conn)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Live view && remote control", null, (_, _) => OpenLive(conn));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Send message…", null, (_, _) => { var t = Prompt.Show("Message", $"Message for {conn.MachineName}:"); if (!string.IsNullOrWhiteSpace(t)) _ = conn.SendMessageAsync(t, 8000); });
        menu.Items.Add("Lock screen", null, (_, _) => { if (conn.HasPermission(ClassroomPermission.Focus) && ConfirmFocus("Lock screens", 1)) { _locked.Add(conn.Host); _tiles[conn.Host].SetLocked(true); _ = conn.LockAsync(true, "Screen locked by the teacher."); } });
        menu.Items.Add("Unlock screen", null, (_, _) => { if (conn.HasPermission(ClassroomPermission.Focus)) { _locked.Remove(conn.Host); _tiles[conn.Host].SetLocked(false); _ = conn.LockAsync(false, ""); } });
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
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Remove computer", null, (_, _) => RemoveComputer(conn.Host));

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
        if (!conn.HasPermission(ClassroomPermission.View))
        {
            _status.Text = UiText.Text("Enrollment and an active lesson are required", _config.Settings.General.Language);
            return;
        }
        if (_liveViews.TryGetValue(conn.Host, out var existing)) { existing.BringToFront(); return; }
        if (_tiles.TryGetValue(conn.Host, out var tile)) tile.SetLive(true);
        var form = new LiveViewForm(conn) { Owner = this };
        _liveViews[conn.Host] = form;
        form.FormClosed += (_, _) =>
        {
            _liveViews.Remove(conn.Host);
            if (_tiles.TryGetValue(conn.Host, out var t)) t.SetLive(false);
        };
        form.Show();
    }

    private void MessageTargets()
    {
        var text = Prompt.Show("Message", "Message to show on the target screens:");
        if (string.IsNullOrWhiteSpace(text)) return;
        foreach (var c in Targets()) _ = c.SendMessageAsync(text, 8000);
    }

    private void AskTargets()
    {
        var text = Prompt.Show("Question", "Question for the target students:");
        if (string.IsNullOrWhiteSpace(text)) return;
        ShowAlerts();
        foreach (var c in Targets()) _ = c.AskQuestionAsync(text);
    }

    private void OnAnswer(AgentConnection conn, AnswerMsg answer) =>
        QueueUi(() => _alertsForm?.Add(DateTime.Now, conn.MachineName, "Answer: " + answer.Text));

    private bool ConfirmFocus(string action, int devices) =>
        !_config.Settings.Policies.ConfirmBeforeFocus || devices == 0 || MessageBox.Show(
            $"{action} on {devices} machine(s)? It ends with the lesson or after {_config.Settings.Policies.MaximumFocusMinutes} minutes.",
            "Confirm focus", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    private void LockTargets(bool locked)
    {
        string text = locked ? "Screen locked by the teacher." : "";
        var targets = Targets().Where(c => c.HasPermission(ClassroomPermission.Focus)).ToList();
        if (locked && !ConfirmFocus("Lock screens", targets.Count)) return;
        foreach (var c in targets)
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
        if (bytes.LongLength > _config.Settings.Files.MaximumTransferBytes)
        {
            MessageBox.Show($"File is too large (the limit is {_config.Settings.Files.MaximumTransferMiB} MiB).", "Send file",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _ = conn.SendFileAsync(Path.GetFileName(dlg.FileName), bytes);
        _status.Text = $"Sent \"{Path.GetFileName(dlg.FileName)}\" to {conn.MachineName}.";
    }

    private void CollectFileOne(AgentConnection conn)
    {
        var path = Prompt.Show("Collect file", $"File to collect from the approved lesson folder on {conn.MachineName}:",
            "assignment.docx");
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

        if (bytes.LongLength > _config.Settings.Files.MaximumTransferBytes)
        {
            MessageBox.Show($"File is too large (the limit is {_config.Settings.Files.MaximumTransferMiB} MiB).", "Send file",
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

        var path = Prompt.Show("Collect file", "File to collect from each student's approved lesson folder:",
            "assignment.docx");
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
                WriteNewFile(_collectFolder, $"{safeMachine}_{Path.GetFileName(data.FileName)}", Convert.FromBase64String(data.DataBase64));
                _collectCount++;
                _status.Text = $"Collected {_collectCount} file(s) → {_collectFolder}";
            }
            catch (Exception ex) { _status.Text = $"{conn.MachineName}: save failed — {ex.Message}"; }
        });
    }

    /// <summary>A name sent by a device never overwrites a file; a repeat gets a numbered name.</summary>
    private static void WriteNewFile(string folder, string name, byte[] bytes)
    {
        string stem = Path.GetFileNameWithoutExtension(name), extension = Path.GetExtension(name);
        for (int suffix = 0; suffix < 10000; suffix++)
        {
            string candidate = ApprovedFilePaths.ResolveFileName(folder, suffix == 0 ? name : $"{stem} ({suffix}){extension}");
            try
            {
                using var output = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                output.Write(bytes);
                return;
            }
            catch (IOException) when (File.Exists(candidate)) { }
        }
        throw new IOException("Too many files share this name.");
    }

    /// <summary>Retention runs without waiting for a new recording, and the storage limit also holds during one.</summary>
    private void MaintainRecordings()
    {
        var settings = _config.Settings.Recording;
        try
        {
            RecordingRetention.PruneExpired(settings.Directory, settings.RetentionDays, DateTimeOffset.UtcNow);
            if (RecordingRetention.OwnedStorageBytes(settings.Directory) < settings.MaximumStorageMiB * 1024L * 1024) return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return; }
        if (_recorders.Count == 0 && !_snapTimer.Enabled) return;
        _snapTimer.Stop();
        foreach (var conn in _recorders.Keys.Where(_conns.ContainsKey).Select(h => _conns[h]).ToList()) StopRecording(conn);
        foreach (var conn in _conns.Values.Where(c => c.Recording).ToList()) _ = conn.SetRecordingAsync(false);
        _status.Text = "The recording storage limit is reached, so recording stopped.";
    }

    /// <summary>Opt-in, per device: console preference, explicit confirmation, granted capability and the agent's visible state.</summary>
    private async void StartRecording(AgentConnection conn)
    {
        if (_recorders.ContainsKey(conn.Host)) return;
        var settings = _config.Settings.Recording;
        if (!settings.AllowRecording)
        {
            _status.Text = "Recording is off. Enable it in Settings before starting.";
            return;
        }
        if (!conn.HasPermission(ClassroomPermission.Record))
        {
            _status.Text = "Operation is not authorized by classroom/session policy.";
            return;
        }
        if (MessageBox.Show($"Record {conn.MachineName}? The student sees a recording notice. Files stay in the recording folder for {settings.RetentionDays} day(s).",
            "Confirm recording", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        string path;
        try
        {
            RecordingRetention.PruneExpired(settings.Directory, settings.RetentionDays, DateTimeOffset.UtcNow);
            if (RecordingRetention.OwnedStorageBytes(settings.Directory) >= settings.MaximumStorageMiB * 1024L * 1024)
            {
                _status.Text = "The recording storage limit is reached. Remove recordings or raise the limit.";
                return;
            }
            Directory.CreateDirectory(settings.Directory);
            string safeName = string.Concat(conn.MachineName.Split(Path.GetInvalidFileNameChars()));
            path = ApprovedFilePaths.ResolveFileName(settings.Directory, $"classmonitor-{safeName}-{DateTime.Now:yyyyMMdd-HHmmss}.avi");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _status.Text = ex.Message;
            return;
        }
        if (!await conn.SetRecordingAsync(true)) return;

        // Make sure frames keep flowing at a decent rate while recording.
        _ = conn.RequestScreenAsync(maxWidth: 1280, quality: 60, intervalMs: 200);

        _recorders[conn.Host] = new Recorder(conn, path);
        if (_tiles.TryGetValue(conn.Host, out var tile)) tile.SetRecording(true);
        _status.Text = $"Recording {conn.MachineName} → {path}";
    }

    private void StopRecording(AgentConnection conn)
    {
        if (!_recorders.Remove(conn.Host, out var rec)) return;
        if (!_snapTimer.Enabled) _ = conn.SetRecordingAsync(false);
        int frames = rec.FrameCount;
        string path = rec.Path;
        rec.Dispose();
        if (_tiles.TryGetValue(conn.Host, out var tile)) tile.SetRecording(false);
        _status.Text = $"Saved recording ({frames} frames) → {path}";
    }

    private void EditSettings()
    {
        using var form = new SettingsCenterForm(_config.Settings, updated =>
        {
            _config.Settings = updated;
            _config.Save(AppConfig.TeacherConfigPath);
            ApplyPreferences();
            if (_startServices) RestartDiscovery();
        }, EnrollDevice);
        form.ShowDialog(this);
    }

    private void EnrollDevice()
    {
        X509Certificate2 identity;
        try { identity = TeacherIdentity; }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or InvalidOperationException)
        {
            MessageBox.Show(this, ex.Message, "Enroll device", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        using var form = new EnrollDeviceForm(identity, _config.Settings);
        if (form.ShowDialog(this) != DialogResult.OK || form.Enrolled is not { } device) return;
        SaveAddedComputer(device);
    }

    private void RestartDiscovery()
    {
        try { _discovery.Start(_config.Settings.Network); }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
        { _status.Text = "Discovery is unavailable: " + ex.Message; }
    }

    private void ApplyPreferences()
    {
        if (_applyingPreferences) return;
        _applyingPreferences = true;
        try
        {
            var settings = _config.Settings;
            TeacherExperience.Apply(this, settings);
            _filter.ApplyPreferences(settings);
            if (settings.Layout.RememberWindowBounds && !Visible)
                ClientSize = new Size(settings.Layout.WindowWidth, settings.Layout.WindowHeight);
            _classroomPanel.Width = settings.Layout.SidebarWidth;
            _tileSize = new Size(settings.Display.ThumbnailWidth, settings.Display.ThumbnailHeight);
            foreach (var tile in _tiles.Values) { tile.ApplyPreferences(settings); tile.SetTileSize(_tileSize); }
            ApplyDashboardFilter();
        }
        finally { _applyingPreferences = false; }
    }

    private void ApplyDashboardFilter()
    {
        var layout = _config.Settings.Layout;
        var ordered = _tiles.OrderBy(pair =>
        {
            int saved = layout.DeviceOrder.FindIndex(id => string.Equals(id, pair.Key, StringComparison.OrdinalIgnoreCase));
            return saved < 0 ? int.MaxValue : saved;
        });
        ordered = layout.DeviceSort == "presence"
            ? ordered.ThenBy(pair => pair.Value.Presence == "online" ? 0 : 1).ThenBy(pair => DeviceName(pair.Key), StringComparer.CurrentCultureIgnoreCase)
            : ordered.ThenBy(pair => DeviceName(pair.Key), StringComparer.CurrentCultureIgnoreCase);
        int visible = 0, position = 0;
        _grid.SuspendLayout();
        foreach (var (host, tile) in ordered)
        {
            _grid.Controls.SetChildIndex(tile, position++);
            string classroom = _pairings.TryGetValue(host, out var pairing) ? pairing.Enrollment.ClassroomId : "";
            tile.Visible = DashboardQuery.Matches(new DeviceSearchEntry(host, DeviceName(host), classroom, tile.Presence), _filter.SearchText, _filter.PresenceFilter);
            if (tile.Visible) visible++;
        }
        _grid.ResumeLayout();
        _filter.SetResultCount(visible, _tiles.Count);
        UpdateStatus();
    }

    private string DeviceName(string host) =>
        _conns.TryGetValue(host, out var conn) ? conn.MachineName : _seen.TryGetValue(host, out var seen) ? seen.Machine : host;

    private void OnTileDropped(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.Text) is not string host || !_tiles.TryGetValue(host, out var moved)) return;
        var over = _grid.GetChildAtPoint(_grid.PointToClient(new Point(e.X, e.Y)));
        var order = _grid.Controls.OfType<AgentTile>().Where(t => t != moved).ToList();
        int index = over is AgentTile target ? order.IndexOf(target) : order.Count;
        order.Insert(Math.Max(0, index), moved);
        _config.Settings.Layout.DeviceOrder = order.Select(t => (string)t.Tag!).Take(500).ToList();
        SavePreferencesQuietly();
        ApplyDashboardFilter();
    }

    private void SavePreferencesQuietly()
    {
        try { _config.Save(AppConfig.TeacherConfigPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { _status.Text = "Preferences were not saved: " + ex.Message; }
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
                if (_config.Settings.General.NotificationsEnabled) System.Media.SystemSounds.Exclamation.Play();
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
        if (minutes <= 0)
        {
            _snapTimer.Stop();
            foreach (var c in _conns.Values.Where(c => c.Recording && !_recorders.ContainsKey(c.Host)).ToList()) _ = c.SetRecordingAsync(false);
            _status.Text = "Snapshots off.";
            return;
        }
        // Saved images are recordings: same preference, confirmation, capability, notice, folder and retention.
        var recording = _config.Settings.Recording;
        if (!recording.AllowRecording)
        {
            _status.Text = "Recording is off. Enable it in Settings before starting.";
            return;
        }
        if (!_snapTimer.Enabled && MessageBox.Show($"Save a screen image of each authorized student every {minutes} minute(s)? Students see a recording notice. Images stay in the recording folder for {recording.RetentionDays} day(s).",
            "Confirm recording", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _snapFolder = recording.Directory;
        _snapTimer.Interval = minutes * 60 * 1000;
        _snapTimer.Start();
        foreach (var c in _conns.Values.Where(c => c.HasPermission(ClassroomPermission.Record) && !c.Recording).ToList()) _ = c.SetRecordingAsync(true);
        _status.Text = $"Snapshots every {minutes} min → {_snapFolder}";
    }

    private void SnapshotTick()
    {
        if (_snapFolder is null) return;
        int n = 0;
        foreach (var c in _conns.Values)
        {
            var frame = c.LastFrame;
            if (frame is null || !c.Recording || !c.HasPermission(ClassroomPermission.Record)) continue;
            try
            {
                Directory.CreateDirectory(_snapFolder);
                string safe = string.Concat(c.MachineName.Split(Path.GetInvalidFileNameChars()));
                WriteNewFile(_snapFolder, $"classmonitor-{safe}-{DateTime.Now:yyyyMMdd-HHmmss}.jpg", frame);
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

    private async void ToggleStudentBroadcast(bool on)
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

            if (MessageBox.Show($"Show {source.MachineName}'s screen to the class? The student sees a notice first.", "Show selected student",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes || !await source.SetSharedAsync(true))
            {
                _studentBroadcastBtn.Checked = false;
                return;
            }
            _studentBroadcastSource = source;
            _studentBroadcasting = true;
            _studentBroadcastBtn.Text = "📺  Stop selected-student broadcast";
            foreach (var c in _conns.Values.Where(c => c != source)) _ = c.SetBroadcastAsync(true);
            _studentBroadcastTimer.Start();
            return;
        }

        if (_studentBroadcastSource is { } previous) _ = previous.SetSharedAsync(false);
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
        _config.Settings.Display.ThumbnailWidth = _tileSize.Width;
        _config.Settings.Display.ThumbnailHeight = _tileSize.Height;
        SavePreferencesQuietly();
        _grid.SuspendLayout();
        foreach (var tile in _tiles.Values) tile.SetTileSize(_tileSize);
        _grid.ResumeLayout();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closing = true;
        _lifetime.Cancel();
        if (_config.Settings.Layout.RememberWindowBounds && WindowState == FormWindowState.Normal)
        {
            _config.Settings.Layout.WindowWidth = Math.Clamp(ClientSize.Width, 900, 7680);
            _config.Settings.Layout.WindowHeight = Math.Clamp(ClientSize.Height, 600, 4320);
            _config.Settings.Layout.SidebarWidth = Math.Clamp(_classroomPanel.Width, 160, 480);
            if (_startServices) SavePreferencesQuietly();
        }
        _retryTimer.Stop();
        foreach (var renderer in _renderers.Values) renderer.Dispose();
        _teacherIdentity?.Dispose();
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
