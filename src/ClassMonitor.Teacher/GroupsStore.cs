using System.Text.Json;

namespace ClassMonitor.Teacher;

/// <summary>
/// Named groups of student machines (classes), persisted to JSON in the user's AppData.
/// A group is a list of machine names; the console maps them to whichever agents are online.
/// </summary>
internal sealed class GroupsStore
{
    public Dictionary<string, List<string>> Groups { get; set; } = new();

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    private static string Path_ => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClassMonitor", "groups.json");

    public static GroupsStore Load()
    {
        try
        {
            if (File.Exists(Path_))
                return JsonSerializer.Deserialize<GroupsStore>(File.ReadAllText(Path_)) ?? new GroupsStore();
        }
        catch { /* corrupt -> empty */ }
        return new GroupsStore();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
            File.WriteAllText(Path_, JsonSerializer.Serialize(this, Opts));
        }
        catch { /* ignore write errors */ }
    }
}
