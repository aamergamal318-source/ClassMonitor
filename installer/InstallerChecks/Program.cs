using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using ClassMonitor.Installation;

string artifacts = Path.GetFullPath("installer/InstallerChecks/artifacts");
Directory.CreateDirectory(artifacts);
string root = Path.Combine(artifacts, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
string script = File.ReadAllText("installer/ClassMonitor.Installer/Core/Uninstall.ps1");
var framework = new System.Runtime.Versioning.FrameworkName(AppContext.TargetFrameworkName!).Version;
string tfm = $"net{framework.Major}.{framework.Minor}";
string version = typeof(Program).Assembly.GetName().Version!.ToString(3);
int passed = 0;
try
{
    foreach (var component in new[] { InstallComponent.Teacher, InstallComponent.Agent })
        await Check($"{component} install, upgrade and uninstall in isolated directory", () =>
        {
            string target = Target(); var registration = new FakeRegistration();
            using var package = Package();
            var receipt = InstallerCore.Install(package, component, target, version, script, registration);
            Assert(receipt.Component == component && registration.Value == component.ToString());
            Assert(File.Exists(Path.Combine(target, InstallerCore.ApplicationName(component))) && File.Exists(Path.Combine(target, "Uninstall.ps1")));
            string unrelated = Path.Combine(target, "lesson-notes.txt"); File.WriteAllText(unrelated, "preserve me");
            using var upgrade = Package(overlay: true);
            InstallerCore.Install(upgrade, component, target, version, script, registration);
            Assert(File.ReadAllText(unrelated) == "preserve me");
            InstallerCore.Uninstall(target, registration);
            Assert(registration.Value is null && File.Exists(unrelated) && !File.Exists(Path.Combine(target, InstallerCore.ApplicationName(component))));
            return Task.CompletedTask;
        });
    await Check("registry failure rolls back prior executable and registration", () =>
    {
        string target = Target(); var registration = new FakeRegistration();
        using (var package = Package()) InstallerCore.Install(package, InstallComponent.Teacher, target, version, script, registration);
        byte[] before = File.ReadAllBytes(Path.Combine(target, "ClassMonitorTeacher.exe"));
        registration.FailRegistration = true;
        using var update = Package(overlay: true);
        ExpectFailure(() => InstallerCore.Install(update, InstallComponent.Teacher, target, version, script, registration));
        Assert(before.SequenceEqual(File.ReadAllBytes(Path.Combine(target, "ClassMonitorTeacher.exe"))) && registration.Value == "Teacher");
        return Task.CompletedTask;
    });
    await Check("uninstall registration failure restores installed files", () =>
    {
        string target = Target(); var registration = new FakeRegistration();
        using (var package = Package()) InstallerCore.Install(package, InstallComponent.Agent, target, version, script, registration);
        registration.FailRemoval = true;
        ExpectFailure(() => InstallerCore.Uninstall(target, registration));
        Assert(File.Exists(Path.Combine(target, "ClassMonitorAgent.exe")) && registration.Value == "Agent");
        return Task.CompletedTask;
    });
    await Check("duplicate executable package entry rejected", () =>
    {
        using var package = Package(duplicate: true);
        ExpectFailure(() => InstallerCore.Install(package, InstallComponent.Teacher, Target(), version, script, new FakeRegistration()));
        return Task.CompletedTask;
    });
    await Check("unexpected archive paths cannot escape destination", () =>
    {
        string target = Target(); using var package = Package(traversal: true);
        InstallerCore.Install(package, InstallComponent.Teacher, target, version, script, new FakeRegistration());
        Assert(!File.Exists(Path.Combine(root, "escape.txt")) && !File.Exists(Path.Combine(target, "escape.txt")));
        return Task.CompletedTask;
    });
    await Check("unrecognized nonempty directory cannot be adopted", () =>
    {
        string target = Target(); Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "my-data.txt"), "keep");
        using var package = Package();
        ExpectFailure(() => InstallerCore.Install(package, InstallComponent.Teacher, target, version, script, new FakeRegistration()));
        Assert(File.ReadAllText(Path.Combine(target, "my-data.txt")) == "keep"); return Task.CompletedTask;
    });
    await Check("component mismatch cannot replace another component", () =>
    {
        string target = Target(); var registration = new FakeRegistration();
        using (var package = Package()) InstallerCore.Install(package, InstallComponent.Teacher, target, version, script, registration);
        using var replacement = Package();
        ExpectFailure(() => InstallerCore.Install(replacement, InstallComponent.Agent, target, version, script, registration));
        Assert(File.Exists(Path.Combine(target, "ClassMonitorTeacher.exe"))); return Task.CompletedTask;
    });
    await Check("wrong package version cannot install", () =>
    {
        using var package = Package();
        ExpectFailure(() => InstallerCore.Install(package, InstallComponent.Teacher, Target(), "99.1.2", script, new FakeRegistration()));
        return Task.CompletedTask;
    });
    await Check("unsafe receipt filenames cannot uninstall outside root", () =>
    {
        string target = Target(); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, InstallerCore.ManifestName), JsonSerializer.Serialize(new InstallReceipt(InstallerCore.Product, 1,
            InstallComponent.Teacher, version, new Dictionary<string, string> { ["ClassMonitorTeacher.exe"] = "hash", ["../outside.txt"] = "hash" })));
        ExpectFailure(() => InstallerCore.Uninstall(target, new FakeRegistration())); return Task.CompletedTask;
    });
    await Check("filesystem root rejected", () =>
    {
        ExpectFailure(() => InstallerCore.ValidateRoot(Path.GetPathRoot(root)!)); return Task.CompletedTask;
    });
    await Check("locked existing executable prevents partial upgrade", () =>
    {
        string target = Target(); var registration = new FakeRegistration();
        using (var package = Package()) InstallerCore.Install(package, InstallComponent.Teacher, target, version, script, registration);
        using var held = new FileStream(Path.Combine(target, "ClassMonitorTeacher.exe"), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var update = Package(overlay: true);
        ExpectFailure(() => InstallerCore.Install(update, InstallComponent.Teacher, target, version, script, registration));
        Assert(registration.Value == "Teacher"); return Task.CompletedTask;
    });
    await Check("install keeps only the chosen role and no setup copy", () =>
    {
        string target = Target(); using var package = Package();
        var receipt = InstallerCore.Install(package, InstallComponent.Agent, target, version, script, new FakeRegistration());
        Assert(!File.Exists(Path.Combine(target, "ClassMonitorSetup.exe")) && !File.Exists(Path.Combine(target, "ClassMonitorTeacher.exe")));
        Assert(!receipt.Files.ContainsKey("ClassMonitorSetup.exe") && File.Exists(Path.Combine(target, "ClassMonitorUpdater.exe")));
        return Task.CompletedTask;
    });
    await Check("upgrade removes a setup copy kept by an earlier build", () =>
    {
        string target = Target(); var registration = new FakeRegistration();
        using (var package = Package()) InstallerCore.Install(package, InstallComponent.Teacher, target, version, script, registration);
        string manifest = Path.Combine(target, InstallerCore.ManifestName);
        var old = JsonSerializer.Deserialize<InstallReceipt>(File.ReadAllText(manifest))!;
        old.Files["ClassMonitorSetup.exe"] = "hash";
        File.WriteAllText(manifest, JsonSerializer.Serialize(old));
        File.WriteAllText(Path.Combine(target, "ClassMonitorSetup.exe"), "old setup");
        using (var package = Package(overlay: true)) InstallerCore.Install(package, InstallComponent.Teacher, target, version, script, registration);
        Assert(!File.Exists(Path.Combine(target, "ClassMonitorSetup.exe")));
        return Task.CompletedTask;
    });
    await Check("firewall rule is scoped to the program, account and required port", () =>
    {
        string program = Path.Combine(root, "ClassMonitorAgent.exe");
        string[] agent = InstallerCore.FirewallAddArguments(InstallComponent.Agent, program, "student");
        Assert(agent.Contains("protocol=TCP") && agent.Contains("localport=5900") && agent.Contains("program=" + program) && agent.Contains("name=ClassMonitor Student (student)"));
        Assert(agent.Contains("profile=domain,private") && agent.Contains("remoteip=localsubnet") && !agent.Any(a => a.Contains("public")));
        string[] teacher = InstallerCore.FirewallAddArguments(InstallComponent.Teacher, program, "teacher");
        Assert(teacher.Contains("protocol=UDP") && teacher.Contains("localport=5901"));
        ExpectFailure(() => InstallerCore.FirewallAddArguments(InstallComponent.Agent, "relative.exe", "student"));
        ExpectFailure(() => InstallerCore.FirewallAddArguments(InstallComponent.Agent, program, "a\" b"));
        return Task.CompletedTask;
    });
    await Check("Apps entry runs the installed uninstall script, not a setup copy", () =>
    {
        string command = InstallerCore.UninstallCommand(@"C:\Windows\System32", @"C:\Install Root");
        Assert(command.Contains("\"C:\\Install Root\\Uninstall.ps1\"") && !command.Contains("ClassMonitorSetup") && !command.Contains("-Confirmed"));
        return Task.CompletedTask;
    });
    await Check("PowerShell uninstall worker removes only owned temporary files", async () =>
    {
        string target = Target(); using (var package = Package()) InstallerCore.Install(package, InstallComponent.Agent, target, version, script, new FakeRegistration());
        File.WriteAllText(Path.Combine(target, "student-created.txt"), "keep");
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(target, "Uninstall.ps1"), "-Confirmed", "-SkipRegistration" }) start.ArgumentList.Add(argument);
        using var child = Process.Start(start)!;
        string error = await child.StandardError.ReadToEndAsync();
        await child.WaitForExitAsync();
        if (child.ExitCode != 0) throw new Exception(error);
        Assert(File.Exists(Path.Combine(target, "student-created.txt")) && !File.Exists(Path.Combine(target, "ClassMonitorAgent.exe")) && !File.Exists(Path.Combine(target, "Uninstall.ps1")));
    });
    Console.WriteLine($"PASS: {passed} installer checks; all files under {root}; no registry writes, no real installation or visible windows.");
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }

async Task Check(string name, Func<Task> test) { await test(); passed++; Console.WriteLine("PASS: " + name); }
void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
void ExpectFailure(Action action)
{
    try { action(); } catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { return; }
    throw new Exception("Expected rejection");
}
string Target() => Path.Combine(root, Guid.NewGuid().ToString("N"));
MemoryStream Package(bool overlay = false, bool duplicate = false, bool traversal = false)
{
    var bytes = new MemoryStream();
    using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
    {
        foreach (string role in new[] { "Teacher", "Agent", "Updater" })
        {
            string file = Path.GetFullPath($"src/ClassMonitor.{role}/bin/Release/{(role == "Updater" ? tfm : tfm + "-windows")}/ClassMonitor{role}.exe");
            var entry = zip.CreateEntry($"ClassMonitor/{role.ToLowerInvariant()}/ClassMonitor{role}.exe");
            using var output = entry.Open(); using var input = File.OpenRead(file); input.CopyTo(output);
            if (overlay) output.Write(new byte[] { 0, 1, 2, 3 });
        }
        using (var writer = new StreamWriter(zip.CreateEntry("ClassMonitor/LICENSE").Open())) writer.Write(File.ReadAllText("LICENSE"));
        if (duplicate) zip.CreateEntryFromFile($"src/ClassMonitor.Teacher/bin/Release/{tfm}-windows/ClassMonitorTeacher.exe", "ClassMonitor/teacher/ClassMonitorTeacher.exe");
        if (traversal) using (var writer = new StreamWriter(zip.CreateEntry("../../escape.txt").Open())) writer.Write("must not extract");
    }
    bytes.Position = 0; return bytes;
}
internal sealed class FakeRegistration : IInstallerRegistration
{
    public string? Value;
    public bool FailRegistration, FailRemoval;
    public object? Capture(InstallComponent component) => Value;
    public void Register(InstallReceipt receipt, string root) { Value = receipt.Component.ToString(); if (FailRegistration) throw new IOException("simulated registry failure"); }
    public void Restore(InstallComponent component, object? snapshot) => Value = (string?)snapshot;
    public void Remove(InstallComponent component) { Value = null; if (FailRemoval) throw new IOException("simulated removal failure"); }
}
