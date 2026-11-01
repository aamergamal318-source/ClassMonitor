using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ClassMonitor.Shared;

int seconds = args.Length == 2 && args[0] == "--seconds" ? int.Parse(args[1]) : 5;
if (seconds is < 2 or > 60) throw new ArgumentOutOfRangeException(nameof(seconds));
const int payloadSize = 32 * 1024;
const int intervalMs = 100;
Directory.CreateDirectory("tests/LoadProbe/artifacts");
await CheckFramingAsync();
using var serverIdentity = CreateIdentity("ClassMonitor loopback console");
var results = new List<Measurement>();
foreach (int count in new[] { 10, 20, 30 })
{
    Console.WriteLine($"Preparing {count} unique simulated endpoint certificates…");
    var identities = Enumerable.Range(0, count).Select(i => CreateIdentity($"ClassMonitor simulation {count}-{i}")).ToArray();
    try { results.Add(await MeasureAsync(identities)); }
    finally { foreach (var identity in identities) identity.Dispose(); }
}
string json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText("tests/LoadProbe/artifacts/latest.json", json);
var report = new StringBuilder();
report.AppendLine("# Measured loopback transport benchmark\n");
report.AppendLine($"Executed UTC: {DateTimeOffset.UtcNow:O}. Runtime: {Environment.Version}; logical processors visible to process: {Environment.ProcessorCount}.\n");
report.AppendLine($"Each row uses unique client certificates, exact SHA-256 pins on both sides, TLS 1.2/1.3, production `Frame` framing, {payloadSize:N0}-byte synthetic payloads, target {1000 / intervalMs} frames/s/client and {seconds} seconds of steady-state measurement. Keys and connections are prepared before the timer starts. No screen capture, visible windows, device installation, school permissions or OS policy changes occur.\n");
report.AppendLine("| Simulated peers | Frames echoed | Duration s | CPU % normalized | Working set before/after MiB | Process-lifetime peak MiB | RTT p50/p95 ms | Application Mbps, both directions |\n|---:|---:|---:|---:|---:|---:|---:|---:|");
foreach (var r in results) report.AppendLine($"| {r.Peers} | {r.Frames} | {r.Seconds:F3} | {r.CpuPercent:F2} | {r.WorkingSetBeforeMiB:F2} / {r.WorkingSetAfterMiB:F2} | {r.ProcessLifetimePeakMiB:F2} | {r.LatencyP50Ms:F3} / {r.LatencyP95Ms:F3} | {r.ApplicationMbps:F3} |");
report.AppendLine("\nCPU is this process's TotalProcessorTime delta divided by elapsed wall time and logical processors, covering both TLS endpoints. Throughput counts payload plus the 5-byte application frame header in both directions; it excludes TCP/TLS overhead. RTT uses same-process monotonic timestamps and includes scheduling/framing/TLS/echo. Peak working set is process-lifetime, not isolated per row. Host contention and loopback results cannot establish physical LAN/Wi-Fi, GPU capture, 30 separate machines, UI painting, authorization or end-to-end classroom performance.\n");
report.AppendLine("Executed preflight checks: oversized/invalid/truncated framing rejected, unknown client certificate rejected, incorrect server pin rejected, and same enrolled test certificate reconnects after a clean disconnect. These checks validate this harness and existing framing; they do not replace production enrollment/permission integration tests.\n");
report.AppendLine("Reproduce: `dotnet run --project tests/LoadProbe -c Release -- --seconds 5`. Raw results: ignored `tests/LoadProbe/artifacts/latest.json`. No performance acceptance threshold is claimed yet; physical-device release gate remains open.");
File.WriteAllText("docs/BENCHMARK_RESULTS.md", report.ToString());
Console.WriteLine(report.ToString());

async Task<Measurement> MeasureAsync(X509Certificate2[] identities)
{
    var pins = identities.Select(Pin).ToHashSet(StringComparer.Ordinal);
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 35));
    var servers = new List<Task>();
    var accept = Task.Run(async () =>
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                lock (servers) servers.Add(ServeAsync(client, pins, lifetime.Token));
            }
            catch (OperationCanceledException) { break; }
        }
    });
    await AssertRejectedAsync(CreateIdentity("ClassMonitor unknown client"), Pin(serverIdentity));
    await AssertRejectedAsync(identities[0], new string('0', 64));
    for (int attempt = 0; attempt < 2; attempt++)
    {
        using var reconnect = await ConnectAsync(identities[0], Pin(serverIdentity), lifetime.Token);
        byte[] bytes = new byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, Stopwatch.GetTimestamp());
        await Frame.WriteAsync(reconnect, MsgType.ScreenFrame, bytes, lifetime.Token);
        if ((await Frame.ReadAsync(reconnect, lifetime.Token))?.Payload.Length != bytes.Length) throw new Exception("Reconnect check failed");
    }
    var connections = new List<SslStream>();
    foreach (var identity in identities) connections.Add(await ConnectAsync(identity, Pin(serverIdentity), lifetime.Token));
    var latencies = new ConcurrentBag<double>();
    long frames = 0;
    using var process = Process.GetCurrentProcess();
    process.Refresh();
    long memoryBefore = process.WorkingSet64;
    TimeSpan cpuBefore = process.TotalProcessorTime;
    long started = Stopwatch.GetTimestamp();
    var loops = connections.Select(async stream =>
    {
        byte[] payload = new byte[payloadSize];
        using var tick = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));
        while (Stopwatch.GetElapsedTime(started).TotalSeconds < seconds)
        {
            BinaryPrimitives.WriteInt64LittleEndian(payload, Stopwatch.GetTimestamp());
            await Frame.WriteAsync(stream, MsgType.ScreenFrame, payload, lifetime.Token);
            var response = await Frame.ReadAsync(stream, lifetime.Token) ?? throw new IOException("Unexpected end of echo stream");
            if (response.Type != MsgType.ScreenFrame || response.Payload.Length != payloadSize) throw new InvalidDataException("Invalid echo");
            long sent = BinaryPrimitives.ReadInt64LittleEndian(response.Payload);
            latencies.Add(Stopwatch.GetElapsedTime(sent).TotalMilliseconds);
            Interlocked.Increment(ref frames);
            await tick.WaitForNextTickAsync(lifetime.Token);
        }
    }).ToArray();
    await Task.WhenAll(loops);
    double elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
    process.Refresh();
    var samples = latencies.Order().ToArray();
    var result = new Measurement(identities.Length, frames, elapsed,
        (process.TotalProcessorTime - cpuBefore).TotalSeconds / elapsed / Environment.ProcessorCount * 100,
        memoryBefore / 1048576d, process.WorkingSet64 / 1048576d, process.PeakWorkingSet64 / 1048576d,
        Percentile(samples, .5), Percentile(samples, .95), frames * 2d * (payloadSize + 5) * 8 / elapsed / 1_000_000);
    foreach (var connection in connections) connection.Dispose();
    lifetime.Cancel();
    await accept;
    Task[] pending;
    lock (servers) pending = servers.ToArray();
    await Task.WhenAll(pending);
    Console.WriteLine($"PASS: {result.Peers} peers, {result.Frames} echoed frames, p95={result.LatencyP95Ms:F3}ms, CPU={result.CpuPercent:F2}%");
    return result;

    async Task<SslStream> ConnectAsync(X509Certificate2 identity, string expectedServerPin, CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, ct);
            var ssl = new SslStream(client.GetStream(), false, (_, certificate, _, _) => IsPinned(certificate, expectedServerPin));
            try
            {
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "ClassMonitor loopback console", ClientCertificates = new X509CertificateCollection { identity },
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }, ct);
                return ssl;
            }
            catch { ssl.Dispose(); throw; }
        }
        catch { client.Dispose(); throw; }
    }

    async Task AssertRejectedAsync(X509Certificate2 identity, string expectedServerPin)
    {
        bool rejected = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            using var stream = await ConnectAsync(identity, expectedServerPin, timeout.Token);
            await Frame.WriteAsync(stream, MsgType.ScreenFrame, new byte[16], timeout.Token);
            rejected = await Frame.ReadAsync(stream, timeout.Token) is null;
        }
        catch (Exception e) when (e is AuthenticationException or IOException) { rejected = true; }
        if (!pins.Contains(Pin(identity))) identity.Dispose();
        if (!rejected) throw new Exception("Certificate rejection check failed");
    }
}

async Task ServeAsync(TcpClient client, HashSet<string> allowedPins, CancellationToken ct)
{
    using (client)
    using (var ssl = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
        certificate is not null && allowedPins.Contains(certificate.GetCertHashString(HashAlgorithmName.SHA256)) && IsCurrent(certificate)))
    {
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = serverIdentity, ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }, ct);
            while (!ct.IsCancellationRequested)
            {
                var frame = await Frame.ReadAsync(ssl, ct);
                if (frame is null) break;
                if (frame.Value.Type != MsgType.ScreenFrame || frame.Value.Payload.Length is < 16 or > payloadSize) throw new InvalidDataException();
                await Frame.WriteAsync(ssl, MsgType.ScreenFrame, frame.Value.Payload, ct);
            }
        }
        catch (Exception e) when (e is AuthenticationException or IOException or OperationCanceledException) { }
    }
}

static X509Certificate2 CreateIdentity(string subject)
{
    using var rsa = RSA.Create(2048);
    var request = new CertificateRequest("CN=" + subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
    {
        new("1.3.6.1.5.5.7.3.1"), new("1.3.6.1.5.5.7.3.2")
    }, false));
    using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(30));
    return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), "", X509KeyStorageFlags.UserKeySet);
}
static string Pin(X509Certificate2 certificate) => certificate.GetCertHashString(HashAlgorithmName.SHA256);
static bool IsPinned(X509Certificate? certificate, string pin) => certificate is not null &&
    certificate.GetCertHashString(HashAlgorithmName.SHA256).Equals(pin, StringComparison.Ordinal) && IsCurrent(certificate);
static bool IsCurrent(X509Certificate certificate)
{
    using var value = new X509Certificate2(certificate);
    return value.NotBefore.ToUniversalTime() <= DateTime.UtcNow && value.NotAfter.ToUniversalTime() > DateTime.UtcNow;
}
static double Percentile(double[] sorted, double percentile) => sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1)];
static async Task CheckFramingAsync()
{
    foreach (byte[] bytes in new[] { new byte[] { 0, 0, 0, 0, 2 }, new byte[] { 127, 255, 255, 255, 2 }, new byte[] { 0, 0, 0, 5, 2, 0 } })
    {
        bool rejected = false;
        try { await Frame.ReadAsync(new MemoryStream(bytes)); }
        catch (Exception e) when (e is IOException or InvalidDataException) { rejected = true; }
        if (!rejected) throw new Exception("Invalid/truncated frame was accepted");
    }
    Console.WriteLine("PASS: invalid, oversized and interrupted frame rejection");
}
internal sealed record Measurement(int Peers, long Frames, double Seconds, double CpuPercent,
    double WorkingSetBeforeMiB, double WorkingSetAfterMiB, double ProcessLifetimePeakMiB,
    double LatencyP50Ms, double LatencyP95Ms, double ApplicationMbps);
