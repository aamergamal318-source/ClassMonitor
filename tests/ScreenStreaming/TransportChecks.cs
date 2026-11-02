using System.Drawing.Imaging;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using ClassMonitor.Agent;
using ClassMonitor.Shared;
using ClassMonitor.Shared.Management;
using ClassMonitor.Shared.Sessions;
using ClassMonitor.Shared.Settings;
using ClassMonitor.Teacher;

internal static class TransportChecks
{
    public static async Task RunAsync(bool captureDesktop = false)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var cert = Identity("ClassMonitor test student");
        using var teacher = Identity("ClassMonitor test teacher");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var request = EnrollmentManager.CreateRequest(cert, "Test device");
        var package = EnrollmentManager.CreatePackage(request, teacher, "school", "class", "Teacher", TeacherRole.Teacher,
            ClassroomPermission.View, DateTimeOffset.UtcNow.AddHours(1));
        var enrollment = EnrollmentManager.ValidatePackage(package, request.CertificateSha256, CertificateTrust.Fingerprint(teacher));
        var pairing = new PairedDevice("127.0.0.1", port, enrollment, request.PublicCertificateBase64);
        var settings = new SchoolSettings();
        var config = new AppConfig();
        config.Settings.Network.AgentPort = port;
        config.Settings.Network.DiscoveryEnabled = false;
        byte[] Synthetic(int width, int quality, int monitor)
        {
            using var bitmap = new Bitmap(800, 600);
            using (var g = Graphics.FromImage(bitmap)) g.Clear(Color.Magenta);
            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Jpeg);
            return ms.ToArray();
        }
        // One lesson owner per device: the three runs are sequential against the real agent server.
        for (int index = 0; index < 3; index++) await CheckClientAsync(index);
        Console.WriteLine($"PASS: 3 enrolled mutual-TLS lessons; 30 {(captureDesktop ? "desktop captures" : "synthetic frames")} decoded and rendered in tiles and live views.");

        async Task CheckClientAsync(int clientIndex)
        {
            string audit = Path.Combine(Path.GetTempPath(), "ClassMonitor.ScreenStreaming", Guid.NewGuid().ToString("N"), "audit.jsonl");
            using var ui = new AgentUi();
            using var server = new AgentServer(ui, config, cert, () => enrollment, new NoRestrictions(), captureDesktop ? null : Synthetic, null, audit, () => null);
            server.Start();
            await Task.Delay(300, timeout.Token);
            using var conn = new AgentConnection("127.0.0.1", $"Test {clientIndex}", pairing, teacher, settings);
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
                byte[] jpeg = await frames.Reader.ReadAsync(timeout.Token);
                tile.SetFrame(jpeg);
                await CheckViewsAsync(tile, live, captureDesktop, timeout.Token);
            }
            live.Close();
            if (conn.FramesReceived < 10 || !live.BuildDiagnostics().Contains("Decoded image:"))
                throw new Exception("Teacher frame diagnostics do not match the received stream");
            Console.WriteLine($"Client {clientIndex + 1}: PASS (10 frames, tile + live view)");
        }
    }

    private static X509Certificate2 Identity(string name)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1"), new("1.3.6.1.5.5.7.3.2") }, false));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), "",
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }

    private sealed class NoRestrictions : ISessionRestrictions
    {
        public void Apply(RestrictCmd command) { }
        public void Clear() { }
        public void Dispose() { }
    }

    internal static async Task CheckViewsAsync(AgentTile tile, LiveViewForm live, bool captureDesktop, CancellationToken ct)
    {
        // FrameReceived queues rendering to the UI thread. Receipt of a network frame
        // does not mean its BeginInvoke callback has run (especially during handle creation).
        var until = DateTime.UtcNow.AddSeconds(3);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                CheckPicture(tile.Controls.OfType<PictureBox>().Single(), captureDesktop);
                CheckPicture(live.Controls.OfType<PictureBox>().Single(), captureDesktop);
                return;
            }
            catch when (DateTime.UtcNow < until) { await Task.Delay(50, ct); }
        }
    }

    internal static void CheckPicture(PictureBox picture, bool captureDesktop)
    {
        if (picture.Image is null) throw new Exception("No image in the picture control");
        using var rendered = new Bitmap(picture.Width, picture.Height);
        picture.DrawToBitmap(rendered, picture.ClientRectangle);
        if (captureDesktop)
        {
            int colored = 0;
            for (int y = 0; y < rendered.Height; y += 2)
            for (int x = 0; x < rendered.Width; x += 2)
            {
                var pixel = rendered.GetPixel(x, y);
                if (pixel.R > 180 && pixel.G < 80 && pixel.B > 180) colored++;
            }
            if (colored < 20)
            {
                Directory.CreateDirectory("tests/ScreenStreaming/artifacts");
                rendered.Save("tests/ScreenStreaming/artifacts/failed-render.png");
                picture.Image.Save("tests/ScreenStreaming/artifacts/failed-frame.png");
                throw new Exception($"Desktop fixture is absent from rendered picture (magenta samples={colored})");
            }
            return;
        }
        var c = rendered.GetPixel(rendered.Width / 2, rendered.Height / 2);
        if (c.R < 180 || c.G > 80 || c.B < 180)
            throw new Exception($"Rendered picture is missing the colored frame: {c}");
    }
}
