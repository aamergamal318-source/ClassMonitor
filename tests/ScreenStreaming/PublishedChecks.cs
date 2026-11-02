using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using ClassMonitor.Shared;
using ClassMonitor.Teacher;

internal static class PublishedChecks
{
    public static async Task RunAsync()
    {
        // Needs an enrolled loopback device: this account's agent enrollment and its record in teacher.json.
        var config = File.Exists(AppConfig.TeacherConfigPath) ? AppConfig.Load(AppConfig.TeacherConfigPath) : new AppConfig();
        var pairing = config.PairedDevices.FirstOrDefault(p => p.Host is "127.0.0.1" or "localhost")
            ?? throw new Exception("--published requires an enrolled loopback device in teacher.json. No device was changed.");
        using var teacher = ClassMonitor.Shared.Management.WindowsIdentityStore.GetOrCreate("Teacher");
        using var process = Process.Start(new ProcessStartInfo(
            Path.GetFullPath("release/ClassMonitor/agent/ClassMonitorAgent.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true
        })!;
        try
        {
            await Task.Delay(2000);
            if (process.HasExited) throw new Exception($"Published agent exited: {process.ExitCode}");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await CheckMainFormAsync();
            foreach (int index in Enumerable.Range(0, 3))
            {
                using var conn = new AgentConnection("127.0.0.1", "Published test", pairing, teacher, config.Settings);
                var frames = Channel.CreateUnbounded<byte[]>();
                conn.FrameReceived += (_, jpeg) => frames.Writer.TryWrite(jpeg);
                using var tile = new AgentTile(conn, new Size(400, 260));
                using var live = new LiveViewForm(conn);
                live.Show();
                Program.ShowFixture();
                await conn.ConnectAsync().WaitAsync(timeout.Token);
                if (!await conn.StartLessonAsync(DateTimeOffset.UtcNow.AddMinutes(5))) throw new Exception("Lesson refused");
                await conn.RequestScreenAsync(1280, 75, 200);
                for (int i = 0; i < 10; i++)
                {
                    tile.SetFrame(await frames.Reader.ReadAsync(timeout.Token));
                    await TransportChecks.CheckViewsAsync(tile, live, true, timeout.Token);
                }
                live.Close();
                Console.WriteLine($"Published agent, lesson {index + 1}: PASS (10 desktop frames in both views)");
                await conn.EndLessonAsync();
                await Task.Delay(500, timeout.Token);
            }
            Console.WriteLine("PASS: published self-contained agent; 3 enrolled lessons; 30 real desktop frames rendered in both views.");

            async Task CheckMainFormAsync()
            {
                using var main = new MainForm(config);
                main.Show();
                var tiles = (Dictionary<string, AgentTile>)typeof(MainForm)
                    .GetField("_tiles", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
                await Task.Delay(3000, timeout.Token);
                if (!tiles.ContainsKey(pairing.Host)) throw new Exception("MainForm did not list the enrolled device");
                main.Close();
                Console.WriteLine("Main teacher console: PASS (enrolled device listed)");
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync();
        }
    }
}
