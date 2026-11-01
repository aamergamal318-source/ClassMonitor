using System.Text.Json;
using ClassMonitor.Shared.Updates;

if (args.Length != 2 || args[0] != "--apply") return 2;
InstallResult result;
InstallPlan? plan = null;
try
{
    plan = JsonSerializer.Deserialize<InstallPlan>(File.ReadAllText(args[1]))
        ?? throw new InvalidDataException("Invalid installation plan.");
    result = await UpdateInstaller.ApplyAsync(plan);
}
catch (Exception ex) { result = new InstallResult(false, ex.Message); }
try
{
    string directory = Path.GetDirectoryName(Path.GetFullPath(args[1]))!;
    File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(result));
}
catch { /* The transaction result must not interrupt recovery. */ }
return result.Success ? 0 : 1;
