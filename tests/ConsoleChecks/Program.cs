using System.Diagnostics;
using System.Text.Json;
using ClassMonitor.Shared.Settings;
using ClassMonitor.Teacher.Presentation;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        string root = Path.GetFullPath(Path.Combine("tests", "ConsoleChecks", "artifacts", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        int passed = 0;
        try
        {
            Check("default settings validate", () => new SchoolSettings().EnsureValid());
            Check("unsupported language, theme and schema fail closed", () =>
            {
                var settings = new SchoolSettings();
                settings.General.Language = "xx";
                settings.General.Theme = "unknown";
                settings.SchemaVersion = 99;
                Assert(settings.Validate().Count == 3);
                Reject(settings.EnsureValid);
            });
            Check("network conflicts and invalid interface rejected", () =>
            {
                var settings = new SchoolSettings();
                settings.Network.DiscoveryPort = settings.Network.AgentPort;
                settings.Network.LocalInterfaceAddress = "not-an-address";
                settings.Network.ConnectTimeoutSeconds = 0;
                settings.Network.HandshakeTimeoutSeconds = 999;
                settings.Network.HealthTimeoutSeconds = -1;
                Assert(settings.Validate().Count == 5);
            });
            Check("all display ranges and nonfinite rates rejected", () =>
            {
                foreach (double fps in new[] { 0, -1, 10.1, double.NaN, double.PositiveInfinity })
                {
                    var settings = new SchoolSettings();
                    settings.Display.FramesPerSecond = fps;
                    Reject(settings.EnsureValid);
                }
                var invalid = new SchoolSettings();
                invalid.Display.Quality = 100;
                invalid.Display.ThumbnailWidth = 1;
                invalid.Display.ThumbnailHeight = 1;
                invalid.Display.MaxFrameWidth = 9999;
                invalid.Display.BandwidthMbps = 0;
                Assert(invalid.Validate().Count == 5);
            });
            Check("unsafe policy and invalid storage bounds rejected", () =>
            {
                var settings = new SchoolSettings();
                settings.Policies.RestoreOnSessionEnd = false;
                settings.Policies.MaximumFocusMinutes = 999;
                settings.Recording.RetentionDays = 0;
                settings.Recording.MaximumStorageMiB = 1;
                settings.Files.MaximumTransferMiB = 9;
                settings.Files.ReceiveDirectory = "relative";
                Assert(settings.Validate().Count == 6);
            });
            Check("null settings groups do not silently reset", () =>
            {
                var settings = JsonSerializer.Deserialize<SchoolSettings>("{\"General\":null,\"Network\":null}")!;
                Assert(settings.Validate().Count == 2);
                Reject(settings.EnsureValid);
            });
            Check("duplicate layout identifiers and invalid sort rejected", () =>
            {
                var settings = new SchoolSettings();
                settings.Layout.DeviceOrder = new() { "pc01", "PC01" };
                settings.Layout.DeviceSort = "unknown";
                Assert(settings.Validate().Count == 2);
            });
            Check("settings clone is detached", () =>
            {
                var current = new SchoolSettings();
                current.Layout.DeviceOrder.Add("pc01");
                var copy = current.Clone();
                copy.Display.Quality = 70;
                copy.Layout.DeviceOrder.Add("pc02");
                Assert(current.Display.Quality == 50 && current.Layout.DeviceOrder.Count == 1);
            });
            Check("all setting groups persist and roundtrip", () =>
            {
                var settings = new SchoolSettings();
                settings.General.Language = "he";
                settings.General.Theme = "dark";
                settings.General.NotificationsEnabled = false;
                settings.Network.AgentPort = 6000;
                settings.Network.DiscoveryPort = 6001;
                settings.Network.DiscoveryEnabled = false;
                settings.Network.ConnectTimeoutSeconds = 12;
                settings.Network.HandshakeTimeoutSeconds = 13;
                settings.Network.HealthTimeoutSeconds = 22;
                settings.Network.LocalInterfaceAddress = "127.0.0.1";
                settings.Display.ThumbnailWidth = 320;
                settings.Display.ThumbnailHeight = 240;
                settings.Display.MaxFrameWidth = 960;
                settings.Display.Quality = 65;
                settings.Display.FramesPerSecond = 3.5;
                settings.Display.BandwidthMbps = 12;
                settings.Display.AdaptiveQuality = false;
                settings.Files.ReceiveDirectory = Path.Combine(root, "receive");
                settings.Files.CollectDirectory = Path.Combine(root, "collected");
                settings.Files.MaximumTransferMiB = 5;
                settings.Recording.AllowRecording = true;
                settings.Recording.Directory = Path.Combine(root, "recordings-preference");
                settings.Recording.RetentionDays = 12;
                settings.Recording.MaximumStorageMiB = 1024;
                settings.Layout.RememberWindowBounds = false;
                settings.Layout.WindowWidth = 1280;
                settings.Layout.WindowHeight = 900;
                settings.Layout.SidebarWidth = 270;
                settings.Layout.DeviceSort = "presence";
                settings.Layout.DeviceOrder = new() { "pc02", "pc01" };
                settings.Policies.ConfirmBeforeFocus = false;
                settings.Policies.MaximumFocusMinutes = 45;
                string path = Path.Combine(root, "profile.json");
                SchoolSettingsStore.SaveAtomic(path, settings);
                Assert(JsonSerializer.Serialize(SchoolSettingsStore.Load(path)) == JsonSerializer.Serialize(settings));
                settings.General.Language = "ar";
                SchoolSettingsStore.SaveAtomic(path, settings);
                Assert(SchoolSettingsStore.Load(path).General.Language == "ar");
                Assert(SchoolSettingsStore.Load(path + ".previous").General.Language == "he");
            });
            Check("invalid save preserves previous settings", () =>
            {
                string path = Path.Combine(root, "keep.json");
                SchoolSettingsStore.SaveAtomic(path, new SchoolSettings());
                var invalid = new SchoolSettings();
                invalid.Network.AgentPort = 0;
                Reject(() => SchoolSettingsStore.SaveAtomic(path, invalid));
                Assert(SchoolSettingsStore.Load(path).Network.AgentPort == 5900);
            });
            Check("corrupt and oversized profiles are reported without overwrite", () =>
            {
                string corrupt = Path.Combine(root, "corrupt.json");
                File.WriteAllText(corrupt, "{broken");
                Reject(() => SchoolSettingsStore.Load(corrupt));
                Assert(File.ReadAllText(corrupt) == "{broken");
                File.WriteAllText(corrupt, new string('x', SchoolSettingsStore.MaximumProfileBytes + 1));
                Reject(() => SchoolSettingsStore.Load(corrupt));
            });
            Check("wrong file types and duplicate properties cannot masquerade as preferences", () =>
            {
                string path = Path.Combine(root, "not-preferences.json");
                foreach (string json in new[] { "{}", "{\"SchoolId\":\"example\"}", "{\"SchemaVersion\":1,\"General\":{\"Language\":\"ar\",\"language\":\"en\"}}" })
                {
                    File.WriteAllText(path, json);
                    Reject(() => SchoolSettingsStore.Load(path));
                    Assert(File.ReadAllText(path) == json);
                }
            });
            Check("stream request uses valid cadence and monitor", () =>
            {
                var display = new DisplaySettings { FramesPerSecond = 2.5, AdaptiveQuality = false, BandwidthMbps = 100 };
                var request = new AdaptiveStreamProfile().CreateRequest(display, 1, 2);
                Assert(request.IntervalMs == 400 && request.Quality == 50 && request.MaxWidth == 640 && request.MonitorIndex == 2);
            });
            Check("adaptive stream reduces demand under contention", () =>
            {
                var profile = new AdaptiveStreamProfile();
                profile.ObserveFrame(500_000);
                var request = profile.CreateRequest(new DisplaySettings { FramesPerSecond = 10, BandwidthMbps = 0.25 }, 30);
                Assert(request.IntervalMs == 5000 && request.Quality < 50 && request.MaxWidth >= 320);
            });
            Check("bandwidth limiter waits and cancellation is honored", () =>
            {
                var clock = Stopwatch.StartNew();
                new BandwidthLimiter(8).WaitAsync(32_000).GetAwaiter().GetResult();
                Assert(clock.ElapsedMilliseconds >= 20);
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                try { new BandwidthLimiter(1).WaitAsync(100, cancelled.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { return; }
                throw new Exception("Expected cancellation");
            });
            Check("approved paths contain valid lesson files", () =>
            {
                string approved = Path.Combine(root, "lessons");
                Assert(ApprovedFilePaths.ResolveFileName(approved, "worksheet.txt") == Path.Combine(approved, "worksheet.txt"));
                Assert(ApprovedFilePaths.ResolveRelative(approved, "week1/worksheet.txt") == Path.Combine(approved, "week1", "worksheet.txt"));
            });
            Check("path traversal, alternate streams and reserved devices rejected", () =>
            {
                foreach (string path in new[] { "../escape.txt", "folder/../../escape.txt", "C:/escape.txt", "x:stream", "NUL.txt", "CON", "COM1.txt", "file. ", "a//b" })
                    Reject(() => ApprovedFilePaths.ResolveRelative(root, path));
                Reject(() => ApprovedFilePaths.ResolveFileName(root, "sub/file.txt"));
            });
            Check("file size setting enforces limits", () =>
            {
                var files = new FileSettings { MaximumTransferMiB = 1 };
                ApprovedFilePaths.CheckLength(1024 * 1024, files);
                Reject(() => ApprovedFilePaths.CheckLength(1024 * 1024 + 1, files));
                Reject(() => ApprovedFilePaths.CheckLength(-1, files));
            });
            Check("retention affects expired owned recordings only", () =>
            {
                string folder = Path.Combine(root, "recordings");
                Directory.CreateDirectory(folder);
                string old = Path.Combine(folder, "classmonitor-old.avi");
                string recent = Path.Combine(folder, "classmonitor-recent.avi");
                string unrelated = Path.Combine(folder, "personal.avi");
                foreach (string path in new[] { old, recent, unrelated }) File.WriteAllBytes(path, new byte[16]);
                File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));
                File.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddDays(-30));
                Assert(RecordingRetention.PruneExpired(folder, 7, DateTimeOffset.UtcNow) == 1);
                Assert(!File.Exists(old) && File.Exists(recent) && File.Exists(unrelated));
                Assert(RecordingRetention.OwnedStorageBytes(folder) == 16);
            });
            Check("all catalog entries translate to Arabic and Hebrew", () =>
            {
                foreach (string key in UiText.Keys)
                    Assert(UiText.Text(key, "ar") != key && UiText.Text(key, "he") != key);
                Assert(UiText.IsRightToLeft("ar") && UiText.IsRightToLeft("he") && !UiText.IsRightToLeft("en"));
                Assert(UiText.Text("🔍  Live view", "ar") == UiText.Text("Live view", "ar"));
            });
            Check("search matches every term and presence scope", () =>
            {
                var device = new DeviceSearchEntry("pc01", "طالب أحمد", "Room 1", "online");
                Assert(DashboardQuery.Matches(device, "أحمد Room", "online"));
                Assert(!DashboardQuery.Matches(device, "أحمد", "offline"));
                Assert(!DashboardQuery.Matches(device, "missing"));
            });
            Check("theme palettes have readable contrast", () =>
            {
                foreach (string theme in new[] { "light", "dark" })
                {
                    var palette = ThemePalette.For(theme);
                    Assert(Contrast(palette.Text, palette.Surface) >= 7);
                }
            });
            Check("settings dialog constructs headlessly and draft stays detached", () =>
            {
                foreach (string language in new[] { "en", "ar", "he" })
                {
                    var current = new SchoolSettings();
                    current.General.Language = language;
                    bool saved = false;
                    using var dialog = new SettingsCenterForm(current, _ => saved = true);
                    Assert(!dialog.Visible && !saved);
                    Assert(JsonSerializer.Serialize(dialog.Draft) == JsonSerializer.Serialize(current));
                    Assert(dialog.RightToLeft == (language == "en" ? RightToLeft.No : RightToLeft.Yes));
                    Assert(dialog.RightToLeftLayout == (language != "en"));
                }
            });
            Check("localization switches existing controls back to English", () =>
            {
                using var form = new Form { Text = "ClassMonitor — Teacher Console" };
                var strip = new ToolStrip();
                var item = new ToolStripButton("🔍  Live view");
                strip.Items.Add(item);
                form.Controls.Add(strip);
                var settings = new SchoolSettings();
                settings.General.Language = "ar";
                TeacherExperience.Apply(form, settings);
                Assert(item.Text == UiText.Text("Live view", "ar"));
                settings.General.Language = "en";
                TeacherExperience.Apply(form, settings);
                Assert(item.Text == "Live view" && item.Image is not null && !form.RightToLeftLayout && !form.Visible);
            });
            Check("all original command glyphs draw without external assets", () =>
            {
                foreach (var glyph in Enum.GetValues<CommandGlyph>())
                {
                    using var image = CommandIcons.Create(glyph, Color.RoyalBlue);
                    int drawn = 0;
                    for (int y = 0; y < image.Height; y++)
                    for (int x = 0; x < image.Width; x++) if (image.GetPixel(x, y).A > 0) drawn++;
                    Assert(drawn > 10);
                }
            });
            Check("localizing refresh selectors preserves indices and behavior", () =>
            {
                using var form = new Form();
                var strip = new ToolStrip();
                var combo = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
                combo.Items.AddRange(new object[] { "Small", "Medium", "Large" });
                combo.SelectedIndex = 1;
                int changed = 0;
                combo.SelectedIndexChanged += (_, _) => changed++;
                strip.Items.Add(combo);
                form.Controls.Add(strip);
                var settings = new SchoolSettings();
                settings.General.Language = "he";
                TeacherExperience.Apply(form, settings);
                settings.General.Language = "en";
                TeacherExperience.Apply(form, settings);
                Assert(combo.SelectedIndex == 1 && (string)combo.SelectedItem! == "Medium" && changed == 0 && !form.Visible);
            });
            Console.WriteLine($"PASS: {passed} settings/localization/operational checks. No visible windows were opened.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        void Check(string name, Action action) { action(); passed++; Console.WriteLine("PASS: " + name); }
        static void Assert(bool success) { if (!success) throw new Exception("Assertion failed"); }
        static void Reject(Action action)
        {
            try { action(); }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException) { return; }
            throw new Exception("Expected validation rejection");
        }
    }

    private static double Contrast(Color a, Color b)
    {
        static double Lum(Color c)
        {
            static double Channel(byte v) { double s = v / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }
        double x = Lum(a), y = Lum(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }
}
