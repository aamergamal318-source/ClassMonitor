using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ClassMonitor.Shared.Management;

string artifacts = Path.GetFullPath(Path.Combine("tests", "SecurityChecks", "artifacts", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(artifacts);
int passed = 0;
using var student = Certificate("Simulated student");
using var teacher = Certificate("Simulated teacher");
using var rogue = Certificate("Unknown teacher");
using var expired = Certificate("Expired", DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(-1));
using var wrongPurpose = Certificate("Wrong purpose", mutualTls: false);
var request = EnrollmentManager.CreateRequest(student, "Simulated device");
var package = EnrollmentManager.CreatePackage(request, teacher, "school-1", "class-1", "Teacher",
    TeacherRole.Teacher, ClassroomPermission.View | ClassroomPermission.Message | ClassroomPermission.Files,
    DateTimeOffset.UtcNow.AddHours(2));
var enrollment = Validate(package);
try
{
    await Check("enrollment round-trip contains public certificate only", () =>
    {
        var serialized = JsonSerializer.Serialize(package);
        var roundTrip = JsonSerializer.Deserialize<EnrollmentPackage>(serialized)!;
        Assert(Validate(roundTrip).DeviceId == request.DeviceId);
        using var publicTeacher = CertificateTrust.ReadPublicCertificate(roundTrip.SigningCertificateBase64);
        using var publicStudent = CertificateTrust.ReadPublicCertificate(request.PublicCertificateBase64);
        Assert(!publicTeacher.HasPrivateKey && !publicStudent.HasPrivateKey);
        Assert(CertificateTrust.SupportsMutualTls(publicStudent) && CertificateTrust.SupportsMutualTls(publicTeacher));
    });
    await Check("explicit teacher and device pins required", () =>
    {
        Reject(() => EnrollmentManager.ValidatePackage(package, request.CertificateSha256, ""));
        Reject(() => EnrollmentManager.ValidatePackage(package, "", CertificateTrust.Fingerprint(teacher)));
    });
    await Check("unknown teacher pin rejected", () => Reject(() => EnrollmentManager.ValidatePackage(package,
        request.CertificateSha256, CertificateTrust.Fingerprint(rogue))));
    await Check("wrong device pin rejected", () => Reject(() => EnrollmentManager.ValidatePackage(package,
        CertificateTrust.Fingerprint(rogue), CertificateTrust.Fingerprint(teacher))));
    await Check("tampered payload rejected before parsing", () =>
    {
        string text = Encoding.UTF8.GetString(Convert.FromBase64String(package.PayloadBase64)).Replace("class-1", "class-2");
        Reject(() => Validate(package with { PayloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) }));
    });
    await Check("unsupported signature algorithm rejected", () => Reject(() => Validate(package with { SignatureAlgorithm = "none" })));
    await Check("invalid Base64 rejected", () => Reject(() => Validate(package with { PayloadBase64 = "%%%" })));
    await Check("oversized payload rejected", () => Reject(() => Validate(package with { PayloadBase64 = new string('A', 65_537) })));
    await Check("signed duplicate properties rejected", () =>
    {
        var text = Encoding.UTF8.GetString(Convert.FromBase64String(package.PayloadBase64));
        Reject(() => Validate(Sign("{\"schoolId\":\"other-school\"," + text[1..])));
    });
    await Check("signed case-ambiguous properties rejected", () =>
    {
        var text = Encoding.UTF8.GetString(Convert.FromBase64String(package.PayloadBase64));
        Reject(() => Validate(Sign("{\"SchoolId\":\"other-school\"," + text[1..])));
    });
    await Check("signed unknown properties rejected", () =>
    {
        var text = Encoding.UTF8.GetString(Convert.FromBase64String(package.PayloadBase64));
        Reject(() => Validate(Sign("{\"unrecognizedTrust\":true," + text[1..])));
    });
    await Check("device request fingerprint substitution rejected", () => Reject(() => EnrollmentManager.CreatePackage(
        request with { CertificateSha256 = CertificateTrust.Fingerprint(rogue) }, teacher, "school-1", "class-1", "Teacher",
        TeacherRole.Teacher, ClassroomPermission.View, DateTimeOffset.UtcNow.AddMinutes(30))));
    await Check("non-admin role cannot grant admin capability", () => Reject(() => EnrollmentManager.CreatePackage(
        request, teacher, "school-1", "class-1", "Teacher", TeacherRole.Teacher,
        ClassroomPermission.View | ClassroomPermission.Admin, DateTimeOffset.UtcNow.AddMinutes(30))));
    await Check("strong unique pairing material generated", () =>
    {
        var another = Validate(EnrollmentManager.CreatePackage(request, teacher, "school-1", "class-1", "Teacher",
            TeacherRole.Teacher, ClassroomPermission.View, DateTimeOffset.UtcNow.AddMinutes(30)));
        Assert(EnrollmentManager.IsStrongPairingSecret(enrollment.PairingSecret));
        Assert(another.PairingSecret != enrollment.PairingSecret);
        Assert(!EnrollmentManager.IsStrongPairingSecret("classmonitor"));
        Assert(!EnrollmentManager.IsStrongPairingSecret(Convert.ToBase64String(new byte[32])));
    });
    await Check("expired or not-yet-valid certificates rejected", () =>
    {
        Assert(!CertificateTrust.Matches(expired, CertificateTrust.Fingerprint(expired)));
        Assert(!CertificateTrust.Matches(student, request.CertificateSha256, DateTimeOffset.UtcNow.AddYears(10)));
    });
    await Check("certificate without mutual TLS purposes rejected", () =>
    {
        Assert(!CertificateTrust.Matches(wrongPurpose, CertificateTrust.Fingerprint(wrongPurpose)));
        Reject(() => EnrollmentManager.CreateRequest(wrongPurpose, "invalid-purpose"));
    });
    await Check("private PFX cannot be accepted as public enrollment certificate", () =>
        Reject(() => CertificateTrust.ReadPublicCertificate(Convert.ToBase64String(teacher.Export(X509ContentType.Pfx)))));
    await Check("malformed signature rejected", () => Reject(() => Validate(package with { SignatureBase64 = "AQID" })));
    await Check("unpaired certificate cannot create lesson authorization", () => Reject(() =>
        new LessonAuthorization(enrollment, CertificateTrust.Fingerprint(rogue))));
    await Check("permission requires a started authorized lesson", () =>
    {
        var auth = Authorization();
        Reject(() => auth.Demand(ClassroomPermission.View));
        auth.Start(Lesson());
        auth.Demand(ClassroomPermission.View | ClassroomPermission.Message);
        Assert(auth.IsActive);
    });
    await Check("cross-school and cross-classroom requests denied", () =>
    {
        var auth = Authorization();
        Reject(() => auth.Start(Lesson() with { SchoolId = "school-2" }));
        Reject(() => auth.Start(Lesson() with { ClassroomId = "class-2" }));
        Assert(!auth.IsActive);
    });
    await Check("expired, excessive or grant-exceeding lessons denied", () =>
    {
        var auth = Authorization();
        Reject(() => auth.Start(Lesson() with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1) }));
        Reject(() => auth.Start(Lesson() with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(9) }));
        Reject(() => auth.Start(Lesson() with { ExpiresAtUtc = enrollment.ExpiresAtUtc.AddMinutes(1) }));
    });
    await Check("ungranted, zero and unknown permissions denied", () =>
    {
        var auth = Authorization(); auth.Start(Lesson());
        Reject(() => auth.Demand(ClassroomPermission.Assist));
        Reject(() => auth.Demand(ClassroomPermission.Admin));
        Reject(() => auth.Demand(ClassroomPermission.None));
        Reject(() => auth.Demand((ClassroomPermission)512));
    });
    await Check("ending lesson revokes all active capabilities", () =>
    {
        var auth = Authorization(); auth.Start(Lesson()); auth.End();
        Assert(!auth.IsActive); Reject(() => auth.Demand(ClassroomPermission.View));
    });
    await Check("lesson replay denied on same connection and reconnect", () =>
    {
        var first = Authorization(); var lesson = Lesson(); first.Start(lesson); first.End();
        Reject(() => first.Start(lesson));
        var reconnect = Authorization(); Reject(() => reconnect.Start(lesson));
    });
    await Check("active lesson cannot silently be extended", () =>
    {
        var auth = Authorization(); var original = Lesson(); auth.Start(original);
        Reject(() => auth.Start(Lesson()));
        Assert(auth.SessionId == original.SessionId); auth.Demand(ClassroomPermission.View);
    });
    await Check("monotonic deadline survives wall-clock rollback", () =>
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var auth = new LessonAuthorization(enrollment, enrollment.Teacher.CertificateSha256, clock);
        auth.Start(new LessonRequest(Guid.NewGuid().ToString("N"), "school-1", "class-1", clock.GetUtcNow().AddMinutes(1)));
        clock.Advance(TimeSpan.FromMinutes(2)); clock.AdjustWall(TimeSpan.FromHours(-1));
        Assert(!auth.IsActive); Reject(() => auth.Demand(ClassroomPermission.View));
    });
    await Check("grant and enrollment expiry rechecked during lesson", () =>
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var auth = new LessonAuthorization(enrollment, enrollment.Teacher.CertificateSha256, clock);
        auth.Start(new LessonRequest(Guid.NewGuid().ToString("N"), "school-1", "class-1", clock.GetUtcNow().AddMinutes(30)));
        clock.Advance(TimeSpan.FromHours(3)); Assert(!auth.IsActive);
        Reject(() => auth.Demand(ClassroomPermission.Files));
    });
    await Check("public grant snapshots cannot expand authorization", () =>
    {
        var auth = Authorization();
        auth.Grant.ClassroomIds[0] = "class-2";
        Reject(() => auth.Start(Lesson() with { ClassroomId = "class-2" }));
        auth.Start(Lesson()); auth.Demand(ClassroomPermission.View);
    });
    await Check("malformed and expired stored enrollment denied", () =>
    {
        Reject(() => EnrollmentManager.ValidateEnrollment(enrollment with { PairingSecret = "classmonitor" }));
        Reject(() => EnrollmentManager.ValidateEnrollment(enrollment with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) }));
        Reject(() => EnrollmentManager.ValidateEnrollment(enrollment with { ClassroomId = "other" }));
    });
    await Check("rate/concurrent limits and idempotent lease release", () =>
    {
        var limit = new ConnectionLimits(2, 10);
        Assert(limit.TryAcquire("peer-a", out var a)); Assert(limit.TryAcquire("peer-b", out var b));
        Assert(!limit.TryAcquire("peer-c", out _)); a!.Dispose(); a.Dispose();
        Assert(limit.TryAcquire("peer-c", out var c)); Assert(!limit.TryAcquire("peer-d", out _));
        b!.Dispose(); c!.Dispose();
    });
    await Check("per-peer attempts reset only after monotonic window", () =>
    {
        var clock = new TestClock(DateTimeOffset.UtcNow); var limit = new ConnectionLimits(2, 2, clock);
        Assert(limit.TryAcquire("peer", out var a)); a!.Dispose();
        Assert(limit.TryAcquire("peer", out var b)); b!.Dispose();
        Assert(!limit.TryAcquire("peer", out _)); clock.AdjustWall(TimeSpan.FromDays(1));
        Assert(!limit.TryAcquire("peer", out _)); clock.Advance(TimeSpan.FromSeconds(61));
        Assert(limit.TryAcquire("peer", out var c)); c!.Dispose();
    });
    await Check("peer table is bounded and expired entries recover", () =>
    {
        var clock = new TestClock(DateTimeOffset.UtcNow); var limit = new ConnectionLimits(1, 2, clock);
        for (int i = 0; i < 1024; i++) { Assert(limit.TryAcquire("peer-" + i, out var lease)); lease!.Dispose(); }
        Assert(!limit.TryAcquire("new-peer", out _)); clock.Advance(TimeSpan.FromMinutes(2));
        Assert(limit.TryAcquire("new-peer", out var recovered)); recovered!.Dispose();
    });
    await Check("audit excludes clear identifiers and free-form content", () =>
    {
        string path = Path.Combine(artifacts, "content-free.jsonl"); var audit = new AuditWriter(path);
        audit.Record(AuditEvent.OperationDenied, "sensitive-name", "device-name", "school-name", "classroom-name",
            ClassroomPermission.Assist, false, AuditReason.PermissionDenied);
        string line = File.ReadAllText(path);
        Assert(!line.Contains("sensitive-name") && !line.Contains("device-name") && !line.Contains("school-name"));
        using var json = JsonDocument.Parse(line);
        Assert(json.RootElement.GetProperty("Allowed").GetBoolean() == false);
    });
    await Check("audit size and retention bound owned log files", () =>
    {
        string path = Path.Combine(artifacts, "rotation.jsonl"); var audit = new AuditWriter(path, 4096);
        for (int i = 0; i < 100; i++) audit.Record(AuditEvent.OperationAllowed, "teacher", "device", "school", "class", ClassroomPermission.View, true);
        Assert(File.Exists(path + ".1") && new FileInfo(path).Length <= 4096 && new FileInfo(path + ".1").Length <= 4096);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-40)); File.SetLastWriteTimeUtc(path + ".1", DateTime.UtcNow.AddDays(-40));
        audit.Record(AuditEvent.LessonEnded, "teacher", "device", "school", "class", ClassroomPermission.View, true);
        Assert(File.ReadAllLines(path).Length == 1 && !File.Exists(path + ".1"));
    });
    await CheckAsync("audit writers serialize concurrent records for same file", async () =>
    {
        string path = Path.Combine(artifacts, "concurrent.jsonl"); var first = new AuditWriter(path); var second = new AuditWriter(path);
        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => Task.Run(() => (i % 2 == 0 ? first : second)
            .Record(AuditEvent.OperationAllowed, "teacher", "device", "school", "class", ClassroomPermission.Message, true))));
        var lines = File.ReadAllLines(path); Assert(lines.Length == 50);
        foreach (string line in lines) { using var json = JsonDocument.Parse(line); Assert(json.RootElement.GetProperty("Allowed").GetBoolean()); }
    });
    await CheckAsync("real mutual pinned TLS accepts enrolled peers", async () => Assert(await TlsRoundTrip(student, teacher, student, teacher)));
    await CheckAsync("real TLS rejects substituted server", async () => Assert(!await TlsRoundTrip(rogue, teacher, student, teacher)));
    await CheckAsync("real TLS rejects substituted client", async () => Assert(!await TlsRoundTrip(student, rogue, student, teacher)));
    await CheckAsync("real TLS rejects missing client certificate", async () => Assert(!await TlsRoundTrip(student, null, student, teacher)));
    Console.WriteLine($"PASS: {passed} headless enrollment/authorization/audit/TLS checks; no OS trust-store enrollment or visible windows.");
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }

DeviceEnrollment Validate(EnrollmentPackage value) => EnrollmentManager.ValidatePackage(value, request.CertificateSha256, CertificateTrust.Fingerprint(teacher));
LessonAuthorization Authorization() => new(enrollment, enrollment.Teacher.CertificateSha256);
LessonRequest Lesson() => new(Guid.NewGuid().ToString("N"), "school-1", "class-1", DateTimeOffset.UtcNow.AddMinutes(20));
EnrollmentPackage Sign(string json)
{
    byte[] bytes = Encoding.UTF8.GetBytes(json);
    using var rsa = teacher.GetRSAPrivateKey()!;
    return package with { PayloadBase64 = Convert.ToBase64String(bytes),
        SignatureBase64 = Convert.ToBase64String(rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) };
}
void Assert(bool value) { if (!value) throw new Exception("Assertion failed"); }
void Reject(Action action)
{
    try { action(); }
    catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException or AuthenticationException or ArgumentException) { return; }
    throw new Exception("Expected security rejection");
}
Task Check(string name, Action action) => CheckAsync(name, () => { action(); return Task.CompletedTask; });
async Task CheckAsync(string name, Func<Task> action) { await action(); passed++; Console.WriteLine("PASS: " + name); }

static X509Certificate2 Certificate(string name, DateTimeOffset? before = null, DateTimeOffset? after = null, bool mutualTls = true)
{
    using var rsa = RSA.Create(2048);
    var request = new CertificateRequest("CN=" + name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(mutualTls
        ? new OidCollection { new("1.3.6.1.5.5.7.3.1"), new("1.3.6.1.5.5.7.3.2") }
        : new OidCollection { new("1.3.6.1.5.5.7.3.3") }, false));
    using var generated = request.CreateSelfSigned(before ?? DateTimeOffset.UtcNow.AddMinutes(-5), after ?? DateTimeOffset.UtcNow.AddHours(4));
    return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), "", X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
}

static async Task<bool> TlsRoundTrip(X509Certificate2 serverIdentity, X509Certificate2? clientIdentity,
    X509Certificate2 expectedServer, X509Certificate2 expectedClient)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    var server = Serve();
    bool clientAccepted = false;
    try
    {
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
        using var stream = new SslStream(client.GetStream(), false, (_, cert, _, _) => RemoteMatches(cert, CertificateTrust.Fingerprint(expectedServer)));
        await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "ClassMonitor", EnabledSslProtocols = SslProtocols.Tls12,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            ClientCertificates = clientIdentity is null ? new X509CertificateCollection() : new X509CertificateCollection { clientIdentity },
            LocalCertificateSelectionCallback = (_, _, _, _, _) => clientIdentity!
        }, timeout.Token);
        var received = new byte[1];
        clientAccepted = await stream.ReadAsync(received, timeout.Token) == 1 && received[0] == 0x42;
    }
    catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException) { }
    return await server && clientAccepted;

    async Task<bool> Serve()
    {
        try
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = new SslStream(client.GetStream(), false, (_, cert, _, _) => RemoteMatches(cert, CertificateTrust.Fingerprint(expectedClient)));
            await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = serverIdentity, ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls12, CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, timeout.Token);
            await stream.WriteAsync(new byte[] { 0x42 }, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException) { return false; }
    }
    static bool RemoteMatches(X509Certificate? certificate, string pin)
    {
        if (certificate is null) return false;
        using var copy = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        return CertificateTrust.Matches(copy, pin) && CertificateTrust.SupportsMutualTls(copy);
    }
}

internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;
    private long _timestamp;
    public override DateTimeOffset GetUtcNow() => _now;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _timestamp;
    public void Advance(TimeSpan amount) { _now += amount; _timestamp += amount.Ticks; }
    public void AdjustWall(TimeSpan amount) => _now += amount;
}
