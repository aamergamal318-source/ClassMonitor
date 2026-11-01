using System.Text;

namespace ClassMonitor.Teacher;

/// <summary>
/// Minimal Motion-JPEG AVI writer: each frame is a JPEG stored as a '00dc' chunk, plus an
/// idx1 index. Produces a single .avi playable in VLC / Windows. No external dependencies.
/// All frames must share the same width/height (the caller guarantees this).
/// </summary>
internal sealed class AviMjpegWriter : IDisposable
{
    private readonly BinaryWriter _w;
    private readonly int _width, _height, _fps;
    private readonly List<(uint Offset, uint Size)> _index = new();
    private long _riffSizePos, _moviSizePos, _moviStart, _avihFramesPos, _strhLengthPos;
    private int _frames;
    private bool _finalized;

    public AviMjpegWriter(string path, int width, int height, int fps)
    {
        _width = width;
        _height = height;
        _fps = Math.Max(1, fps);
        _w = new BinaryWriter(File.Create(path));
        WriteHeaders();
    }

    private void Fourcc(string s) => _w.Write(Encoding.ASCII.GetBytes(s));
    private void U32(uint v) => _w.Write(v);

    private void Patch(long pos, uint value)
    {
        long cur = _w.BaseStream.Position;
        _w.BaseStream.Seek(pos, SeekOrigin.Begin);
        _w.Write(value);
        _w.BaseStream.Seek(cur, SeekOrigin.Begin);
    }

    private void WriteHeaders()
    {
        Fourcc("RIFF"); _riffSizePos = _w.BaseStream.Position; U32(0); Fourcc("AVI ");

        Fourcc("LIST"); long hdrlSizePos = _w.BaseStream.Position; U32(0);
        long hdrlStart = _w.BaseStream.Position; Fourcc("hdrl");

        // ---- avih (main AVI header) ----
        Fourcc("avih"); U32(56);
        U32((uint)(1_000_000 / _fps)); // dwMicroSecPerFrame
        U32(0);                        // dwMaxBytesPerSec
        U32(0);                        // dwPaddingGranularity
        U32(0x10);                     // dwFlags = AVIF_HASINDEX
        _avihFramesPos = _w.BaseStream.Position; U32(0); // dwTotalFrames (patched)
        U32(0);                        // dwInitialFrames
        U32(1);                        // dwStreams
        U32(0);                        // dwSuggestedBufferSize
        U32((uint)_width); U32((uint)_height);
        U32(0); U32(0); U32(0); U32(0); // dwReserved[4]

        // ---- strl / strh (stream header) ----
        Fourcc("LIST"); long strlSizePos = _w.BaseStream.Position; U32(0);
        long strlStart = _w.BaseStream.Position; Fourcc("strl");

        Fourcc("strh"); U32(56);
        Fourcc("vids"); Fourcc("MJPG");
        U32(0);                        // dwFlags
        U32(0);                        // wPriority + wLanguage
        U32(0);                        // dwInitialFrames
        U32(1);                        // dwScale
        U32((uint)_fps);               // dwRate -> fps = Rate/Scale
        U32(0);                        // dwStart
        _strhLengthPos = _w.BaseStream.Position; U32(0); // dwLength (patched)
        U32(0);                        // dwSuggestedBufferSize
        U32(0xFFFFFFFF);               // dwQuality
        U32(0);                        // dwSampleSize
        _w.Write((short)0); _w.Write((short)0); _w.Write((short)_width); _w.Write((short)_height); // rcFrame

        // ---- strf (BITMAPINFOHEADER) ----
        Fourcc("strf"); U32(40);
        U32(40);                       // biSize
        U32((uint)_width); U32((uint)_height);
        _w.Write((short)1);            // biPlanes
        _w.Write((short)24);           // biBitCount
        Fourcc("MJPG");                // biCompression
        U32((uint)(_width * _height * 3)); // biSizeImage
        U32(0); U32(0); U32(0); U32(0);    // ppm x/y, clrUsed, clrImportant

        long strlEnd = _w.BaseStream.Position; Patch(strlSizePos, (uint)(strlEnd - strlStart));
        long hdrlEnd = _w.BaseStream.Position; Patch(hdrlSizePos, (uint)(hdrlEnd - hdrlStart));

        // ---- movi list ----
        Fourcc("LIST"); _moviSizePos = _w.BaseStream.Position; U32(0);
        _moviStart = _w.BaseStream.Position; Fourcc("movi");
    }

    public void WriteFrame(byte[] jpeg)
    {
        long chunkStart = _w.BaseStream.Position;
        Fourcc("00dc"); U32((uint)jpeg.Length); _w.Write(jpeg);
        if ((jpeg.Length & 1) == 1) _w.Write((byte)0); // chunks are word-aligned
        _index.Add(((uint)(chunkStart - _moviStart), (uint)jpeg.Length));
        _frames++;
    }

    public void FinalizeFile()
    {
        if (_finalized) return;
        _finalized = true;

        long moviEnd = _w.BaseStream.Position;
        Patch(_moviSizePos, (uint)(moviEnd - _moviStart));

        // idx1
        Fourcc("idx1"); U32((uint)(_index.Count * 16));
        foreach (var (offset, size) in _index)
        {
            Fourcc("00dc");
            U32(0x10);     // AVIIF_KEYFRAME
            U32(offset);
            U32(size);
        }

        long end = _w.BaseStream.Position;
        Patch(_riffSizePos, (uint)(end - (_riffSizePos + 4)));
        Patch(_avihFramesPos, (uint)_frames);
        Patch(_strhLengthPos, (uint)_frames);
        _w.Flush();
    }

    public void Dispose()
    {
        try { FinalizeFile(); } catch { }
        _w.Dispose();
    }
}
