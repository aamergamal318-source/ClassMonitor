using System.Text.Json;

namespace ClassMonitor.Shared.Settings;

/// <summary>Standalone profile persistence. AppConfig owners may persist the same model as an additive JSON property.</summary>
public static class SchoolSettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public const int MaximumProfileBytes = 256 * 1024;

    public static SchoolSettings Load(string path)
    {
        if (!File.Exists(path)) return new SchoolSettings();
        if (new FileInfo(path).Length > MaximumProfileBytes) throw new InvalidDataException("Settings profile is too large.");
        try
        {
            string json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("SchemaVersion", out _))
                throw new InvalidDataException("This file is not a versioned ClassMonitor preferences profile.");
            RejectDuplicateProperties(document.RootElement);
            var settings = JsonSerializer.Deserialize<SchoolSettings>(json, Options)
                ?? throw new InvalidDataException("Settings profile cannot be null.");
            settings.EnsureValid();
            return settings;
        }
        catch (JsonException ex) { throw new InvalidDataException("Settings profile contains invalid JSON; it was not reset.", ex); }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Settings profile contains duplicate properties.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }

    public static void SaveAtomic(string path, SchoolSettings settings)
    {
        settings.EnsureValid();
        string full = Path.GetFullPath(path);
        string parent = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(parent);
        string temporary = Path.Combine(parent, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        string json = JsonSerializer.Serialize(settings, Options);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumProfileBytes) throw new InvalidDataException("Settings profile is too large.");
        try
        {
            File.WriteAllText(temporary, json);
            if (File.Exists(full)) File.Replace(temporary, full, full + ".previous");
            else File.Move(temporary, full);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
