using System.Text.Json;
using HDMICaptain.Core;

namespace HDMICaptain.App;

/// <summary>
/// <see cref="ViewerPrefs"/> as JSON in %LOCALAPPDATA%\HDMICaptain\prefs.json, the same keys as the Mac (spec §13.7).
/// Inside the MSIX, LocalAppData is the package's own folder. UI thread only.
/// </summary>
public sealed class PrefsStore
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HDMICaptain", "prefs.json");

    public PrefsStore()
    {
        try
        {
            Prefs = ViewerPrefs.Decode(File.Exists(_path) ? File.ReadAllText(_path) : null);
        }
        catch (IOException)
        {
            Prefs = new ViewerPrefs();
        }
    }

    public ViewerPrefs Prefs { get; }

    /// <summary>Main window frame in physical pixels, saved on close (window.json beside prefs.json).</summary>
    public WindowFrame? LoadFrame()
    {
        try
        {
            return File.Exists(FramePath) ? JsonSerializer.Deserialize(File.ReadAllText(FramePath), AppJsonContext.Default.WindowFrame) : null;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return null;
        }
    }

    public void SaveFrame(WindowFrame frame)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FramePath)!);
            File.WriteAllText(FramePath, JsonSerializer.Serialize(frame, AppJsonContext.Default.WindowFrame));
        }
        catch (IOException e)
        {
            AppLog.Write($"couldn't save the window frame: {e.Message}");
        }
    }

    private string FramePath => Path.Combine(Path.GetDirectoryName(_path)!, "window.json");

    public void Update(Action<ViewerPrefs> change)
    {
        change(Prefs);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, Prefs.Encode());
        }
        catch (IOException e)
        {
            AppLog.Write($"couldn't save prefs: {e.Message}");
        }
    }
}

public sealed record WindowFrame(int X, int Y, int Width, int Height);

[System.Text.Json.Serialization.JsonSerializable(typeof(WindowFrame))]
internal sealed partial class AppJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
