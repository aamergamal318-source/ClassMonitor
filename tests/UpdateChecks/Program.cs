using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClassMonitor.Shared.Updates;

if (args.Length == 2 && args[0] == "--wait-probe")
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    while (!File.Exists(args[1])) await Task.Delay(50, timeout.Token);
    return 0;
}

if (Environment.GetEnvironmentVariable("CLASSMONITOR_UPDATE_PROBE_MARKER") is { } marker)
{
    File.WriteAllText(marker, "restarted:" + Environment.ProcessPath);
    return 0;
}

if (args.Length == 2 && args[0] == "--verify-package")
{
    var v = ReleaseUpdater.CurrentVersion;
    foreach (var component in new[] { UpdateComponent.Teacher, UpdateComponent.Agent })
    {
        string folder = Path.GetFullPath(Path.Combine("tests/UpdateChecks/artifacts", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(folder);
        var stage = ReleaseUpdater.ExtractVerifiedPackage(Path.GetFullPath(args[1]), folder, component, new Version(v.Major, v.Minor, v.Build));
        Console.WriteLine($"PASS: packaged {component}, version {v.ToString(3)}, updater present, SHA256={stage.Sha256}");
    }
    return 0;
}

if (args.Length == 1 && args[0] == "--live-check")
{
    using var service = new ReleaseUpdater();
    var check = await service.CheckAsync(new Version(0, 0, 0));
    var expected = ReleaseUpdater.CurrentVersion;
    if (check.State != UpdateState.Available || check.Release!.Version != new Version(expected.Major, expected.Minor, expected.Build))
        throw new Exception("Latest published release did not match the installed build: " + check.Message);
    Console.WriteLine($"PASS: live GitHub metadata for {check.Release.Version}; SHA256={check.Release.Sha256}");
    return 0;
}

if (args.Length == 1 && args[0] == "--live-download")
{
    // Real public release through the same check, download, hash and extraction path the applications use. Nothing is installed.
    using var service = new ReleaseUpdater();
    var check = await service.CheckAsync(new Version(0, 0, 0));
    if (check.State != UpdateState.Available) throw new Exception(check.Message);
    foreach (var component in new[] { UpdateComponent.Teacher, UpdateComponent.Agent })
    {
        var staged = await service.StageAsync(check.Release!, component);
        Console.WriteLine($"PASS: downloaded and verified {component} {check.Release!.Version}, {check.Release.Size} bytes, SHA256={staged.Sha256}");
    }
    return 0;
}

string workerExecutable = args.Length == 2 && args[0] == "--worker" ? Path.GetFullPath(args[1])
    : Path.GetFullPath("src/ClassMonitor.Updater/bin/Release/net10.0/ClassMonitorUpdater.exe");

string root = Path.GetFullPath(Path.Combine("tests", "UpdateChecks", "artifacts", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
int passed = 0;
try
{
    await Check("newer stable release selected", () =>
    {
        var check = ReleaseUpdater.ParseRelease(ReleaseJson("v2.0.0"), new Version(1, 1, 0, 0));
        Assert(check.State == UpdateState.Available && check.Release!.Version == new Version(2, 0, 0));
        return Task.CompletedTask;
    });
    await Check("in-app update records its version in a setup-created installation", () =>
    {
        string folder = Path.Combine(root, "receipt"); Directory.CreateDirectory(folder);
        string application = Path.Combine(folder, "ClassMonitorTeacher.exe");
        File.Copy(workerExecutable, application);
        string manifest = Path.Combine(folder, "install-manifest.json");
        File.WriteAllText(manifest, "{\"Product\":\"ClassMonitor.ManagedDesktop\",\"Schema\":1,\"Component\":0,\"Version\":\"0.0.1\",\"Files\":{\"ClassMonitorTeacher.exe\":\"OLD\",\"LICENSE\":\"KEEP\"}}");
        (string Component, string Version)? written = null;
        UpdateInstaller.RecordInstalledVersion(application, "NEWHASH", (c, v) => written = (c, v));
        using var receipt = JsonDocument.Parse(File.ReadAllText(manifest));
        var v = ReleaseUpdater.CurrentVersion;
        Assert(receipt.RootElement.GetProperty("Version").GetString() == v.ToString(3) && written == ("Teacher", v.ToString(3)));
        var files = receipt.RootElement.GetProperty("Files");
        Assert(files.GetProperty("ClassMonitorTeacher.exe").GetString() == "NEWHASH" && files.GetProperty("LICENSE").GetString() == "KEEP");
        return Task.CompletedTask;
    });
    await Check("a damaged receipt cannot disturb a committed update", () =>
    {
        string folder = Path.Combine(root, "damaged"); Directory.CreateDirectory(folder);
        string application = Path.Combine(folder, "ClassMonitorTeacher.exe");
        File.Copy(workerExecutable, application);
        string manifest = Path.Combine(folder, "install-manifest.json");
        foreach (string damaged in new[] { "{\"Product\":7,\"Files\":{}}", "not json", "{\"Product\":\"ClassMonitor.ManagedDesktop\",\"Files\":{\"a\":1,\"a\":2}}" })
        {
            File.WriteAllText(manifest, damaged);
            UpdateInstaller.RecordInstalledVersion(application, "HASH", (_, _) => { });
        }
        Assert(Directory.GetFiles(folder, "*.tmp").Length == 0);
        return Task.CompletedTask;
    });
    await Check("portable copy and foreign receipt are left untouched", () =>
    {
        string folder = Path.Combine(root, "portable"); Directory.CreateDirectory(folder);
        string application = Path.Combine(folder, "ClassMonitorAgent.exe");
        File.Copy(workerExecutable, application);
        bool wrote = false;
        UpdateInstaller.RecordInstalledVersion(application, "HASH", (_, _) => wrote = true);
        Assert(!wrote && !File.Exists(Path.Combine(folder, "install-manifest.json")));
        string foreign = "{\"Product\":\"Other\",\"Files\":{}}";
        File.WriteAllText(Path.Combine(folder, "install-manifest.json"), foreign);
        UpdateInstaller.RecordInstalledVersion(application, "HASH", (_, _) => wrote = true);
        Assert(!wrote && File.ReadAllText(Path.Combine(folder, "install-manifest.json")) == foreign);
        return Task.CompletedTask;
    });
    await Check("equal and older releases never downgrade", () =>
    {
        Assert(ReleaseUpdater.ParseRelease(ReleaseJson("v1.1.0"), new Version(1, 1, 0, 0)).State == UpdateState.Current);
        Assert(ReleaseUpdater.ParseRelease(ReleaseJson("v1.0.0"), new Version(1, 1, 0)).State == UpdateState.Current);
        return Task.CompletedTask;
    });
    foreach (string json in new[]
    {
        ReleaseJson("v2.0.0").Replace("\"prerelease\":false", "\"prerelease\":true"),
        ReleaseJson("v2.0.0").Replace("\"draft\":false", "\"draft\":true"),
        ReleaseJson("v2.0.0-beta"),
        ReleaseJson("v2.0.0").Replace("github.com/aamergamal318-source/ClassMonitor/", "example.org/untrusted/"),
        ReleaseJson("v2.0.0").Replace("sha256:", "invalid:"),
        ReleaseJson("v2.0.0").Replace(ReleaseUpdater.AssetName, "wrong.zip")
    })
        await Check("invalid or untrusted release rejected", () => ThrowsAsync(() => Task.FromResult(ReleaseUpdater.ParseRelease(json, new Version(1, 1, 0)))));
    await Check("offline check does not interrupt application", async () =>
    {
        using var service = new ReleaseUpdater(new ReplyHandler(_ => throw new HttpRequestException("offline")), root);
        Assert((await service.CheckAsync(new Version(1, 1, 0))).State == UpdateState.Unavailable);
    });
    await Check("GitHub rate limit handled", async () =>
    {
        using var service = new ReleaseUpdater(new ReplyHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)), root);
        Assert((await service.CheckAsync(new Version(1, 1, 0))).State == UpdateState.Unavailable);
    });
    await Check("malformed API metadata handled", async () =>
    {
        using var service = new ReleaseUpdater(new ReplyHandler(_ => Reply(Encoding.UTF8.GetBytes("{}"))), root);
        Assert((await service.CheckAsync(new Version(1, 1, 0))).State == UpdateState.Unavailable);
    });

    byte[] package = Package();
    var builtVersion = ReleaseUpdater.CurrentVersion;
    var candidate = new ReleaseUpdate(new Version(builtVersion.Major, builtVersion.Minor, builtVersion.Build),
        new Uri($"https://github.com/{ReleaseUpdater.Repository}/releases/download/v{builtVersion.ToString(3)}/{ReleaseUpdater.AssetName}"), Hash(package), package.Length);
    foreach (var component in new[] { UpdateComponent.Teacher, UpdateComponent.Agent })
        await Check($"verified package extracts only {component} and updater", async () =>
        {
            using var service = new ReleaseUpdater(new ReplyHandler(_ => Reply(package)), root);
            var stage = await service.StageAsync(candidate, component);
            Assert(Path.GetFileName(stage.Application) == ReleaseUpdater.FileName(component));
            Assert(File.Exists(stage.Worker) && Directory.GetFiles(stage.Directory).Length == 3);
            Assert(!File.Exists(Path.Combine(root, "escape.txt")));
        });
    await Check("wrong checksum cannot stage an update", async () =>
    {
        using var service = new ReleaseUpdater(new ReplyHandler(_ => Reply(package)), root);
        await ThrowsAsync(() => service.StageAsync(candidate with { Sha256 = new string('0', 64) }, UpdateComponent.Teacher));
    });
    await Check("truncated download rejected", async () =>
    {
        using var service = new ReleaseUpdater(new ReplyHandler(_ => Reply(package[..^1])), root);
        await ThrowsAsync(() => service.StageAsync(candidate, UpdateComponent.Teacher));
    });
    await Check("download larger than declared rejected", async () =>
    {
        using var service = new ReleaseUpdater(new ReplyHandler(_ => Reply(package)), root);
        await ThrowsAsync(() => service.StageAsync(candidate with { Size = package.Length - 1 }, UpdateComponent.Teacher));
    });
    await Check("release tag and executable version must match", async () =>
    {
        using var service = new ReleaseUpdater(new ReplyHandler(_ => Reply(package)), root);
        await ThrowsAsync(() => service.StageAsync(candidate with { Version = new Version(9, 9, 9) }, UpdateComponent.Teacher));
    });
    await Check("duplicate executable entries rejected", async () =>
    {
        byte[] duplicate = Package(duplicate: true);
        using var service = new ReleaseUpdater(new ReplyHandler(_ => Reply(duplicate)), root);
        await ThrowsAsync(() => service.StageAsync(candidate with { Size = duplicate.Length, Sha256 = Hash(duplicate) }, UpdateComponent.Teacher));
    });
    await Check("transaction replaces executable and preserves settings/backup", async () =>
    {
        var plan = Plan();
        var result = await UpdateInstaller.ApplyAsync(plan, path =>
        {
            Assert(File.ReadAllText(path) == "MZ-new-version");
            return Task.CompletedTask;
        });
        Assert(result.Success);
        Assert(File.ReadAllText(Path.Combine(Path.GetDirectoryName(plan.Target)!, "teacher.json")) == "keep-settings");
        Assert(File.ReadAllText(Directory.GetFiles(Path.GetDirectoryName(plan.Target)!, "*.backup-*").Single()) == "MZ-old-version");
    });
    await Check("failed restart restores and relaunches previous version", async () =>
    {
        var plan = Plan();
        int launches = 0;
        var result = await UpdateInstaller.ApplyAsync(plan, path =>
        {
            launches++;
            if (launches == 1) throw new IOException("simulated launch failure");
            Assert(File.ReadAllText(path) == "MZ-old-version");
            return Task.CompletedTask;
        });
        Assert(!result.Success && launches == 2 && File.ReadAllText(plan.Target) == "MZ-old-version");
    });
    await Check("tampered staged executable leaves current program untouched", async () =>
    {
        var plan = Plan();
        File.AppendAllText(plan.Source, "tampered");
        await ThrowsAsync(() => UpdateInstaller.ApplyAsync(plan, _ => Task.CompletedTask));
        Assert(File.ReadAllText(plan.Target) == "MZ-old-version");
    });
    await Check("unrelated target files cannot be replaced", async () =>
    {
        var plan = Plan();
        await ThrowsAsync(() => UpdateInstaller.ApplyAsync(plan with { Target = Path.Combine(root, "unrelated.exe") }));
    });
    await Check("concurrent installation cannot replace the same executable", async () =>
    {
        var plan = Plan();
        using var held = new FileStream(plan.Target + ".update-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            await UpdateInstaller.ApplyAsync(plan, _ => Task.CompletedTask);
            throw new Exception("Expected update lock rejection");
        }
        catch (IOException) { Assert(File.ReadAllText(plan.Target) == "MZ-old-version"); }
    });
    await Check("real updater process installs and starts replacement without windows", () => ExerciseWorkerAsync(false, false));
    await Check("real updater waits for the initiating process to exit", () => ExerciseWorkerAsync(true, false));
    await Check("real failed executable is rolled back and previous app starts", () => ExerciseWorkerAsync(false, true));
    Console.WriteLine($"PASS: {passed} update checks. No visual test windows were opened.");
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }

async Task ExerciseWorkerAsync(bool waitForParent, bool invalidExecutable)
    {
        string folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
        string sourceFolder = Path.Combine(folder, "source");
        string installedFolder = Path.Combine(folder, "installed");
        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(installedFolder);
        foreach (string file in Directory.GetFiles(AppContext.BaseDirectory))
            File.Copy(file, Path.Combine(installedFolder, Path.GetFileName(file)));
        string target = Path.Combine(installedFolder, "ClassMonitorTeacher.exe");
        string source = Path.Combine(sourceFolder, "ClassMonitorTeacher.exe");
        File.Copy(Environment.ProcessPath!, target);
        File.Copy(Environment.ProcessPath!, source);
        await File.AppendAllTextAsync(source, "\nupdated-test-version\n");
        if (invalidExecutable) File.WriteAllText(source, "MZ-not-a-valid-executable");
        string originalHash = Hash(File.ReadAllBytes(target));
        Process? parent = null;
        string gate = Path.Combine(folder, "parent-exit.signal");
        if (waitForParent)
        {
            var parentStart = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            parentStart.ArgumentList.Add("--wait-probe");
            parentStart.ArgumentList.Add(gate);
            parent = Process.Start(parentStart)!;
        }
        var plan = new InstallPlan(source, target, Hash(File.ReadAllBytes(source)), parent?.Id ?? int.MaxValue,
            parent?.StartTime.ToUniversalTime().Ticks ?? 0);
        string planFile = Path.Combine(folder, "install.json");
        File.WriteAllText(planFile, JsonSerializer.Serialize(plan));
        string childMarker = Path.Combine(folder, "restarted.txt");
        var start = new ProcessStartInfo(workerExecutable)
        {
            UseShellExecute = false, CreateNoWindow = true
        };
        start.ArgumentList.Add("--apply");
        start.ArgumentList.Add(planFile);
        start.Environment["CLASSMONITOR_UPDATE_PROBE_MARKER"] = childMarker;
        using var updater = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        if (waitForParent)
        {
            await Task.Delay(400);
            Assert(!updater.HasExited && Hash(File.ReadAllBytes(target)) == originalHash);
            File.WriteAllText(gate, "exit");
        }
        await updater.WaitForExitAsync(timeout.Token);
        Assert(updater.ExitCode == (invalidExecutable ? 1 : 0) && File.Exists(childMarker));
        Assert(Hash(File.ReadAllBytes(target)) == (invalidExecutable ? originalHash : plan.Sha256));
        Assert(JsonSerializer.Deserialize<InstallResult>(File.ReadAllText(Path.Combine(folder, "result.json")))!.Success == !invalidExecutable);
        parent?.Dispose();
    }

async Task Check(string name, Func<Task> run)
{
    await run();
    passed++;
    Console.WriteLine("PASS: " + name);
}
void Assert(bool success) { if (!success) throw new Exception("Assertion failed"); }
async Task ThrowsAsync(Func<Task> run)
{
    try { await run(); }
    catch (Exception ex) when (ex is InvalidDataException or HttpRequestException) { return; }
    throw new Exception("Expected a rejected update");
}
string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
HttpResponseMessage Reply(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
string ReleaseJson(string tag) => JsonSerializer.Serialize(new
{
    tag_name = tag, draft = false, prerelease = false,
    assets = new[] { new { name = ReleaseUpdater.AssetName,
        browser_download_url = $"https://github.com/{ReleaseUpdater.Repository}/releases/download/{tag}/{ReleaseUpdater.AssetName}",
        digest = "sha256:" + new string('A', 64), size = 100 } }
});
byte[] Package(bool duplicate = false)
{
    using var bytes = new MemoryStream();
    using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
    {
        foreach (var item in new[]
        {
            ("ClassMonitor/teacher/ClassMonitorTeacher.exe", "src/ClassMonitor.Teacher/bin/Release/net10.0-windows/ClassMonitorTeacher.exe"),
            ("ClassMonitor/agent/ClassMonitorAgent.exe", "src/ClassMonitor.Agent/bin/Release/net10.0-windows/ClassMonitorAgent.exe"),
            ("ClassMonitor/updater/ClassMonitorUpdater.exe", "src/ClassMonitor.Updater/bin/Release/net10.0/ClassMonitorUpdater.exe")
        }) zip.CreateEntryFromFile(item.Item2, item.Item1);
        using (var writer = new StreamWriter(zip.CreateEntry("../../escape.txt").Open())) writer.Write("must not extract");
        if (duplicate) zip.CreateEntryFromFile("src/ClassMonitor.Teacher/bin/Release/net10.0-windows/ClassMonitorTeacher.exe", "ClassMonitor/teacher/ClassMonitorTeacher.exe");
    }
    return bytes.ToArray();
}
InstallPlan Plan()
{
    string folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(folder, "source"));
    Directory.CreateDirectory(Path.Combine(folder, "installed"));
    string source = Path.Combine(folder, "source", "ClassMonitorTeacher.exe");
    string target = Path.Combine(folder, "installed", "ClassMonitorTeacher.exe");
    File.WriteAllText(source, "MZ-new-version");
    File.WriteAllText(target, "MZ-old-version");
    File.WriteAllText(Path.Combine(folder, "installed", "teacher.json"), "keep-settings");
    return new InstallPlan(source, target, Hash(File.ReadAllBytes(source)), int.MaxValue, 0);
}

internal sealed class ReplyHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(reply(request));
}
