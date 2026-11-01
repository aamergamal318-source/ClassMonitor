using System.Drawing;
using System.Drawing.Imaging;

namespace ClassMonitor.Teacher;

/// <summary>Captures the teacher's own desktop as a scaled JPEG for broadcasting to agents.</summary>
internal static class TeacherCapture
{
    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public static byte[] CaptureJpeg(int maxWidth, int quality)
    {
        var bounds = SystemInformation.VirtualScreen;
        using var full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(full))
        {
            try { g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size); }
            catch { g.Clear(Color.DimGray); }
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
