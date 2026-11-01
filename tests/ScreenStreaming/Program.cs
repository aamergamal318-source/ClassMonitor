using System.Drawing;
using System.Reflection;
using ClassMonitor.Shared;

internal static class Program
{
    private static Form? _fixture;
    internal static void ShowFixture()
    {
        _fixture!.TopMost = false;
        _fixture.TopMost = true;
        _fixture.BringToFront();
        _fixture.Update();
    }
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var fixture = new Form
        {
            Text = "ClassMonitor screen streaming regression test",
            BackColor = Color.Magenta, TopMost = true,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(80, 80, 600, 400)
        };
        _fixture = fixture;
        if (args.Contains("--layered")) fixture.Opacity = 0.95;
        fixture.Shown += async (_, _) =>
        {
            try
            {
                if (args.Contains("--transport"))
                {
                    await TransportChecks.RunAsync();
                    return;
                }
                if (args.Contains("--end-to-end"))
                {
                    await Task.Delay(500);
                    await TransportChecks.RunAsync(captureDesktop: true);
                    return;
                }
                if (args.Contains("--published"))
                {
                    await Task.Delay(500);
                    await PublishedChecks.RunAsync();
                    return;
                }
                await Task.Delay(500);
                var capture = Assembly.Load("ClassMonitorAgent").GetType("ClassMonitor.Agent.ScreenCapture", true)!;
                Console.WriteLine($"Fixture={fixture.Bounds}, virtual={SystemInformation.VirtualScreen}, visible={fixture.Visible}, monitors={string.Join(';', Screen.AllScreens.Select(s => s.Bounds))}");
                for (int i = 0; i < 3; i++)
                {
                    if (args.Contains("--diagnostics"))
                    {
                        var result = await Task.Run(() => ClassMonitor.Agent.CaptureDiagnostics.Collect(-1));
                        using (result.Preview)
                        {
                            Console.WriteLine(result.Report);
                            if (!result.Report.Contains("Windows CopyFromScreen: OK"))
                                throw new Exception("Windows capture probe failed");
                        }
                    }
                    var jpeg = await Task.Run(() => (byte[])capture.GetMethod("CaptureJpeg")!.Invoke(null, new object[] { 1280, 85, -1 })!);
                    using var ms = new MemoryStream(jpeg);
                    using var bitmap = new Bitmap(ms);
                    Directory.CreateDirectory("tests/ScreenStreaming/artifacts");
                    bitmap.Save("tests/ScreenStreaming/artifacts/capture.png");
                    int colored = 0;
                    for (int y = 0; y < bitmap.Height; y += 3)
                    for (int x = 0; x < bitmap.Width; x += 3)
                    {
                        var c = bitmap.GetPixel(x, y);
                        if (c.R > 180 && c.G < 80 && c.B > 180) colored++;
                    }
                    Console.WriteLine($"Capture {i + 1}: {bitmap.Width}x{bitmap.Height}, magenta samples={colored}, bytes={jpeg.Length}");
                    if (colored < 100) throw new Exception("Desktop fixture is missing from the captured screen.");
                    await Task.Delay(150);
                }
                Console.WriteLine("PASS: desktop capture includes the visible fixture on three consecutive frames.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                Environment.ExitCode = 1;
            }
            finally { fixture.Close(); }
        };
        Application.Run(fixture);
    }
}
