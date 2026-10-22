using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HDMICaptain.Core;
using HDMICaptain.Media.Capture;
using HDMICaptain.Media.Render;
using Microsoft.UI.Dispatching;

namespace HDMICaptain.App.ViewModels;

/// <summary>What a <see cref="SettingsModel.Changed"/> notification covers.</summary>
[Flags]
public enum SettingsChange
{
    None = 0,
    /// <summary>Window-level prefs (hover bar, on top, clean mode, aspect lock, diagnostics).</summary>
    Prefs = 1,
    /// <summary>Scale, filter, colour range, matrix: <see cref="SettingsModel.ApplyTo(D3DRenderer)"/>.</summary>
    Render = 2,
    /// <summary>Brightness, contrast, saturation, gamma, sharpness: also in <see cref="SettingsModel.ApplyTo(D3DRenderer)"/>.</summary>
    Adjustments = 4,
    /// <summary>Delay, channel mode, volume, mute: <see cref="SettingsModel.AudioSettings"/>.</summary>
    Audio = 8,
    /// <summary>Format preference (the model already applies it to the attached CaptureController).</summary>
    Format = 16,
    /// <summary>The current card changed, so every preset-backed value may have changed.</summary>
    Device = Render | Adjustments | Audio | Format | 32,
    All = Prefs | Device,
}

/// <summary>
/// Single source of truth for the window prefs (<see cref="ViewerPrefs"/>) and the current card's
/// <see cref="DevicePreset"/>. The settings window binds to it; MainWindow subscribes to <see cref="Changed"/>.
/// Saves are debounced (400 ms) and atomic through <see cref="PrefsStore"/>; presets are stored per card key,
/// so a card gets its own picture, sound and format back when it is plugged in again. UI thread only.
///
/// Integration (see the 095 task log): create one with <c>new SettingsModel(_prefs)</c>, call
/// <see cref="Attach(ViewerModel)"/> and <see cref="Attach(CaptureController)"/>, subscribe to
/// <see cref="Changed"/>, call <see cref="OnDeviceChanged"/> when the live card changes, and
/// <see cref="Flush"/> on close.
/// </summary>
public sealed partial class SettingsModel : ObservableObject
{
    /// <summary>Preset used when no card has been seen yet; also the template a never-seen card starts from.</summary>
    public const string DefaultKey = "default";

    private readonly PrefsStore _store;
    private readonly DispatcherQueueTimer? _timer;
    private DevicePreset _preset;
    private string _presetKey = DefaultKey;
    private bool _syncing;
    private bool _dirtyPrefs;
    private bool _dirtyPreset;
    private ViewerModel? _viewer;
    private CaptureController? _capture;

    public SettingsModel(PrefsStore store)
    {
        _store = store;
        _preset = store.Presets.Get(DefaultKey);
        Profiles = [.. store.Presets.LoadProfiles()];
        if (DispatcherQueue.GetForCurrentThread() is { } queue)
        {
            _timer = queue.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(400);
            _timer.IsRepeating = false;
            _timer.Tick += (_, _) => Flush();
        }
    }

    /// <summary>Raised on the UI thread after any setting changed, with what changed.</summary>
    public event Action<SettingsChange>? Changed;

    public ViewerPrefs Prefs => _store.Prefs;

    public DevicePreset Preset => _preset;

    public string PresetKey => _presetKey;

    /// <summary>The card the preset belongs to; null before one is seen.</summary>
    public CaptureDevice? Device { get; private set; }

    public List<Profile> Profiles { get; }

    // MARK: - Integration API

    /// <summary>Scale, filter, colour range and matrix, plus the picture adjustments, onto the renderer, then a redraw.</summary>
    public void ApplyTo(D3DRenderer renderer)
    {
        renderer.Options = _preset.RenderOptions;
        var a = _preset.Adjustments;
        renderer.Adjustments = ((float)a.Brightness, (float)a.Contrast, (float)a.Saturation, (float)a.Gamma, (float)a.Sharpness);
        ApplyLut(renderer);
        renderer.Redraw();
    }

    /// <summary>A .cube file that can't be read or parsed (message for the toast). UI thread.</summary>
    public event Action<string>? LutFailed;

    public string? LutName => _preset.LutName;

    /// <summary>LUT strength 0…1.</summary>
    public double LutIntensity
    {
        get => _preset.LutIntensity;
        set => Set(_preset with { LutIntensity = Math.Clamp(value, 0, 1) }, SettingsChange.Adjustments);
    }

    /// <summary>Uses <paramref name="path"/> as the current card's LUT (null removes it); remembered in its preset.</summary>
    public void SetLut(string? path) =>
        Set(_preset with { LutPath = path, LutName = path is null ? null : Path.GetFileNameWithoutExtension(path) }, SettingsChange.Adjustments);

    private (string? Path, CubeLUT? Lut) _lutCache;

    // Parses each file once; a file that fails stays failed until another path is chosen.
    private void ApplyLut(D3DRenderer renderer)
    {
        if (_preset.LutPath is not { } path)
        {
            renderer.ClearLut();
            return;
        }
        if (_lutCache.Path != path)
        {
            try
            {
                _lutCache = (path, CubeLUT.Parse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or CubeLUT.ParseException)
            {
                _lutCache = (path, null);
                LutFailed?.Invoke(Strings.Format("Toast_LutFailed", e.Message));
            }
        }
        if (_lutCache.Lut is { } lut)
        {
            renderer.SetLut(lut.Size, lut.Entries, new System.Numerics.Vector3(lut.DomainMin[0], lut.DomainMin[1], lut.DomainMin[2]),
                new System.Numerics.Vector3(lut.DomainMax[0], lut.DomainMax[1], lut.DomainMax[2]), (float)_preset.LutIntensity);
        }
        else
        {
            renderer.ClearLut();
        }
    }

    public AudioSettings AudioSettings => _preset.AudioSettings;

    public FormatPreference FormatPreference => _preset.Format;

    /// <summary>
    /// Keeps the two-way link between the settings and the main window's toggles (scale, sharp pixels,
    /// mute, volume, diagnostics, on top, clean mode). Settings win at attach time.
    /// </summary>
    public void Attach(ViewerModel viewer)
    {
        _viewer = viewer;
        PushToViewer();
        viewer.PropertyChanged += OnViewerChanged;
    }

    /// <summary>
    /// Gives the controller the format preference now and on every change (a change reopens the card with
    /// <see cref="CaptureController.Retry"/>; nothing else restarts capture).
    /// </summary>
    public void Attach(CaptureController capture)
    {
        _capture = capture;
        capture.Preference = _preset.Format;
    }

    /// <summary>
    /// A card went live (or went away: null). Loads that card's preset, or a copy of the default one with the
    /// format on Automatic for a card we haven't seen, and re-applies everything; the format change reopens the card.
    /// </summary>
    public void OnDeviceChanged(CaptureDevice? device)
    {
        if (device?.Key == Device?.Key && device is not null) { Device = device; return; }
        Flush();
        Device = device;
        if (device is null)
        {
            OnPropertyChanged(nameof(Device));
            return;
        }
        var key = device.Key;
        var store = _store.Presets;
        var next = store.Has(key) ? store.Get(key) : store.Get(DefaultKey) with { Format = new FormatPreference.Auto() };
        var formatChanged = next.Format != _preset.Format || _capture?.Preference != next.Format;
        _presetKey = key;
        _preset = next;
        OnPropertyChanged(string.Empty);
        PushToViewer();
        if (_capture is { } capture)
        {
            capture.Preference = next.Format;
            if (formatChanged && capture.CurrentType is not null) capture.Retry();
        }
        Changed?.Invoke(SettingsChange.Device);
        _dirtyPreset = !store.Has(key);
    }

    /// <summary>Writes anything pending now (call when the window closes).</summary>
    public void Flush()
    {
        _timer?.Stop();
        if (_dirtyPrefs)
        {
            _dirtyPrefs = false;
            _store.Update(_ => { });
        }
        if (_dirtyPreset)
        {
            _dirtyPreset = false;
            try { _store.Presets.Set(_presetKey, _preset); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AppLog.Write($"couldn't save presets: {e.Message}"); }
        }
    }

    // MARK: - Window prefs

    public bool ShowControlsOnHover
    {
        get => Prefs.ShowControlsOnHover;
        set { if (Prefs.ShowControlsOnHover != value) { Prefs.ShowControlsOnHover = value; Prefs_Changed(); } }
    }

    public bool FloatOnTop
    {
        get => Prefs.FloatOnTop;
        set { if (Prefs.FloatOnTop != value) { Prefs.FloatOnTop = value; Prefs_Changed(); } }
    }

    public bool CleanMode
    {
        get => Prefs.CleanMode;
        set { if (Prefs.CleanMode != value) { Prefs.CleanMode = value; Prefs_Changed(); } }
    }

    public bool LockAspect
    {
        get => Prefs.LockAspect;
        set { if (Prefs.LockAspect != value) { Prefs.LockAspect = value; Prefs_Changed(); } }
    }

    public bool ShowDiagnostics
    {
        get => Prefs.ShowStats;
        set { if (Prefs.ShowStats != value) { Prefs.ShowStats = value; Prefs_Changed(); } }
    }

    // MARK: - Preset (current card)

    public FormatPreference Format
    {
        get => _preset.Format;
        set
        {
            if (_preset.Format == value) return;
            Set(_preset with { Format = value }, SettingsChange.Format);
            if (_capture is { } capture)
            {
                capture.Preference = value;
                capture.Retry();
            }
        }
    }

    public ScaleMode Scale
    {
        get => _preset.Scale;
        set => Set(_preset with { Scale = value }, SettingsChange.Render);
    }

    public bool SharpPixels
    {
        get => _preset.Filter == FilterMode.Nearest;
        set => Set(_preset with { Filter = value ? FilterMode.Nearest : FilterMode.Linear }, SettingsChange.Render);
    }

    public ColorRange ColorRange
    {
        get => _preset.ColorRange;
        set => Set(_preset with { ColorRange = value }, SettingsChange.Render);
    }

    public ColorMatrix Matrix
    {
        get => _preset.Matrix;
        set => Set(_preset with { Matrix = value }, SettingsChange.Render);
    }

    public int AudioDelayMs
    {
        get => _preset.AudioDelayMs;
        set => Set(_preset with { AudioDelayMs = Math.Clamp(value, 0, 500) }, SettingsChange.Audio);
    }

    public ChannelMode ChannelMode
    {
        get => _preset.ChannelMode;
        set => Set(_preset with { ChannelMode = value }, SettingsChange.Audio);
    }

    /// <summary>0 to 100, like the control bar slider.</summary>
    public double Volume
    {
        get => Math.Round(_preset.Volume * 100);
        set => Set(_preset with { Volume = (float)(Math.Clamp(value, 0, 100) / 100) }, SettingsChange.Audio);
    }

    public bool Muted
    {
        get => _preset.Muted;
        set => Set(_preset with { Muted = value }, SettingsChange.Audio);
    }

    public double Brightness
    {
        get => _preset.Adjustments.Brightness;
        set => Adjust(a => a with { Brightness = value });
    }

    public double Contrast
    {
        get => _preset.Adjustments.Contrast;
        set => Adjust(a => a with { Contrast = value });
    }

    public double Saturation
    {
        get => _preset.Adjustments.Saturation;
        set => Adjust(a => a with { Saturation = value });
    }

    public double Gamma
    {
        get => _preset.Adjustments.Gamma;
        set => Adjust(a => a with { Gamma = value });
    }

    public double Sharpness
    {
        get => _preset.Adjustments.Sharpness;
        set => Adjust(a => a with { Sharpness = value });
    }

    public bool AdjustmentsAreIdentity => _preset.Adjustments.IsIdentity;

    public void ResetAdjustments() => Adjust(_ => new ImageAdjustments());

    /// <summary>Applies a look preset (Standard, Game, Cinema, Vivid).</summary>
    public void ApplyLook(LookPreset look) => Adjust(_ => look.Values with { });

    // MARK: - Profiles

    /// <summary>Copies a profile's settings onto the current card's preset (volume and mute stay as they are).</summary>
    public void ApplyProfile(Profile profile)
    {
        var next = profile.Settings.ApplyTo(_preset);
        var formatChanged = next.Format != _preset.Format;
        Set(next, SettingsChange.Render | SettingsChange.Adjustments | SettingsChange.Audio | SettingsChange.Format);
        if (formatChanged && _capture is { } capture)
        {
            capture.Preference = next.Format;
            capture.Retry();
        }
    }

    public Profile SaveCurrentAsProfile(string name)
    {
        var profile = new Profile { Id = Guid.NewGuid().ToString().ToUpperInvariant(), Name = name.Trim(), Settings = new ProfileSettings(_preset) };
        Profiles.Add(profile);
        SaveProfiles();
        return profile;
    }

    public void DeleteProfile(Profile profile)
    {
        if (profile.IsBuiltIn || !Profiles.Remove(profile)) return;
        SaveProfiles();
    }

    private void SaveProfiles()
    {
        try { _store.Presets.SaveProfiles(Profiles); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AppLog.Write($"couldn't save profiles: {e.Message}"); }
    }

    // MARK: - Plumbing

    private void Adjust(Func<ImageAdjustments, ImageAdjustments> change) =>
        Set(_preset with { Adjustments = change(_preset.Adjustments) }, SettingsChange.Adjustments);

    private void Set(DevicePreset next, SettingsChange kind)
    {
        if (next == _preset) return;
        _preset = next;
        // Cheap and simple: every preset-backed property re-reads.
        OnPropertyChanged(string.Empty);
        _dirtyPreset = true;
        PushToViewer();
        Commit(kind);
    }

    private void Prefs_Changed()
    {
        _dirtyPrefs = true;
        OnPropertyChanged(string.Empty);
        PushToViewer();
        Commit(SettingsChange.Prefs);
    }

    private void Commit(SettingsChange kind)
    {
        if (_timer is null) Flush();
        else { _timer.Stop(); _timer.Start(); }
        Changed?.Invoke(kind);
    }

    private void PushToViewer()
    {
        if (_viewer is not { } v || _syncing) return;
        _syncing = true;
        try
        {
            v.Scale = Scale;
            v.SharpPixels = SharpPixels;
            v.Muted = Muted;
            v.Volume = Volume;
            v.ShowDiagnostics = ShowDiagnostics;
            v.AlwaysOnTop = FloatOnTop;
            v.CleanMode = CleanMode;
        }
        finally { _syncing = false; }
    }

    // The control bar / shortcuts / context menu change the viewer; mirror that into the settings.
    private void OnViewerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncing || _viewer is not { } v) return;
        _syncing = true;
        try
        {
            switch (e.PropertyName)
            {
                case nameof(ViewerModel.Scale): Scale = v.Scale; break;
                case nameof(ViewerModel.SharpPixels): SharpPixels = v.SharpPixels; break;
                case nameof(ViewerModel.Muted): Muted = v.Muted; break;
                case nameof(ViewerModel.Volume): Volume = v.Volume; break;
                case nameof(ViewerModel.ShowDiagnostics): ShowDiagnostics = v.ShowDiagnostics; break;
                case nameof(ViewerModel.AlwaysOnTop): FloatOnTop = v.AlwaysOnTop; break;
                case nameof(ViewerModel.CleanMode): CleanMode = v.CleanMode; break;
            }
        }
        finally { _syncing = false; }
    }
}
