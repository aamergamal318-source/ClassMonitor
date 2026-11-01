using ClassMonitor.Shared;

namespace ClassMonitor.Agent;

/// <summary>Receives files pushed by the teacher and reads files the teacher asks to collect.</summary>
internal static class FileTransfer
{
    /// <summary>Folder on the student machine where pushed files are saved.</summary>
    public static string InboxFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ClassMonitor");

    /// <summary>Save a pushed file to the inbox; returns the saved file name.</summary>
    public static string SaveReceived(FilePush push)
    {
        Directory.CreateDirectory(InboxFolder);
        string safeName = Path.GetFileName(push.FileName);
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "received.bin";

        string dest = Path.Combine(InboxFolder, safeName);
        dest = MakeUnique(dest);
        File.WriteAllBytes(dest, Convert.FromBase64String(push.DataBase64));
        return Path.GetFileName(dest);
    }

    /// <summary>Read a file to return to the teacher. Never throws — errors come back in the result.</summary>
    public static FileDataMsg ReadForCollect(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new FileDataMsg(Path.GetFileName(path), "", false, "File not found");

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length + 1024 > Net.MaxMessageBytes)
                return new FileDataMsg(Path.GetFileName(path), "", false, "File too large (max ~16 MB)");

            return new FileDataMsg(Path.GetFileName(path), Convert.ToBase64String(bytes), true, "");
        }
        catch (Exception ex)
        {
            return new FileDataMsg(Path.GetFileName(path), "", false, ex.Message);
        }
    }

    private static string MakeUnique(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
