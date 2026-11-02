using System.Diagnostics;
using ClassMonitor.Shared;
namespace ClassMonitor.Agent;

internal sealed record ApprovedLaunch(string Target, bool UseShellExecute);
internal static class RunControl
{
    public static ApprovedLaunch Validate(RunCmd command)
    {
        ArgumentNullException.ThrowIfNull(command);
        string target = (command.Target ?? "").Trim();
        if (target.Length is 0 or > 4096 || target.Any(char.IsControl)) throw new InvalidDataException("Invalid launch target.");
        if (command.Kind == "website")
        {
            if (!target.Contains(':')) target = "https://" + target;
            if (!Uri.TryCreate(target, UriKind.Absolute, out var address) || address.Scheme is not ("https" or "http") || string.IsNullOrWhiteSpace(address.Host)
                || address.UserInfo.Length != 0 || address.HostNameType is UriHostNameType.Unknown or UriHostNameType.Basic)
                throw new InvalidDataException("Only HTTP and HTTPS lesson links without embedded credentials are allowed.");
            return new ApprovedLaunch(address.AbsoluteUri, true);
        }
        if (command.Kind != "program") throw new InvalidDataException("Unsupported launch type.");
        string name = target.ToLowerInvariant() switch
        {
            "notepad" or "notepad.exe" => "notepad.exe", "calc" or "calc.exe" => "calc.exe", "mspaint" or "mspaint.exe" => "mspaint.exe",
            _ => throw new InvalidDataException("Only the approved Windows Notepad, Calculator and Paint applications may be launched.")
        };
        return new ApprovedLaunch(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name), false);
    }
    public static void Apply(RunCmd command)
    {
        var approved = Validate(command);
        using var process = Process.Start(new ProcessStartInfo(approved.Target) { UseShellExecute = approved.UseShellExecute });
    }
}
