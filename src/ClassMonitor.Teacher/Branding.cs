using System.Drawing;
using System.Reflection;

namespace ClassMonitor.Teacher;

/// <summary>App icon + logo image, loaded once from the executable and embedded resources.</summary>
internal static class Branding
{
    public static readonly Icon? AppIcon = LoadIcon();
    public static readonly Image? Logo = LoadLogo();

    private static Icon? LoadIcon()
    {
        try { return Icon.ExtractAssociatedIcon(Environment.ProcessPath!); }
        catch { return null; }
    }

    private static Image? LoadLogo()
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png");
            return s is null ? null : Image.FromStream(s);
        }
        catch { return null; }
    }
}
