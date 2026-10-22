using System.Text.Json;

namespace HDMICaptain.Core;

/// <summary>Whole-file writes that never leave a half-written file: write a temp file next to it, then replace.</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string text)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        var temp = $"{path}.{Environment.ProcessId}.tmp";
        try
        {
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>The file's text, or null when it is missing or unreadable.</summary>
    public static string? ReadAllText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

/// <summary>
/// Per-card presets in <c>presets.json</c> (key = <see cref="DeviceKey"/>) and the user's profiles in
/// <c>profiles.json</c>, in one folder. Plain class: no UI types, so it is unit-tested. Writes are atomic;
/// damaged files read as empty. Not thread-safe; call from one thread (the UI thread in the app).
/// </summary>
public sealed class PresetStore
{
    private readonly string _presetsPath;
    private readonly string _profilesPath;
    private Dictionary<string, DevicePreset> _presets;

    public PresetStore(string folder)
    {
        _presetsPath = Path.Combine(folder, "presets.json");
        _profilesPath = Path.Combine(folder, "profiles.json");
        _presets = ReadPresets(AtomicFile.ReadAllText(_presetsPath));
    }

    /// <summary>The saved preset for the card, or the defaults for one we haven't seen.</summary>
    public DevicePreset Get(string deviceKey) => _presets.TryGetValue(deviceKey, out var p) ? p : new DevicePreset();

    public bool Has(string deviceKey) => _presets.ContainsKey(deviceKey);

    public void Set(string deviceKey, DevicePreset preset)
    {
        _presets[deviceKey] = preset;
        AtomicFile.WriteAllText(_presetsPath, WritePresets(_presets));
    }

    /// <summary>Built-in profiles first, then the user's.</summary>
    public IReadOnlyList<Profile> LoadProfiles() =>
        [.. Profile.BuiltIns, .. Profile.Decode(AtomicFile.ReadAllText(_profilesPath))];

    public void SaveProfiles(IEnumerable<Profile> profiles) =>
        AtomicFile.WriteAllText(_profilesPath, Profile.Encode(profiles));

    private static Dictionary<string, DevicePreset> ReadPresets(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        try
        {
            using var doc = JsonDocument.Parse(text);
            var result = new Dictionary<string, DevicePreset>();
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
            // Each card on its own so one damaged entry doesn't lose the others.
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                try
                {
                    if (entry.Value.Deserialize(CoreJsonContext.Default.DevicePreset) is { } preset) result[entry.Name] = preset;
                }
                catch (JsonException) { }
            }
            return result;
        }
        catch (JsonException) { return []; }
    }

    private static string WritePresets(Dictionary<string, DevicePreset> presets)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var (key, preset) in presets.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(key);
                JsonSerializer.Serialize(writer, preset, CoreJsonContext.Default.DevicePreset);
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
