using System.Drawing;
using System.Drawing.Imaging;

namespace ClassMonitor.Teacher;

/// <summary>
/// Records an agent's live frames to an MJPEG AVI file. The first frame fixes the recording
/// size; later frames are re-encoded to that size if the resolution ever changes.
/// </summary>
internal sealed class Recorder : IDisposable
{
    private const int Fps = 5; // live stream runs ~5 fps while recording

    private readonly AgentConnection _conn;
    private readonly object _lock = new();
    private AviMjpegWriter? _avi;
    private Size _size;

    public string Path { get; }
    public int FrameCount { get; private set; }

    public Recorder(AgentConnection conn, string path)
    {
        _conn = conn;
        Path = path;
        _conn.FrameReceived += OnFrame;
    }

    private void OnFrame(AgentConnection c, byte[] jpeg)
    {
        if (c != _conn) return;
        lock (_lock)
        {
            try
            {
                using var ms = new MemoryStream(jpeg);
                using var img = Image.FromStream(ms);

                if (_avi is null)
                {
                    _size = img.Size;
                    _avi = new AviMjpegWriter(Path, _size.Width, _size.Height, Fps);
                }

                byte[] frame = img.Size == _size ? jpeg : ReEncode(img, _size);
                _avi.WriteFrame(frame);
                FrameCount++;
            }
            catch { /* skip bad frame */ }
        }
    }

    private static byte[] ReEncode(Image img, Size size)
    {
        using var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            g.DrawImage(img, 0, 0, size.Width, size.Height);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Jpeg);
        return ms.ToArray();
    }

    public void Dispose()
    {
        _conn.FrameReceived -= OnFrame;
        lock (_lock)
        {
            _avi?.FinalizeFile();
            _avi?.Dispose();
            _avi = null;
        }
    }
}
