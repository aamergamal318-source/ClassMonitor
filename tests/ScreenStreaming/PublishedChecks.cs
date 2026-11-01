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
        // Read existing settings without modifying them or printing the class password.
        var config = File.Exists(AppConfig.AgentConfigPath)
            ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(AppConfig.AgentConfigPath))!
            : new AppConfig();
        using (var probe = new TcpListener(IPAddress.Loopback, config.Port)) probe.Start();
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
            await Task.WhenAll(Enumerable.Range(0, 3).Select(async index =>
            {
                using var conn = new AgentConnection("127.0.0.1", "Published test", config.Port,
                    Security.DeriveKey(config.Password));
                var frames = Channel.CreateUnbounded<byte[]>();
                conn.FrameReceived += (_, jpeg) => frames.Writer.TryWrite(jpeg);
                using var tile = new AgentTile(conn, new Size(400, 260));
                using var live = new LiveViewForm(conn);
                live.Show();
                Program.ShowFixture();
                await conn.ConnectAsync().WaitAsync(timeout.Token);
                await conn.RequestScreenAsync(1280, 75, 200);
                for (int i = 0; i < 10; i++)
                {
                    tile.SetFrame(await frames.Reader.ReadAsync(timeout.Token));
                    await TransportChecks.CheckViewsAsync(tile, live, true, timeout.Token);
                }
                live.Close();
                Console.WriteLine($"Published agent, client {index + 1}: PASS (10 desktop frames in both views)");
            }));
            Console.WriteLine("PASS: published self-contained agent; 3 TLS clients; 30 real desktop frames rendered in both views.");

            async Task CheckMainFormAsync()
            {
                using var main = new MainForm();
                typeof(MainForm).GetField("_key", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .SetValue(main, Security.DeriveKey(config.Password));
                main.Show();
                typeof(MainForm).GetMethod("AddOrConnect", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(main, new object[] { "127.0.0.1", "Published agent UI test", config.Port });
                var tiles = (Dictionary<string, AgentTile>)typeof(MainForm)
                    .GetField("_tiles", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
                for (int i = 0; i < 10; i++)
                {
                    await Task.Delay(1100, timeout.Token);
                    if (!tiles.TryGetValue("127.0.0.1", out var tile)) throw new Exception("MainForm dropped the agent connection");
                    TransportChecks.CheckPicture(tile.Controls.OfType<PictureBox>().Single(), true);
                }
                main.Close();
                Console.WriteLine("Main teacher console: PASS (10 real desktop frames rendered through production event handlers)");
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync();
        }
    }
}
