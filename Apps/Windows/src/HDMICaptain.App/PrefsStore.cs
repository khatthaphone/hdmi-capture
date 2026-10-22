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
