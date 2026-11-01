using System.Drawing;
using System.Drawing.Imaging;

namespace ClassMonitor.Agent;

/// <summary>Captures the full virtual desktop and encodes a scaled JPEG snapshot.</summary>
internal static class ScreenCapture
{
    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    /// <summary>Bounds of the whole desktop across all monitors (device pixels).</summary>
    public static Rectangle VirtualBounds => SystemInformation.VirtualScreen;

    /// <summary>Bounds for a capture request: the whole desktop (index &lt; 0) or one monitor.</summary>
    private static Rectangle BoundsFor(int monitorIndex)
    {
        if (monitorIndex >= 0)
        {
            var screens = Screen.AllScreens;
            if (monitorIndex < screens.Length)
                return screens[monitorIndex].Bounds;
        }
        return VirtualBounds;
    }

    /// <summary>
    /// Grab the desktop (or a single monitor), scale so the width is at most
    /// <paramref name="maxWidth"/>, and return JPEG bytes.
    /// </summary>
    public static byte[] CaptureJpeg(int maxWidth, int quality, int monitorIndex = -1)
    {
        var bounds = BoundsFor(monitorIndex);
        using var full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(full))
        {
            try
            {
                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            }
            catch (Exception)
            {
                // CopyFromScreen can fail on a secure desktop (UAC/lock screen); send a placeholder.
                g.Clear(Color.DimGray);
            }
        }

        double scale = maxWidth > 0 && bounds.Width > maxWidth ? (double)maxWidth / bounds.Width : 1.0;
        int w = Math.Max(1, (int)(bounds.Width * scale));
        int h = Math.Max(1, (int)(bounds.Height * scale));

        using var scaled = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            g.DrawImage(full, 0, 0, w, h);
        }

        using var ms = new MemoryStream();
        using var p = new EncoderParameters(1);
        p.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(quality, 10, 100));
        scaled.Save(ms, JpegCodec, p);
        return ms.ToArray();
    }
}
