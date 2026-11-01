using System.Diagnostics;
using ClassMonitor.Shared;

namespace ClassMonitor.Agent;

/// <summary>Launches a program or opens a website on this machine, in the student's session.</summary>
internal static class RunControl
{
    public static void Apply(RunCmd c)
    {
        string target = (c.Target ?? "").Trim();
        if (target.Length == 0)
            return;

        try
        {
            if (c.Kind == "website")
            {
                // Make sure bare hosts like "example.com" open as a URL.
                if (!target.Contains("://"))
                    target = "https://" + target;
            }

            // UseShellExecute resolves PATH apps (e.g. "notepad") and opens URLs in the default browser.
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch { /* bad path / blocked by policy; ignore */ }
    }
}
