using System.Text.Json;
using HDMICaptain.Core;

namespace HDMICaptain.App;

/// <summary>
/// <see cref="ViewerPrefs"/> as JSON in %LOCALAPPDATA%\HDMICaptain\prefs.json, the same keys as the Mac (spec §13.7),
/// plus app state that isn't a preference (window frame, preferred card) in state.json beside it.
/// Inside the MSIX, LocalAppData is the package's own folder. UI thread only.
/// </summary>
public sealed class PrefsStore
{
    private readonly string _folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HDMICaptain");

    public PrefsStore()
    {
        Prefs = ViewerPrefs.Decode(ReadText("prefs.json"));
        try
        {
            State = ReadText("state.json") is { } text ? JsonSerializer.Deserialize(text, AppJsonContext.Default.AppState) ?? new() : new();
        }
        catch (JsonException)
        {
            State = new AppState();
        }
    }

    public ViewerPrefs Prefs { get; }

    public AppState State { get; private set; }

    public void Update(Action<ViewerPrefs> change)
    {
        change(Prefs);
        WriteText("prefs.json", Prefs.Encode());
    }

    public void UpdateState(Func<AppState, AppState> change)
    {
        State = change(State);
        WriteText("state.json", JsonSerializer.Serialize(State, AppJsonContext.Default.AppState));
    }

    private string? ReadText(string name)
    {
        try
        {
            var path = Path.Combine(_folder, name);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void WriteText(string name, string text)
    {
        try
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(Path.Combine(_folder, name), text);
        }
        catch (IOException e)
        {
            AppLog.Write($"couldn't save {name}: {e.Message}");
        }
    }
}

/// <summary>Main window frame in physical pixels.</summary>
public sealed record WindowFrame(int X, int Y, int Width, int Height);

/// <summary>Remembered between launches: where the window was and which card the user picked last (DeviceKey).</summary>
public sealed record AppState(WindowFrame? Frame = null, string? PreferredDeviceKey = null);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(AppState))]
internal sealed partial class AppJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
