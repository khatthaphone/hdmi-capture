using HDMICaptain.Core;
using HDMICaptain.Linux.Media;

namespace HDMICaptain.Linux.App;

/// <summary>
/// The settings of the card on screen: its <see cref="DevicePreset"/> (format, picture, sound, look, LUT), loaded when
/// the card changes and saved on every edit, plus the app-wide <see cref="ViewerPrefs"/>. The window and the settings
/// window both edit through here; <see cref="Changed"/> tells everyone to re-read. UI thread only.
/// </summary>
public sealed class ViewerSettings
{
    private readonly PrefsStore _store;
    private string? _deviceKey;
    private CubeLUT? _lut;
    private string? _lutLoadedFrom;

    public ViewerSettings(PrefsStore store) => _store = store;

    public event Action? Changed;

    public PrefsStore Store => _store;
    public ViewerPrefs Prefs => _store.Prefs;
    public DevicePreset Preset { get; private set; } = new();
    public string? DeviceKey => _deviceKey;

    /// <summary>The loaded .cube for <see cref="DevicePreset.LutPath"/>, or null (none, unreadable).</summary>
    public CubeLUT? Lut
    {
        get
        {
            if (Preset.LutPath != _lutLoadedFrom)
            {
                _lutLoadedFrom = Preset.LutPath;
                _lut = null;
                if (Preset.LutPath is { } path)
                {
                    try
                    {
                        _lut = CubeLUT.Parse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or CubeLUT.ParseException)
                    {
                        LinuxLog.Write($"lut: {path}: {e.Message}");
                    }
                }
            }
            return _lut;
        }
    }

    /// <summary>The card on screen changed (or went away: null keeps the last preset in use).</summary>
    public void UseDevice(string? deviceKey)
    {
        if (deviceKey == null || deviceKey == _deviceKey) return;
        _deviceKey = deviceKey;
        Preset = _store.Presets.Get(deviceKey);
        Changed?.Invoke();
    }

    public void Edit(Func<DevicePreset, DevicePreset> change)
    {
        var next = change(Preset);
        if (next == Preset) return;
        Preset = next;
        if (_deviceKey != null) _store.Presets.Set(_deviceKey, Preset);
        Changed?.Invoke();
    }

    public void EditPrefs(Action<ViewerPrefs> change)
    {
        _store.Update(change);
        Changed?.Invoke();
    }

    public void ApplyProfile(Profile profile) => Edit(p => profile.Settings.ApplyTo(p));
}
