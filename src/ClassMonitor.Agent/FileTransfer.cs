using ClassMonitor.Shared;
using ClassMonitor.Shared.Settings;
namespace ClassMonitor.Agent;

internal static class FileTransfer
{
    public static string InboxFolder => new FileSettings().ReceiveDirectory;
    public static string SaveReceived(FilePush push) => SaveReceived(push, new FileSettings());
    public static string SaveReceived(FilePush push, FileSettings settings)
    {
        Validate(settings);
        ArgumentNullException.ThrowIfNull(push);
        if (push.DataBase64 is null) throw new InvalidDataException("Missing lesson data.");
        long maximum = Math.Min(settings.MaximumTransferBytes, MaximumRawFrameBytes);
        long maximumEncoded = 4 * ((maximum + 2) / 3);
        if (push.DataBase64.Length > maximumEncoded || push.DataBase64.Length % 4 != 0 || push.DataBase64.Any(char.IsWhiteSpace))
            throw new InvalidDataException("Lesson data exceeds the permitted size or encoding.");
        string destination = ApprovedFilePaths.ResolveFileName(settings.ReceiveDirectory, push.FileName);
        byte[] bytes;
        try { bytes = Convert.FromBase64String(push.DataBase64); }
        catch (FormatException error) { throw new InvalidDataException("Invalid lesson encoding.", error); }
        ApprovedFilePaths.CheckLength(bytes.LongLength, settings);
        if (bytes.LongLength > maximum) throw new InvalidDataException("Lesson data exceeds the protocol limit.");
        Directory.CreateDirectory(settings.ReceiveDirectory);
        string stem = Path.GetFileNameWithoutExtension(destination), extension = Path.GetExtension(destination);
        for (int suffix = 0; suffix < 10000; suffix++)
        {
            string name = suffix == 0 ? push.FileName : $"{stem} ({suffix}){extension}";
            string candidate = ApprovedFilePaths.ResolveFileName(settings.ReceiveDirectory, name);
            try { using var output = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None); output.Write(bytes); return name; }
            catch (IOException) when (File.Exists(candidate)) { }
        }
        throw new IOException("Too many files share this lesson name.");
    }
    public static FileDataMsg ReadForCollect(string path) => ReadForCollect(path, new FileSettings());
    public static FileDataMsg ReadForCollect(string path, FileSettings settings)
    {
        try
        {
            Validate(settings);
            string file = ApprovedFilePaths.ResolveRelative(settings.CollectDirectory, path);
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            ApprovedFilePaths.CheckLength(input.Length, settings);
            if (input.Length > MaximumRawFrameBytes) throw new InvalidDataException("Lesson file exceeds the protocol limit.");
            byte[] bytes = new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
            if (input.ReadByte() != -1) throw new IOException("Lesson file changed during collection.");
            return new FileDataMsg(Path.GetFileName(file), Convert.ToBase64String(bytes), true, "");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { return new FileDataMsg("", "", false, "Lesson file was unavailable, outside its approved directory, or exceeded its limit."); }
    }
    private static long MaximumRawFrameBytes => (Net.MaxMessageBytes - 16384L) / 4 * 3;
    private static void Validate(FileSettings settings) { ArgumentNullException.ThrowIfNull(settings); new SchoolSettings { Files = settings }.EnsureValid(); }
}
