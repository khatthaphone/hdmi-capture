using System.Text.Json;
using System.Text.Json.Serialization;
using HDMICaptain.Core;
using HDMICaptain.Linux.Media;

namespace HDMICaptain.Linux.App;

/// <summary>
/// <see cref="ViewerPrefs"/> as JSON in $XDG_CONFIG_HOME/hdmicaptain/prefs.json with the same keys as the Mac and
/// Windows (spec §14.7), per-card presets and profiles beside it (<see cref="PresetStore"/>), and app state that
/// isn't a preference (window size, preferred card) in state.json. Writes are atomic. UI thread only.
/// QA launch arguments never come through here.
/// </summary>
public sealed class PrefsStore
{
    public PrefsStore(string? folder = null)
    {
        Folder = folder ?? Path.Combine(ConfigHome(), "hdmicaptain");
        Presets = new PresetStore(Folder);
        Prefs = ViewerPrefs.Decode(Read("prefs.json"));
        try
        {
            State = Read("state.json") is { } text ? JsonSerializer.Deserialize(text, LinuxJsonContext.Default.AppState) ?? new() : new();
        }
        catch (JsonException)
        {
            State = new AppState();
        }
    }

    public string Folder { get; }
    public ViewerPrefs Prefs { get; }
    public PresetStore Presets { get; }
    public AppState State { get; private set; }

    public void Update(Action<ViewerPrefs> change)
    {
        change(Prefs);
        Write("prefs.json", Prefs.Encode());
    }

    public void UpdateState(Func<AppState, AppState> change)
    {
        State = change(State);
        Write("state.json", JsonSerializer.Serialize(State, LinuxJsonContext.Default.AppState));
    }

    public static string ConfigHome() =>
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

    private string? Read(string name)
    {
        try
        {
            return AtomicFile.ReadAllText(Path.Combine(Folder, name));
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void Write(string name, string text)
    {
        try
        {
            AtomicFile.WriteAllText(Path.Combine(Folder, name), text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LinuxLog.Write($"couldn't save {name}: {e.Message}");
        }
    }
}

/// <summary>Remembered between launches: window size (logical px) and the card the user picked last (DeviceKey).</summary>
public sealed record AppState(int? WindowWidth = null, int? WindowHeight = null, string? PreferredDeviceKey = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppState))]
internal sealed partial class LinuxJsonContext : JsonSerializerContext;
