using System.Drawing.Imaging;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using ClassMonitor.Shared;
using ClassMonitor.Teacher;

internal static class TransportChecks
{
    public static async Task RunAsync(bool captureDesktop = false)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var rsa = RSA.Create(2048);
        using var generated = new CertificateRequest("CN=ClassMonitor test", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
        using var cert = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), "",
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        byte[] key = Security.DeriveKey("local-test-only");
        await Task.WhenAll(Enumerable.Range(0, 3).Select(CheckClientAsync));
        Console.WriteLine($"PASS: 3 simultaneous TLS clients; 30 {(captureDesktop ? "desktop captures" : "synthetic frames")} decoded and rendered in tiles and live views.");

        async Task CheckClientAsync(int clientIndex)
        {
            using var sessionStop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var server = ServeAsync(sessionStop.Token);
            using var conn = new AgentConnection("127.0.0.1", $"Test {clientIndex}", port, key);
            var frames = Channel.CreateUnbounded<byte[]>();
            conn.FrameReceived += (_, jpeg) => frames.Writer.TryWrite(jpeg);
            using var tile = new AgentTile(conn, new Size(400, 260));
            using var live = new LiveViewForm(conn);
            live.Show();
            Program.ShowFixture();
            await conn.ConnectAsync().WaitAsync(timeout.Token);
            for (int i = 0; i < 10; i++)
            {
                byte[] jpeg = await frames.Reader.ReadAsync(timeout.Token);
                tile.SetFrame(jpeg);
                await CheckViewsAsync(tile, live, captureDesktop, timeout.Token);
            }
            live.Close();
            if (conn.FramesReceived < 10 || !live.BuildDiagnostics().Contains("Decoded image:"))
                throw new Exception("Teacher frame diagnostics do not match the received stream");
            if (captureDesktop) sessionStop.Cancel();
            await server;
            Console.WriteLine($"Client {clientIndex + 1}: PASS (10 frames, tile + live view)");
        }

        async Task ServeAsync(CancellationToken sessionToken)
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var ssl = new SslStream(client.GetStream());
            await ssl.AuthenticateAsServerAsync(cert);
            string nonce = Security.NewNonce();
            await Frame.WriteJsonAsync(ssl, MsgType.AuthHello, new AuthHello(nonce), timeout.Token);
            var request = await Frame.ReadAsync(ssl, timeout.Token);
            var auth = Frame.ReadJson<AuthRequest>(request!.Value.Payload);
            if (!Security.ProofsEqual(auth.ProofT, Security.Proof(key, "teacher", nonce, auth.NonceT)))
                throw new Exception("Authentication failed");
            await Frame.WriteJsonAsync(ssl, MsgType.AuthResult,
                new AuthResult(true, Security.Proof(key, "agent", nonce, auth.NonceT)), timeout.Token);
            if (captureDesktop)
            {
                // Run the production streaming session after the authenticated TLS handshake.
                // No restriction commands are sent; omit Restrictions to avoid changing host/firewall settings.
                using var ui = new ClassMonitor.Agent.AgentUi(false);
                var sessionType = typeof(ClassMonitor.Agent.AgentServer).GetNestedType("Session", BindingFlags.NonPublic)!;
                var session = Activator.CreateInstance(sessionType, client, ssl, ui, null)!;
                await Task.Run(() => (Task)sessionType.GetMethod("RunAsync")!.Invoke(session, new object[] { sessionToken })!);
                return;
            }
            await Frame.WriteJsonAsync(ssl, MsgType.Hello, new HelloInfo("Test", "Test"), timeout.Token);
            await Frame.WriteJsonAsync(ssl, MsgType.MonitorsInfo,
                new MonitorsMsg(new[] { new MonitorInfo(0, 0, 0, 800, 600, true) }), timeout.Token);
            while ((await Frame.ReadAsync(ssl, timeout.Token))?.Type != MsgType.ScreenRequest) { }
            using var bitmap = new Bitmap(800, 600);
            using (var g = Graphics.FromImage(bitmap)) g.Clear(Color.Magenta);
            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Jpeg);
            for (int i = 0; i < 10; i++)
            {
                await Frame.WriteAsync(ssl, MsgType.ScreenFrame, ms.ToArray(), timeout.Token);
                await Task.Delay(80, timeout.Token);
            }
        }
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
