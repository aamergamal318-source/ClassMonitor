using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ClassMonitor.Agent;
using ClassMonitor.Shared;
using ClassMonitor.Shared.Management;
using ClassMonitor.Shared.Sessions;
using ClassMonitor.Teacher;
using ClassMonitor.Teacher.Management;

// The real AgentServer and AgentConnection over loopback mutual TLS. Capture, input, power, launches and
// restrictions are recorded fakes, so no window, desktop or system setting changes during these checks.
string artifacts = Path.Combine(Path.GetTempPath(), "ClassMonitor.IntegrationChecks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(artifacts);
var failures = new List<string>();
int passed = 0;
using var student = Certificate("Simulated student");
using var teacher = Certificate("Simulated teacher");
using var rogue = Certificate("Rogue endpoint");
const ClassroomPermission Standard = ClassroomPermission.View | ClassroomPermission.Message | ClassroomPermission.Focus | ClassroomPermission.Assist | ClassroomPermission.Present;
Fixtures.StudentIdentity = student;
Fixtures.TeacherIdentity = teacher;

// Optional: the real agent program, on this computer, added with a password. Frames stay in memory and are only decoded.
if (args.Length == 2 && args[0] == "--live-agent")
{
    await Check("real agent is added with its password, then streams a decodable screen during a lesson", async () =>
    {
        foreach (string file in new[] { LocalEnrollmentStore.PasswordPath, LocalEnrollmentStore.EnrollmentPath })
            if (File.Exists(file)) throw new Exception("Refusing to run: this account already has " + file);
        static string[] Existing()
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            return store.Certificates.Where(c => c.Subject.StartsWith("CN=ClassMonitor ")).Select(c => c.Thumbprint).ToArray();
        }
        var before = Existing();
        string password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        LocalEnrollmentStore.SavePassword(PasswordPairing.Create(password));
        using var agent = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(args[1]) { UseShellExecute = false })!;
        var teacherIdentity = WindowsIdentityStore.GetOrCreate("Teacher");
        try
        {
            var settings = new ClassMonitor.Shared.Settings.SchoolSettings();
            PairedDevice? device = null;
            var until = DateTime.UtcNow.AddSeconds(30);
            while (device is null)
            {
                try { device = await AddComputer.RunAsync("127.0.0.1", password, "live-test", teacherIdentity, settings); }
                catch (SocketException) when (DateTime.UtcNow < until) { await Task.Delay(500); }
            }
            Console.WriteLine("  added: " + device.Enrollment.DeviceId[..20]);
            using var conn = new AgentConnection("127.0.0.1", "live", device, teacherIdentity, settings);
            until = DateTime.UtcNow.AddSeconds(15);
            while (true)
            {
                try { await conn.ConnectAsync(); break; }
                catch (SocketException) when (DateTime.UtcNow < until) { await Task.Delay(500); }
            }
            Assert(await conn.StartLessonAsync(DateTimeOffset.UtcNow.AddMinutes(2)));
            await conn.RequestScreenAsync(960, 50, 300);
            await Eventually(() => conn.FramesReceived >= 3, seconds: 20);
            using (var stream = new MemoryStream(conn.LastFrame!))
            using (var image = System.Drawing.Image.FromStream(stream))
                Console.WriteLine($"  frames: {conn.FramesReceived}; decoded {image.Width}x{image.Height}; machine reported: {conn.MachineName.Length > 0}");
            await conn.EndLessonAsync();
            await Task.Delay(500);
            int frames = (int)conn.FramesReceived;
            await Task.Delay(1000);
            Assert(conn.FramesReceived == frames);
            Console.WriteLine("  frames stopped after the lesson ended");
        }
        finally
        {
            if (!agent.HasExited) { agent.Kill(); agent.WaitForExit(5000); }
            foreach (string file in new[] { LocalEnrollmentStore.PasswordPath, LocalEnrollmentStore.EnrollmentPath, LocalEnrollmentStore.Root + "\\audit.jsonl" })
                if (File.Exists(file)) File.Delete(file);
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            foreach (var certificate in store.Certificates.Where(c => c.Subject.StartsWith("CN=ClassMonitor ") && !before.Contains(c.Thumbprint)).ToArray())
            {
                using (var key = certificate.GetRSAPrivateKey() as RSACng) key?.Key.Delete();
                store.Remove(certificate);
            }
            teacherIdentity.Dispose();
        }
    });
    Console.WriteLine(failures.Count == 0 ? "PASS: live agent." : "FAIL: live agent.");
    return failures.Count == 0 ? 0 : 1;
}

// Optional: real non-exportable Windows keys in this account's store. Identities this run creates are removed afterwards.
if (args.Contains("--windows-identities"))
{
    await Check("real Windows identities complete mutual TLS and a lesson; private keys are not exportable", async () =>
    {
        static string[] Existing()
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            return store.Certificates.Where(c => c.Subject.StartsWith("CN=ClassMonitor ")).Select(c => c.Thumbprint).ToArray();
        }
        var before = Existing();
        using var teacherIdentity = WindowsIdentityStore.GetOrCreate("Teacher");
        using var agentIdentity = WindowsIdentityStore.GetOrCreate("Agent");
        try
        {
            using (var key = teacherIdentity.GetRSAPrivateKey() as RSACng ?? throw new Exception("CNG key expected"))
                Assert(key.Key.ExportPolicy == CngExportPolicies.None);
            using (var key = agentIdentity.GetRSAPrivateKey() as RSACng ?? throw new Exception("CNG key expected"))
                Assert(key.Key.ExportPolicy == CngExportPolicies.None);
            Fixtures.StudentIdentity = agentIdentity;
            Fixtures.TeacherIdentity = teacherIdentity;
            await using var host = new Host(Standard);
            using var conn = await host.ConnectedAsync();
            Assert(await conn.StartLessonAsync(DateTimeOffset.UtcNow.AddMinutes(5)));
            await conn.RequestScreenAsync(640, 50, 100);
            await Eventually(() => conn.FramesReceived >= 2);
        }
        finally
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            foreach (var certificate in store.Certificates.Where(c => c.Subject.StartsWith("CN=ClassMonitor ") && !before.Contains(c.Thumbprint)).ToArray())
            {
                using (var key = certificate.GetRSAPrivateKey() as RSACng) key?.Key.Delete();
                store.Remove(certificate);
            }
            Fixtures.StudentIdentity = student;
            Fixtures.TeacherIdentity = teacher;
        }
    });
    Console.WriteLine(failures.Count == 0 ? "PASS: real Windows identities." : "FAIL: real Windows identities.");
    return failures.Count == 0 ? 0 : 1;
}

await Check("port stays closed until a valid enrollment exists", async () =>
{
    await using var host = new Host(Standard, enrolled: false);
    await Task.Delay(300);
    Assert(!await host.PortOpenAsync());
});
await Check("an imported enrollment opens the port without a restart", async () =>
{
    await using var host = new Host(Standard, enrolled: false);
    host.Policy = host.Enrollment;
    await EventuallyAsync(host.PortOpenAsync, 5);
    Assert(await host.PortOpenAsync());
});
await Check("device identity that does not match the enrollment keeps the port closed", async () =>
{
    await using var host = new Host(Standard, identity: rogue);
    await Task.Delay(500);
    Assert(!await host.PortOpenAsync());
});
await Check("rogue teacher certificate is rejected during TLS", async () =>
{
    await using var host = new Host(Standard);
    await EventuallyAsync(host.PortOpenAsync, 5);
    using var conn = host.Connection(identity: rogue);
    await ExpectFailure(conn.ConnectAsync);
    Assert(host.Capture.Calls == 0);
});
await Check("teacher rejects a device that does not match its pin", async () =>
{
    await using var host = new Host(Standard);
    await EventuallyAsync(host.PortOpenAsync, 5);
    var wrong = host.Device with { Enrollment = host.Enrollment with { DeviceCertificateSha256 = CertificateTrust.Fingerprint(rogue) } };
    using var conn = new AgentConnection("127.0.0.1", "wrong pin", wrong, teacher, host.Config.Settings);
    await ExpectFailure(conn.ConnectAsync);
});
await Check("wrong pairing secret is rejected after mutual TLS", async () =>
{
    await using var host = new Host(Standard);
    await EventuallyAsync(host.PortOpenAsync, 5);
    var wrong = host.Device with { Enrollment = host.Enrollment with { PairingSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) } };
    using var conn = new AgentConnection("127.0.0.1", "wrong secret", wrong, teacher, host.Config.Settings);
    await ExpectFailure(conn.ConnectAsync);
});
await Check("agent itself refuses a client with a wrong pairing proof", async () =>
{
    await using var host = new Host(Standard);
    await EventuallyAsync(host.PortOpenAsync, 5);
    using var client = new TcpClient { NoDelay = true };
    await client.ConnectAsync(IPAddress.Loopback, host.Config.Port);
    await using var tls = await SecureChannel.ConnectAsync(client, teacher, host.Enrollment.DeviceCertificateSha256, TimeSpan.FromSeconds(5), CancellationToken.None);
    var challenge = Frame.ReadJson<AuthHello>((await Frame.ReadAsync(tls))!.Value.Payload);
    string nonce = Security.NewNonce();
    byte[] wrongKey = Security.DeriveKey(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    await Frame.WriteJsonAsync(tls, MsgType.AuthRequest, new AuthRequest(nonce, Security.Proof(wrongKey, "teacher", challenge.NonceS, nonce)));
    try { Assert(await Frame.ReadAsync(tls) is null); }
    catch (IOException) { /* The agent closed the session without answering, which is the expected refusal. */ }
    Assert(!host.Ui.Active && host.Capture.Calls == 0);
});
await Check("the pairing port stays closed until a password is set", async () =>
{
    await using var host = new Host(Standard, enrolled: false);
    await Task.Delay(300);
    Assert(!await host.PairingPortOpenAsync());
    host.Password = PasswordPairing.Create("correct horse");
    await EventuallyAsync(host.PairingPortOpenAsync, 5);
});
await Check("adding a computer with the right password connects and works in a lesson", async () =>
{
    await using var host = new Host(Standard, enrolled: false);
    host.Password = PasswordPairing.Create("correct horse");
    await EventuallyAsync(host.PairingPortOpenAsync, 5);
    Assert(!await host.PortOpenAsync());
    var device = await AddComputer.RunAsync("127.0.0.1", "correct horse", "class-7", teacher, host.Config.Settings, host.PairingPort);
    Assert(host.Saved is not null && host.Ui.Messages == 1);
    Assert(device.Port == host.Config.Port && device.Enrollment.ClassroomId == "class-7" && device.Enrollment.Teacher.Role == TeacherRole.Teacher);
    Assert(device.Enrollment.DeviceCertificateSha256 == CertificateTrust.Fingerprint(student));
    await EventuallyAsync(host.PortOpenAsync, 5);
    using var conn = new AgentConnection("127.0.0.1", "added", device, teacher, host.Config.Settings);
    await conn.ConnectAsync();
    Assert(!conn.HasPermission(ClassroomPermission.View));
    Assert(await conn.StartLessonAsync(DateTimeOffset.UtcNow.AddMinutes(5)));
    await conn.RequestScreenAsync(640, 50, 100);
    await Eventually(() => conn.FramesReceived >= 2);
    Assert(!conn.HasPermission(ClassroomPermission.Admin));
});
await Check("a wrong password adds nothing, and repeated guesses lock pairing", async () =>
{
    await using var host = new Host(Standard, enrolled: false);
    host.Password = PasswordPairing.Create("correct horse");
    await EventuallyAsync(host.PairingPortOpenAsync, 5);
    for (int i = 0; i < 5; i++)
    {
        try { await AddComputer.RunAsync("127.0.0.1", "wrong password", "class-1", teacher, host.Config.Settings, host.PairingPort); throw new Exception("Accepted a wrong password"); }
        catch (PairingRefusedException ex) { Assert(ex.Reason == "wrong-password"); }
    }
    Assert(host.Saved is null);
    try { await AddComputer.RunAsync("127.0.0.1", "correct horse", "class-1", teacher, host.Config.Settings, host.PairingPort); throw new Exception("Lockout did not hold"); }
    catch (PairingRefusedException ex) { Assert(ex.Reason == "locked"); }
    Assert(host.Saved is null && !await host.PortOpenAsync());
});
await Check("a computer that does not know the password cannot pass as the real one", async () =>
{
    // A rogue listener offers the teacher its own hash of another password.
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var rogueRequest = EnrollmentManager.CreateRequest(rogue, "Rogue");
    var fake = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var tls = await SecureChannel.AcceptForPairingAsync(client, rogue, TimeSpan.FromSeconds(5), CancellationToken.None);
        try { await PasswordPairing.AgentAsync(tls, PasswordPairing.Create("another password"), rogueRequest, SecureChannel.RemotePin(tls), 5900, CancellationToken.None); }
        catch (AuthenticationException) { }
    });
    var settings = new ClassMonitor.Shared.Settings.SchoolSettings();
    try { await AddComputer.RunAsync("127.0.0.1", "correct horse", "class-1", teacher, settings, port); throw new Exception("Trusted a rogue computer"); }
    catch (PairingRefusedException) { }
    await fake.WaitAsync(TimeSpan.FromSeconds(5));
});
await Check("the teacher refuses a computer that cannot prove the password, and sends it nothing", async () =>
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    bool sentEnrollment = false;
    var rogueRequest = EnrollmentManager.CreateRequest(rogue, "Rogue");
    // It accepts the teacher's proof without checking and answers with a made-up one.
    var fake = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var tls = await SecureChannel.AcceptForPairingAsync(client, rogue, TimeSpan.FromSeconds(5), CancellationToken.None);
        await Frame.ReadAsync(tls);
        await Frame.WriteJsonAsync(tls, MsgType.PairChallenge, new PairChallenge(Convert.ToBase64String(new byte[16]), 100_000, Security.NewNonce()));
        await Frame.ReadAsync(tls);
        await Frame.WriteJsonAsync(tls, MsgType.PairAccepted, new PairAccepted(Convert.ToBase64String(new byte[32]), rogueRequest, 5900));
        try { sentEnrollment = (await Frame.ReadAsync(tls))?.Type == MsgType.PairEnrollment; }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
    });
    try { await AddComputer.RunAsync("127.0.0.1", "correct horse", "class-1", teacher, new ClassMonitor.Shared.Settings.SchoolSettings(), port); throw new Exception("Trusted a computer without proof"); }
    catch (AuthenticationException) { }
    await fake.WaitAsync(TimeSpan.FromSeconds(5));
    Assert(!sentEnrollment);
});
await Check("the proofs are bound to both certificates, so a machine in the middle fails", () =>
{
    byte[] key = RandomNumberGenerator.GetBytes(32);
    string pinA = CertificateTrust.Fingerprint(student), pinT = CertificateTrust.Fingerprint(teacher), pinX = CertificateTrust.Fingerprint(rogue);
    string real = PasswordPairing.Proof(key, "teacher", "n1", "n2", pinA, pinT);
    Assert(real == PasswordPairing.Proof(key, "teacher", "n1", "n2", pinA.ToLowerInvariant(), pinT));
    Assert(real != PasswordPairing.Proof(key, "teacher", "n1", "n2", pinX, pinT));
    Assert(real != PasswordPairing.Proof(key, "teacher", "n1", "n2", pinA, pinX));
    Assert(real != PasswordPairing.Proof(key, "agent", "n1", "n2", pinA, pinT));
    Assert(real != PasswordPairing.Proof(key, "teacher", "n3", "n2", pinA, pinT));
    return Task.CompletedTask;
});
await Check("a password cannot grant the school administrator role", async () =>
{
    await using var host = new Host(Standard, enrolled: false);
    host.Password = PasswordPairing.Create("correct horse");
    await EventuallyAsync(host.PairingPortOpenAsync, 5);
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, host.PairingPort);
    await using var tls = await SecureChannel.ConnectForPairingAsync(client, teacher, TimeSpan.FromSeconds(5), CancellationToken.None);
    try
    {
        await PasswordPairing.TeacherAsync<int>(tls, "correct horse", CertificateTrust.Fingerprint(teacher), SecureChannel.RemotePin(tls), (request, port) =>
        {
            var (document, _) = EnrollmentWorkflow.Create(request, teacher, new EnrollmentChoice("127.0.0.1", port, "school-1", "class-1", "Teacher", TeacherRole.SchoolAdmin, ClassroomPermission.Admin, DateTimeOffset.UtcNow.AddDays(1)));
            return (document, 0);
        }, CancellationToken.None);
        throw new Exception("Accepted the administrator role");
    }
    catch (PairingRefusedException ex) { Assert(ex.Reason == "role-refused"); }
    Assert(host.Saved is null);
});
await Check("the stored password record does not contain the password and short ones are refused", () =>
{
    var record = PasswordPairing.Create("correct horse");
    string json = System.Text.Json.JsonSerializer.Serialize(record);
    Assert(!json.Contains("correct") && !json.Contains("horse") && record.Iterations >= 600_000);
    Assert(PasswordPairing.Create("correct horse").VerifierBase64 != record.VerifierBase64);
    Reject(() => PasswordPairing.Create("short"));
    Reject(() => PasswordPairing.Create(""));
    return Task.CompletedTask;
});
await Check("enrolled connection captures nothing until a lesson starts", async () =>
{
    await using var host = new Host(Standard);
    using var conn = await host.ConnectedAsync();
    Assert(!conn.LessonActive && !conn.HasPermission(ClassroomPermission.View));
    await conn.RequestScreenAsync(640, 50, 100);
    await Task.Delay(400);
    Assert(host.Capture.Calls == 0 && conn.FramesReceived == 0);
});
await Check("agent refuses every command before a lesson, even from a modified client", async () =>
{
    await using var host = new Host(Standard);
    await EventuallyAsync(host.PortOpenAsync, 5);
    await using var raw = await host.RawClientAsync();
    await raw.CommandAsync(MsgType.ScreenRequest, new ScreenRequest(640, 50, 100));
    await raw.CommandAsync(MsgType.ShowMessage, new MessageCmd("before lesson", 5000));
    await raw.CommandAsync(MsgType.Lock, new LockCmd(true, "before lesson"));
    await raw.CommandAsync(MsgType.RemoteInput, new RemoteInputCmd("mousemove", 0.5, 0.5));
    await raw.CommandAsync(MsgType.Restrict, new RestrictCmd("apps", true, new[] { "game" }));
    var results = await raw.ReadResultsAsync(5);
    Assert(results.All(r => !r.Ok));
    Assert(host.Capture.Calls == 0 && host.Ui.Messages == 0 && !host.Ui.Locked && host.Operations.Inputs == 0 && host.Restrictions.Applied == 0);
});
await Check("lesson starts the stream and shows the student who started it", async () =>
{
    await using var host = new Host(Standard);
    using var conn = await host.ConnectedAsync();
    Assert(await conn.StartLessonAsync(DateTimeOffset.UtcNow.AddMinutes(5)));
    Assert(conn.HasPermission(ClassroomPermission.View) && host.Ui.Active && host.Ui.Teacher == "Teacher" && host.Ui.Classroom == "class-1");
    await conn.RequestScreenAsync(640, 50, 100);
    await Eventually(() => conn.FramesReceived >= 3);
    Assert(host.Capture.Calls >= 3);
});
await Check("lesson for another classroom or past expiry is refused", async () =>
{
    await using var host = new Host(Standard);
    await EventuallyAsync(host.PortOpenAsync, 5);
    await using var raw = await host.RawClientAsync();
    await raw.SendJsonAsync(MsgType.LessonStart, new LessonRequest(Guid.NewGuid().ToString("N"), "school-1", "class-2", DateTimeOffset.UtcNow.AddMinutes(5)));
    await raw.SendJsonAsync(MsgType.LessonStart, new LessonRequest(Guid.NewGuid().ToString("N"), "school-2", "class-1", DateTimeOffset.UtcNow.AddMinutes(5)));
    await raw.SendJsonAsync(MsgType.LessonStart, new LessonRequest(Guid.NewGuid().ToString("N"), "school-1", "class-1", DateTimeOffset.UtcNow.AddMinutes(-1)));
    Assert((await raw.ReadResultsAsync(3)).All(r => !r.Ok) && !host.Ui.Active);
});
await Check("a lesson identifier cannot be replayed on a reconnect", async () =>
{
    await using var host = new Host(Standard);
    await EventuallyAsync(host.PortOpenAsync, 5);
    var lesson = new LessonRequest(Guid.NewGuid().ToString("N"), "school-1", "class-1", DateTimeOffset.UtcNow.AddMinutes(5));
    await using (var first = await host.RawClientAsync())
    {
        await first.SendJsonAsync(MsgType.LessonStart, lesson);
        Assert((await first.ReadResultsAsync(1))[0].Ok);
    }
    await Eventually(() => !host.Ui.Active);
    await using var second = await host.RawClientAsync();
    await second.SendJsonAsync(MsgType.LessonStart, lesson);
    Assert(!(await second.ReadResultsAsync(1))[0].Ok && !host.Ui.Active);
});
await Check("ungranted capabilities are refused during a lesson", async () =>
{
    await using var host = new Host(ClassroomPermission.View);
    await EventuallyAsync(host.PortOpenAsync, 5);
    await using var raw = await host.RawClientAsync();
    await raw.StartLessonAsync();
    await raw.CommandAsync(MsgType.ShowMessage, new MessageCmd("not granted", 5000));
    await raw.CommandAsync(MsgType.Lock, new LockCmd(true, "not granted"));
    await raw.CommandAsync(MsgType.AssistanceState, new StateRequest("x", true));
    await raw.CommandAsync(MsgType.Power, new PowerCmd("shutdown"));
    await raw.CommandAsync(MsgType.Run, new RunCmd("program", "notepad"));
    await raw.CommandAsync(MsgType.Run, new RunCmd("website", "https://example.com"));
    await raw.CommandAsync(MsgType.FileSend, new FilePush("a.txt", "QQ=="));
    await raw.CommandAsync(MsgType.RecordingState, new StateRequest("x", true));
    Assert((await raw.ReadResultsAsync(8)).All(r => !r.Ok));
    Assert(host.Ui.Messages == 0 && !host.Ui.Locked && host.Operations.Powers == 0 && host.Operations.Runs == 0 && !host.Ui.Recording);
});
await Check("Teacher role cannot use power or program launch; website needs Message", async () =>
{
    await using var host = new Host(Standard);
    using var conn = await host.ConnectedAsync();
    string feedback = "";
    conn.CommandFeedback += (_, text) => feedback = text;
    Assert(await conn.StartLessonAsync(DateTimeOffset.UtcNow.AddMinutes(5)));
    await conn.PowerAsync("shutdown");
    Assert(feedback.Contains("not authorized") && host.Operations.Powers == 0);
    await conn.RunAsync("program", "notepad");
    Assert(host.Operations.Runs == 0);
    await conn.RunAsync("website", "https://example.com");
    await Eventually(() => host.Operations.Runs == 1);
});
await Check("remote input requires visible assistance first", async () =>
{
    await using var host = new Host(Standard);
    using var conn = await host.ConnectedAsync();
    Assert(await conn.StartLessonAsync(DateTimeOffset.UtcNow.AddMinutes(5)));
    await conn.RemoteInputAsync(new RemoteInputCmd("mousemove", 0.5, 0.5));
    await Task.Delay(200);
    Assert(host.Operations.Inputs == 0);
    Assert(await conn.SetAssistanceAsync(true) && host.Ui.Assisting);
    await conn.RemoteInputAsync(new RemoteInputCmd("mousemove", 0.5, 0.5));
    await Eventually(() => host.Operations.Inputs == 1);
    Assert(await conn.SetAssistanceAsync(false) && !host.Ui.Assisting && host.Operations.Releases >= 1);
    await conn.RemoteInputAsync(new RemoteInputCmd("mousemove", 0.5, 0.5));
    await Task.Delay(200);
    Assert(host.Operations.Inputs == 1);
});
await Check("recording requires the enrollment grant and is shown to the student", async () =>
{
    await using (var refused = new Host(Standard))
    {
        using var conn = await refused.ConnectedAsync();
        Assert(await conn.StartLessonAsync(DateTimeOffset.UtcNow.AddMinutes(5)));
        Assert(!await conn.SetRecordingAsync(true) && !conn.Recording && !refused.Ui.Recording);
    }
    await using var host = new Host(Standard | ClassroomPermission.Record);
    using var allowed = await host.ConnectedAsync();
    Assert(await allowed.StartLessonAsync(DateTimeOffset.UtcNow.AddMinutes(5)));
    Assert(await allowed.SetRecordingAsync(true) && allowed.Recording && host.Ui.Recording);
    Assert(await allowed.SetRecordingAsync(false) && !host.Ui.Recording);
});
await Check("ending a lesson stops capture and restores what the session owned", async () =>
{
    await using var host = new Host(Standard);
    using var conn = await host.ConnectedAsync();
    bool disconnected = false;
    conn.Disconnected += _ => disconnected = true;
    Assert(await conn.StartLessonAsync(DateTimeOffset.UtcNow.AddMinutes(5)));
    await conn.LockAsync(true, "focus");
    await conn.RestrictAsync("apps", true, new[] { "game" });
    await conn.RequestScreenAsync(640, 50, 100);
    await Eventually(() => host.Ui.Locked && host.Restrictions.Applied == 1 && host.Capture.Calls > 0);
    await conn.EndLessonAsync();
    await Eventually(() => disconnected && !host.Ui.Active && !host.Ui.Locked && host.Restrictions.Cleared >= 1);
    int captured = host.Capture.Calls;
    await Task.Delay(400);
    Assert(host.Capture.Calls == captured && !conn.HasPermission(ClassroomPermission.View));
});
await Check("a dropped teacher restores the device", async () =>
{
    await using var host = new Host(Standard);
    var conn = await host.ConnectedAsync();
    Assert(await conn.StartLessonAsync(DateTimeOffset.UtcNow.AddMinutes(5)));
    await conn.LockAsync(true, "focus");
    await Eventually(() => host.Ui.Locked);
    conn.Dispose();
    await Eventually(() => !host.Ui.Active && !host.Ui.Locked && host.Restrictions.Cleared >= 1);
});
await Check("lesson expiry ends the session and restores the device", async () =>
{
    await using var host = new Host(Standard);
    using var conn = await host.ConnectedAsync();
    Assert(await conn.StartLessonAsync(DateTimeOffset.UtcNow.AddSeconds(2)));
    await conn.LockAsync(true, "focus");
    await Eventually(() => host.Ui.Locked);
    await Eventually(() => !host.Ui.Active && !host.Ui.Locked && !conn.LessonActive, seconds: 10);
});
await Check("revoking enrollment ends a running session", async () =>
{
    await using var host = new Host(Standard);
    using var conn = await host.ConnectedAsync();
    Assert(await conn.StartLessonAsync(DateTimeOffset.UtcNow.AddMinutes(5)));
    host.Policy = null;
    await Eventually(() => !host.Ui.Active, seconds: 10);
});
await Check("the student can end the session and the teacher is told why", async () =>
{
    await using var host = new Host(Standard);
    using var conn = await host.ConnectedAsync();
    bool disconnected = false;
    conn.Disconnected += _ => disconnected = true;
    Assert(await conn.StartLessonAsync(DateTimeOffset.UtcNow.AddMinutes(5)));
    host.Ui.RequestEnd();
    await Eventually(() => disconnected && !host.Ui.Active && !conn.LessonActive);
    Assert(conn.EndedByStudent);
});
await Check("a teacher ending a lesson is not reported as the student's choice", async () =>
{
    await using var host = new Host(Standard);
    using var conn = await host.ConnectedAsync();
    bool disconnected = false;
    conn.Disconnected += _ => disconnected = true;
    Assert(await conn.StartLessonAsync(DateTimeOffset.UtcNow.AddMinutes(5)));
    await conn.EndLessonAsync();
    await Eventually(() => disconnected);
    Assert(!conn.EndedByStudent);
});
await Check("the student sees when their screen is shown to the class", async () =>
{
    await using var host = new Host(Standard);
    using var conn = await host.ConnectedAsync();
    Assert(!await conn.SetSharedAsync(true) && !host.Ui.Shared);
    Assert(await conn.StartLessonAsync(DateTimeOffset.UtcNow.AddMinutes(5)));
    Assert(await conn.SetSharedAsync(true) && host.Ui.Shared);
    Assert(await conn.SetSharedAsync(false) && !host.Ui.Shared);
});
await Check("a failing command is reported and does not end the lesson", async () =>
{
    await using var host = new Host(Standard);
    using var conn = await host.ConnectedAsync();
    string feedback = "";
    conn.CommandFeedback += (_, text) => feedback = text;
    Assert(await conn.StartLessonAsync(DateTimeOffset.UtcNow.AddMinutes(5)));
    host.Restrictions.FailNext = true;
    await conn.RestrictAsync("internet", true, Array.Empty<string>());
    await Eventually(() => feedback.Contains("could not complete"));
    Assert(host.Ui.Active && conn.LessonActive);
});
await Check("a power command restores system changes before it runs", async () =>
{
    await using var host = new Host(ClassroomPermission.Admin | ClassroomPermission.Focus);
    using var conn = await host.ConnectedAsync();
    Assert(await conn.StartLessonAsync(DateTimeOffset.UtcNow.AddMinutes(5)));
    await conn.RestrictAsync("internet", true, Array.Empty<string>());
    await Eventually(() => host.Restrictions.Applied == 1);
    await conn.PowerAsync("restart");
    await Eventually(() => host.Operations.Powers == 1);
    Assert(host.Restrictions.Cleared >= 1 && host.Operations.ClearedBeforePower);
});
await Check("a modified student agent cannot push files, frames or a bad name to the teacher", async () =>
{
    // The enrolled peer here is a hostile agent: it holds real credentials and sends whatever it likes.
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var request = EnrollmentManager.CreateRequest(student, "Hostile agent");
    var (_, device) = EnrollmentWorkflow.Create(request, teacher, new EnrollmentChoice("127.0.0.1", port, "school-1", "class-1", "Teacher", TeacherRole.Teacher, ClassroomPermission.Files | ClassroomPermission.View, DateTimeOffset.UtcNow.AddHours(1)));
    using var conn = new AgentConnection("127.0.0.1", "original name", device, teacher, new ClassMonitor.Shared.Settings.SchoolSettings());
    int collected = 0, frames = 0;
    conn.FileCollected += (_, _) => collected++;
    conn.FrameReceived += (_, _) => frames++;
    var unsolicited = new FileDataMsg("planted.exe", "QQ==", true, "");
    var agent = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var tls = await SecureChannel.AcceptAsync(client, student, CertificateTrust.Fingerprint(teacher), TimeSpan.FromSeconds(5), CancellationToken.None);
        await SecureChannel.AuthenticateAgentAsync(tls, device.Enrollment.PairingSecret, CancellationToken.None);
        await Frame.WriteJsonAsync(tls, MsgType.Hello, new HelloInfo("bad\nname" + new string('x', 400), "user"));
        await Frame.WriteJsonAsync(tls, MsgType.FileData, unsolicited);
        await Frame.WriteAsync(tls, MsgType.ScreenFrame, new byte[] { 1, 2, 3 });
        while (await Frame.ReadAsync(tls) is { } message)
        {
            if (message.Type == MsgType.LessonStart)
            {
                await Frame.WriteJsonAsync(tls, MsgType.StateResult, new StateResult(Frame.ReadJson<LessonRequest>(message.Payload).SessionId, true, "ok"));
            }
            else if (message.Type == MsgType.ManagedCommand && Frame.ReadJson<ManagedCommand>(message.Payload).Operation == MsgType.ScreenRequest)
            {
                await Frame.WriteJsonAsync(tls, MsgType.FileData, unsolicited);
                await Frame.WriteAsync(tls, MsgType.ScreenFrame, new byte[] { 4, 5, 6 });
            }
            else if (message.Type == MsgType.ManagedCommand && Frame.ReadJson<ManagedCommand>(message.Payload).Operation == MsgType.FileRequest)
            {
                await Frame.WriteJsonAsync(tls, MsgType.FileData, new FileDataMsg("asked.txt", "QQ==", true, ""));
                await Frame.WriteJsonAsync(tls, MsgType.FileData, unsolicited);
            }
        }
    });
    await conn.ConnectAsync();
    await Eventually(() => conn.MachineName != "original name");
    Assert(conn.MachineName.Length <= 128 && !conn.MachineName.Contains('\n'));
    await Task.Delay(150);
    Assert(collected == 0 && frames == 0 && conn.LastFrame is null);
    Assert(await conn.StartLessonAsync(DateTimeOffset.UtcNow.AddMinutes(5)));
    await conn.RequestScreenAsync(640, 50, 200);
    await Eventually(() => frames == 1);
    Assert(collected == 0);
    await conn.RequestFileAsync("asked.txt");
    await Eventually(() => collected == 1);
    await Task.Delay(150);
    Assert(collected == 1);
    conn.Dispose();
    await agent.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { });
});
await Check("only one lesson owner per device", async () =>
{
    await using var host = new Host(Standard);
    using var first = await host.ConnectedAsync();
    Assert(await first.StartLessonAsync(DateTimeOffset.UtcNow.AddMinutes(5)));
    using var second = host.Connection();
    await second.ConnectAsync();
    Assert(!await second.StartLessonAsync(DateTimeOffset.UtcNow.AddMinutes(5)));
    Assert(first.LessonActive && host.Ui.Active);
});
await Check("reconnect after a drop starts a new lesson", async () =>
{
    await using var host = new Host(Standard);
    using (var first = await host.ConnectedAsync())
        Assert(await first.StartLessonAsync(DateTimeOffset.UtcNow.AddMinutes(5)));
    await Eventually(() => !host.Ui.Active);
    using var second = await host.ConnectedAsync();
    Assert(await second.StartLessonAsync(DateTimeOffset.UtcNow.AddMinutes(5)) && host.Ui.Active);
});
await Check("health pings keep an idle connection alive past the timeout", async () =>
{
    await using var host = new Host(Standard, healthSeconds: 5);
    using var conn = await host.ConnectedAsync();
    bool disconnected = false;
    conn.Disconnected += _ => disconnected = true;
    await Task.Delay(7000);
    Assert(!disconnected);
});
await Check("a silent teacher is dropped by the agent watchdog", async () =>
{
    await using var host = new Host(Standard, healthSeconds: 5);
    await EventuallyAsync(host.PortOpenAsync, 5);
    await using var raw = await host.RawClientAsync();
    await raw.StartLessonAsync();
    Assert(host.Ui.Active);
    await Eventually(() => !host.Ui.Active, seconds: 12);
});
await Check("malformed traffic closes that session and the agent keeps serving", async () =>
{
    await using var host = new Host(Standard);
    await EventuallyAsync(host.PortOpenAsync, 5);
    await using (var raw = await host.RawClientAsync())
    {
        await raw.StartLessonAsync();
        await Frame.WriteAsync(raw.Stream, MsgType.ManagedCommand, System.Text.Encoding.UTF8.GetBytes("{not json"));
        await Eventually(() => !host.Ui.Active);
    }
    await using (var raw = await host.RawClientAsync())
    {
        await raw.StartLessonAsync();
        await raw.Stream.WriteAsync(new byte[] { 0x7F, 0xFF, 0xFF, 0xFF, 1 });
        await raw.Stream.FlushAsync();
        await Eventually(() => !host.Ui.Active);
    }
    using var conn = await host.ConnectedAsync();
    Assert(await conn.StartLessonAsync(DateTimeOffset.UtcNow.AddMinutes(5)));
});
await Check("nested lesson control inside a command envelope is refused", async () =>
{
    await using var host = new Host(Standard);
    await EventuallyAsync(host.PortOpenAsync, 5);
    await using var raw = await host.RawClientAsync();
    var nested = new LessonRequest(Guid.NewGuid().ToString("N"), "school-1", "class-1", DateTimeOffset.UtcNow.AddMinutes(5));
    await raw.SendJsonAsync(MsgType.ManagedCommand, new ManagedCommand(Guid.NewGuid().ToString("N"), MsgType.LessonStart, Frame.ToJson(nested)));
    await EventuallyAsync(raw.IsClosedAsync, 5);
    Assert(!host.Ui.Active);
});
await Check("file transfer stays inside approved folders and within the size limit", async () =>
{
    await using var host = new Host(ClassroomPermission.Files);
    await EventuallyAsync(host.PortOpenAsync, 5);
    await using var raw = await host.RawClientAsync();
    await raw.StartLessonAsync();
    await raw.CommandAsync(MsgType.FileSend, new FilePush("..\\escape.txt", "QQ=="));
    await raw.CommandAsync(MsgType.FileSend, new FilePush("lesson.txt", Convert.ToBase64String(new byte[5 * 1024 * 1024])));
    await raw.CommandAsync(MsgType.FileSend, new FilePush("lesson.txt", "QQ=="));
    var results = await raw.ReadResultsAsync(3);
    Assert(!results[0].Ok && !results[1].Ok && results[2].Ok);
    Assert(File.Exists(Path.Combine(host.Config.Settings.Files.ReceiveDirectory, "lesson.txt")));
    Assert(!File.Exists(Path.Combine(Path.GetDirectoryName(host.Config.Settings.Files.ReceiveDirectory)!, "escape.txt")));
    await raw.CommandAsync(MsgType.FileRequest, new FileRequestCmd(Path.Combine(artifacts, "absolute.txt")));
    Assert((await raw.ReadUntilAsync(MsgType.FileData)) is { } data && !Frame.ReadJson<FileDataMsg>(data).Ok);
});
await Check("settings of the running session cap frames at the configured profile", async () =>
{
    await using var host = new Host(Standard);
    await EventuallyAsync(host.PortOpenAsync, 5);
    await using var raw = await host.RawClientAsync();
    await raw.StartLessonAsync();
    await raw.CommandAsync(MsgType.ScreenRequest, new ScreenRequest(5000, 50, 100));
    await raw.CommandAsync(MsgType.ScreenRequest, new ScreenRequest(640, 99, 100));
    await raw.CommandAsync(MsgType.ScreenRequest, new ScreenRequest(640, 50, 5));
    Assert((await raw.ReadResultsAsync(3)).All(r => !r.Ok) && host.Capture.Calls == 0);
});
await Check("audit records operations without clear identifiers or content", async () =>
{
    string marker = "private lesson message";
    await using var host = new Host(Standard);
    using var conn = await host.ConnectedAsync();
    Assert(await conn.StartLessonAsync(DateTimeOffset.UtcNow.AddMinutes(5)));
    await conn.SendMessageAsync(marker, 5000);
    await Eventually(() => host.Ui.Messages == 1);
    string audit = File.ReadAllText(host.AuditPath);
    Assert(audit.Length > 0 && !audit.Contains(marker) && !audit.Contains("school-1") && !audit.Contains("class-1"));
});
await Check("expired enrollment leaves settings readable and asks for re-enrollment", () =>
{
    using var expired = Certificate("Expired student", DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(-1));
    string pin = CertificateTrust.Fingerprint(expired);
    var request = EnrollmentManager.CreateRequest(student, "PC-02");
    var (_, current) = EnrollmentWorkflow.Create(request, teacher, new EnrollmentChoice("192.0.2.20", 5900, "school-1", "class-1", "Teacher", TeacherRole.Teacher, ClassroomPermission.View, DateTimeOffset.UtcNow.AddDays(1)));
    var device = current with
    {
        PublicCertificateBase64 = Convert.ToBase64String(expired.RawData),
        Enrollment = current.Enrollment with { DeviceCertificateSha256 = pin, DeviceId = "device-" + pin.ToLowerInvariant(), ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(-1) }
    };
    string path = Path.Combine(artifacts, "expired-teacher.json");
    var config = new AppConfig();
    config.PairedDevices.Add(device);
    config.Save(path);
    var loaded = AppConfig.Load(path);
    Assert(loaded.PairedDevices.Count == 1 && ClassMonitor.Teacher.Presentation.PairingLookup.IsExpired(loaded.PairedDevices[0]));
    Assert(ClassMonitor.Teacher.Presentation.PairingLookup.Find(loaded.PairedDevices, "192.0.2.20") is null);
    loaded.Save(path);
    return Task.CompletedTask;
});
await Check("two accounts on one computer keep separate records", () =>
{
    var first = EnrollmentManager.CreateRequest(student, "PC / account A");
    var second = EnrollmentManager.CreateRequest(rogue, "PC / account B");
    var choice = new EnrollmentChoice("192.0.2.30", 5900, "school-1", "class-1", "Teacher", TeacherRole.Teacher, Standard, DateTimeOffset.UtcNow.AddDays(30));
    var devices = new List<PairedDevice>();
    EnrollmentWorkflow.Save(devices, EnrollmentWorkflow.Create(first, teacher, choice).Device);
    EnrollmentWorkflow.Save(devices, EnrollmentWorkflow.Create(second, teacher, choice).Device);
    Assert(devices.Count == 2);
    var config = new AppConfig { PairedDevices = devices };
    config.Validate();
    var lookup = ClassMonitor.Teacher.Presentation.PairingLookup.Find(devices, "192.0.2.30", second.DeviceId);
    Assert(lookup?.Enrollment.DeviceId == second.DeviceId);
    Assert(ClassMonitor.Teacher.Presentation.PairingLookup.Find(devices, "192.0.2.30") is null);
    return Task.CompletedTask;
});
await Check("leftover hosts blocks are removed and everything else is kept", () =>
{
    string owner = new string('a', 32);
    string[] hosts = { "127.0.0.1 school.local", $"# ClassMonitor:{owner}:Begin", "0.0.0.0 game.example", $"# ClassMonitor:{owner}:End", "# user comment" };
    Assert(RestrictionCleanup.HostsHasOwnedBlocks(hosts));
    Assert(RestrictionCleanup.WithoutOwnedHostsBlocks(hosts).SequenceEqual(new[] { "127.0.0.1 school.local", "# user comment" }));
    string[] unterminated = { "keep", $"# ClassMonitor:{owner}:Begin", "0.0.0.0 unknown.example" };
    Assert(RestrictionCleanup.WithoutOwnedHostsBlocks(unterminated).SequenceEqual(unterminated));
    Assert(!RestrictionCleanup.HostsHasOwnedBlocks(new[] { "# ClassMonitor:notours:Begin", "keep" }));
    return Task.CompletedTask;
});
await Check("snapshots count towards recording retention and storage; other files do not", () =>
{
    string folder = Path.Combine(artifacts, "recordings"); Directory.CreateDirectory(folder);
    string avi = Path.Combine(folder, "classmonitor-a.avi"), jpg = Path.Combine(folder, "classmonitor-b.jpg"), other = Path.Combine(folder, "classmonitor-notes.txt"), mine = Path.Combine(folder, "lesson.jpg");
    foreach (string file in new[] { avi, jpg, other, mine }) { File.WriteAllBytes(file, new byte[10]); File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-30)); }
    Assert(ClassMonitor.Shared.Settings.RecordingRetention.OwnedStorageBytes(folder) == 20);
    Assert(ClassMonitor.Shared.Settings.RecordingRetention.PruneExpired(folder, 7, DateTimeOffset.UtcNow) == 2);
    Assert(!File.Exists(avi) && !File.Exists(jpg) && File.Exists(other) && File.Exists(mine));
    return Task.CompletedTask;
});
await Check("enrollment workflow signs a reviewed request that the store format accepts", async () =>
{
    var request = EnrollmentManager.CreateRequest(student, "PC-01 / student");
    var choice = new EnrollmentChoice("192.0.2.10", 5900, "school-1", "class-1", "Teacher", TeacherRole.Teacher, Standard, DateTimeOffset.UtcNow.AddDays(30));
    var (document, device) = EnrollmentWorkflow.Create(request, teacher, choice);
    Assert(device.Enrollment.DeviceCertificateSha256 == CertificateTrust.Fingerprint(student));
    Assert(document.TeacherFingerprint == CertificateTrust.Fingerprint(teacher));
    string path = Path.Combine(artifacts, "document.json");
    EnrollmentWorkflow.WriteDocument(path, document);
    var read = System.Text.Json.JsonSerializer.Deserialize<ManagedEnrollment>(File.ReadAllText(path))!;
    Assert(EnrollmentManager.ValidatePackage(read.Package, read.DeviceFingerprint, read.TeacherFingerprint).ClassroomId == "class-1");
    Assert(!File.ReadAllText(path).Contains("PRIVATE KEY"));
    var saved = new List<PairedDevice> { device with { Host = "198.51.100.1" } };
    EnrollmentWorkflow.Save(saved, device); // The same identity on a new address replaces its record.
    Assert(saved.Count == 1 && saved[0].Host == "192.0.2.10");
    Reject(() => EnrollmentWorkflow.Create(request, teacher, choice with { Permissions = ClassroomPermission.Admin }));
    Reject(() => EnrollmentWorkflow.Create(request, teacher, choice with { Host = " " }));
    Reject(() => EnrollmentWorkflow.Create(request, teacher, choice with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddYears(30) }));
    await Task.CompletedTask;
});

try { Directory.Delete(artifacts, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
if (failures.Count != 0)
{
    Console.Error.WriteLine($"FAIL: {failures.Count} of {passed + failures.Count} integration checks failed.");
    foreach (string name in failures) Console.Error.WriteLine("  - " + name);
    return 1;
}
Console.WriteLine($"PASS: {passed} integration checks over loopback mutual TLS; system operations were recorded fakes and no window opened.");
return 0;

async Task Check(string name, Func<Task> action)
{
    try
    {
        await action().WaitAsync(TimeSpan.FromSeconds(60));
        passed++;
        Console.WriteLine("PASS: " + name);
    }
    catch (Exception ex)
    {
        failures.Add(name);
        Console.Error.WriteLine("FAIL: " + name + Environment.NewLine + ex);
    }
}
static void Assert(bool value) { if (!value) throw new Exception("Assertion failed"); }
static void Reject(Action action)
{
    try { action(); }
    catch (Exception ex) when (ex is InvalidDataException or AuthenticationException or ArgumentException) { return; }
    throw new Exception("Expected rejection");
}
static async Task ExpectFailure(Func<Task> action)
{
    try { await action(); }
    catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException or InvalidDataException) { return; }
    throw new Exception("Expected connection failure");
}
static async Task Eventually(Func<bool> condition, int seconds = 5)
{
    var until = DateTime.UtcNow.AddSeconds(seconds);
    while (!condition())
    {
        if (DateTime.UtcNow > until) throw new TimeoutException("Condition not reached");
        await Task.Delay(25);
    }
}
static async Task EventuallyAsync(Func<Task<bool>> condition, int seconds)
{
    var until = DateTime.UtcNow.AddSeconds(seconds);
    while (!await condition())
    {
        if (DateTime.UtcNow > until) throw new TimeoutException("Condition not reached");
        await Task.Delay(50);
    }
}
static X509Certificate2 Certificate(string name, DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null)
{
    using var rsa = RSA.Create(2048);
    var request = new CertificateRequest("CN=" + name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1"), new("1.3.6.1.5.5.7.3.2") }, false));
    using var generated = request.CreateSelfSigned(notBefore ?? DateTimeOffset.UtcNow.AddMinutes(-5), notAfter ?? DateTimeOffset.UtcNow.AddYears(1));
    return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), "", X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
}

sealed class Host : IAsyncDisposable
{
    private readonly AgentServer _server;
    private readonly string _root;
    private readonly X509Certificate2 _teacher;
    private readonly X509Certificate2 _student;
    public FakeUi Ui { get; } = new();
    public FakeRestrictions Restrictions { get; } = new();
    public FakeCapture Capture { get; } = new();
    public FakeOperations Operations { get; } = new();
    public AppConfig Config { get; } = new();
    public DeviceEnrollment Enrollment { get; }
    public PairedDevice Device { get; }
    public string AuditPath { get; }
    public volatile DeviceEnrollment? Policy;
    public volatile PairingPassword? Password;
    public volatile ManagedEnrollment? Saved;
    public int PairingPort { get; }

    public Host(ClassroomPermission permissions, bool enrolled = true, X509Certificate2? identity = null, int healthSeconds = 15)
    {
        var fixtures = Fixtures.Current;
        _student = fixtures.Student; _teacher = fixtures.Teacher;
        _root = Path.Combine(Path.GetTempPath(), "ClassMonitor.IntegrationChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        AuditPath = Path.Combine(_root, "audit.jsonl");
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            Config.Settings.Network.AgentPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        Config.Settings.Network.DiscoveryEnabled = false;
        Config.Settings.Network.HealthTimeoutSeconds = healthSeconds;
        Config.Settings.Network.HandshakeTimeoutSeconds = 5;
        Config.Settings.Files.ReceiveDirectory = Path.Combine(_root, "received");
        Config.Settings.Files.CollectDirectory = Path.Combine(_root, "collected");
        var role = permissions.HasFlag(ClassroomPermission.Admin) ? TeacherRole.SchoolAdmin : TeacherRole.Teacher;
        var request = EnrollmentManager.CreateRequest(_student, "Simulated device");
        var package = EnrollmentManager.CreatePackage(request, _teacher, "school-1", "class-1", "Teacher", role, permissions, DateTimeOffset.UtcNow.AddHours(4));
        Enrollment = EnrollmentManager.ValidatePackage(package, request.CertificateSha256, CertificateTrust.Fingerprint(_teacher));
        Device = new PairedDevice("127.0.0.1", Config.Port, Enrollment, request.PublicCertificateBase64);
        Policy = enrolled ? Enrollment : null;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            PairingPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        _server = new AgentServer(Ui, Config, identity ?? _student, () => Policy, Restrictions, Capture.Jpeg,
            new AgentOperations(Operations.Input, command => { Operations.ClearedBeforePower = Restrictions.Cleared > 0; Operations.Power(command); }, Operations.Run, Operations.Release), AuditPath,
            () => Password, document =>
            {
                Saved = document;
                Policy = EnrollmentManager.ValidatePackage(document.Package, document.DeviceFingerprint, document.TeacherFingerprint);
            }, PairingPort);
        _server.Start();
    }

    public AgentConnection Connection(X509Certificate2? identity = null) =>
        new("127.0.0.1", "Simulated device", Device, identity ?? _teacher, Config.Settings);

    public async Task<AgentConnection> ConnectedAsync()
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!await PortOpenAsync())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Agent did not listen");
            await Task.Delay(50);
        }
        var conn = Connection();
        await conn.ConnectAsync();
        return conn;
    }

    public Task<bool> PortOpenAsync() => OpenAsync(Config.Port);
    public Task<bool> PairingPortOpenAsync() => OpenAsync(PairingPort);
    private static async Task<bool> OpenAsync(int port)
    {
        using var client = new TcpClient();
        try { await client.ConnectAsync(IPAddress.Loopback, port); return true; }
        catch (SocketException) { return false; }
    }

    /// <summary>A client without AgentConnection's local guards, proving the agent enforces policy by itself.</summary>
    public async Task<RawClient> RawClientAsync()
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, Config.Port);
        var tls = await SecureChannel.ConnectAsync(client, _teacher, Enrollment.DeviceCertificateSha256, TimeSpan.FromSeconds(5), CancellationToken.None);
        await SecureChannel.AuthenticateTeacherAsync(tls, Enrollment.PairingSecret, CancellationToken.None);
        return new RawClient(client, tls, Enrollment);
    }

    public async ValueTask DisposeAsync()
    {
        _server.Dispose();
        await Task.Delay(50);
        try { Directory.Delete(_root, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

static class Fixtures
{
    public static (X509Certificate2 Student, X509Certificate2 Teacher) Current => (StudentIdentity!, TeacherIdentity!);
    public static X509Certificate2? StudentIdentity, TeacherIdentity;
}

sealed class RawClient(TcpClient client, Stream stream, DeviceEnrollment enrollment) : IAsyncDisposable
{
    public Stream Stream { get; } = stream;
    public Task SendJsonAsync<T>(MsgType type, T value) => Frame.WriteJsonAsync(Stream, type, value);
    public Task CommandAsync<T>(MsgType type, T value) =>
        SendJsonAsync(MsgType.ManagedCommand, new ManagedCommand(Guid.NewGuid().ToString("N"), type, Frame.ToJson(value)));
    public async Task StartLessonAsync()
    {
        await SendJsonAsync(MsgType.LessonStart, new LessonRequest(Guid.NewGuid().ToString("N"), enrollment.SchoolId, enrollment.ClassroomId, DateTimeOffset.UtcNow.AddMinutes(5)));
        if (!(await ReadResultsAsync(1))[0].Ok) throw new Exception("Lesson refused");
    }
    public async Task<List<StateResult>> ReadResultsAsync(int count)
    {
        var results = new List<StateResult>();
        while (results.Count < count)
            results.Add(Frame.ReadJson<StateResult>(await ReadUntilAsync(MsgType.StateResult)));
        return results;
    }
    public async Task<byte[]> ReadUntilAsync(MsgType wanted)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var message = await Frame.ReadAsync(Stream, timeout.Token) ?? throw new EndOfStreamException();
            if (message.Type == wanted) return message.Payload;
        }
    }
    public async Task<bool> IsClosedAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        try { return await Frame.ReadAsync(Stream, timeout.Token) is null; }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException) { return true; }
    }
    public async ValueTask DisposeAsync() { await Stream.DisposeAsync(); client.Dispose(); }
}

sealed class FakeUi : IAgentInteraction
{
    public event Action? EndSessionRequested;
    public volatile int Messages;
    public volatile bool Locked, Active, Assisting, Recording, Shared;
    public string Teacher = "", Classroom = "";
    public void RequestEnd() => EndSessionRequested?.Invoke();
    public void ShowMessage(string text, int durationMs) => Messages++;
    public void SetLock(bool locked, string text) => Locked = locked;
    public void ShowBroadcastFrame(byte[] jpeg) { }
    public void HideBroadcast() { }
    public void ShowChat(string from, string text, Func<string, Task> reply) { }
    public void SetManagedState(bool active, string teacher, string classroom, bool assisting, bool recording, bool shared)
    {
        Teacher = teacher; Classroom = classroom; Assisting = assisting; Recording = recording; Shared = shared; Active = active;
    }
}

sealed class FakeRestrictions : ISessionRestrictions
{
    public volatile int Applied, Cleared;
    public volatile bool FailNext;
    public void Apply(RestrictCmd command)
    {
        if (FailNext) { FailNext = false; throw new IOException("Administrator permission is required."); }
        Applied++;
    }
    public void Clear() => Cleared++;
    public void Dispose() { }
}

sealed class FakeCapture
{
    public volatile int Calls;
    public byte[] Jpeg(int width, int quality, int monitor) { Calls++; return new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }; }
}

sealed class FakeOperations
{
    public volatile int Inputs, Powers, Runs, Releases;
    public volatile bool ClearedBeforePower;
    public void Input(RemoteInputCmd command, int monitor) => Inputs++;
    public void Power(PowerCmd command) => Powers++;
    public void Run(RunCmd command) => Runs++;
    public void Release() => Releases++;
}
