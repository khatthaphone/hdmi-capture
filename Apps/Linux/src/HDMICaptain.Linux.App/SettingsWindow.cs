using HDMICaptain.Core;
using HDMICaptain.Linux.Media;
using HDMICaptain.Linux.Media.Capture;

namespace HDMICaptain.Linux.App;

/// <summary>
/// Settings (spec §14.7, task 115): General, Video, Audio, Image, Profiles and About pages in an
/// <see cref="Adw.PreferencesWindow"/>. Card settings edit the on-screen card's <see cref="DevicePreset"/> through
/// <see cref="ViewerSettings"/> and apply live; only a format change reopens the card. One instance.
/// </summary>
public sealed class SettingsWindow
{
    private static SettingsWindow? _instance;

    private readonly MainWindow _owner;
    private readonly ViewerSettings _settings;
    private readonly Adw.PreferencesWindow _window;
    private readonly List<Action> _refresh = [];
    private bool _refreshing;

    public static void Show(MainWindow owner)
    {
        _instance ??= new SettingsWindow(owner);
        _instance._window.Present();
    }

    private SettingsWindow(MainWindow owner)
    {
        _owner = owner;
        _settings = owner.Settings;
        _window = Adw.PreferencesWindow.New();
        _window.SetTitle("Settings");
        _window.SetTransientFor(owner.Window);
        _window.SetDefaultSize(640, 720);
        _window.SetSearchEnabled(true);
        _window.Add(General());
        _window.Add(Video());
        _window.Add(Audio());
        _window.Add(Image());
        _window.Add(Profiles());
        _window.Add(About());
        _settings.Changed += Refresh;
        _window.OnCloseRequest += (_, _) =>
        {
            _settings.Changed -= Refresh;
            _instance = null;
            return false;
        };
        Refresh();
    }

    private void Refresh()
    {
        _refreshing = true;
        foreach (var r in _refresh) r();
        _refreshing = false;
    }

    // MARK: - Pages

    private Adw.PreferencesPage General()
    {
        var page = Page("General", "preferences-system-symbolic");
        var window = Group(page, "Window");
        Switch(window, "Show controls on hover", "The control bar appears when the pointer moves over the picture.",
            () => _settings.Prefs.ShowControlsOnHover, v => _settings.EditPrefs(p => p.ShowControlsOnHover = v));
        Switch(window, "Show diagnostics", "Frame rate, dropped frames and the audio buffer over the picture.",
            () => _owner.Diagnostics, v => { if (v != _owner.Diagnostics) _owner.ToggleDiagnostics(); });
        Switch(window, "Clean mode", "Hide the header bar and window frame.",
            () => _owner.Clean, v => { if (v != _owner.Clean) _owner.ToggleClean(); });
        var onTop = Switch(window, "Keep window on top",
            X11Window.IsAvailable ? "Stays above other windows." : "On Wayland, use the window menu (Alt+Space) › Always on Top.",
            () => _settings.Prefs.FloatOnTop, v => _owner.SetKeepOnTop(v));
        onTop.SetSensitive(X11Window.IsAvailable);
        return page;
    }

    private Adw.PreferencesPage Video()
    {
        var page = Page("Video", "camera-video-symbolic");
        var input = Group(page, "Capture card");
        var cards = Gtk.StringList.New([]);
        var cardKeys = new List<string>();
        var cardRow = Adw.ComboRow.New();
        cardRow.SetTitle("Card");
        cardRow.SetModel(cards);
        input.Add(cardRow);
        var formatRow = Adw.ComboRow.New();
        formatRow.SetTitle("Format");
        var formats = Gtk.StringList.New([]);
        var formatItems = new List<FormatPreference>();
        formatRow.SetModel(formats);
        input.Add(formatRow);

        _refresh.Add(() =>
        {
            var devices = _owner.Capture?.Catalog.Devices ?? [];
            Replace(cards, devices.Count == 0 ? ["No capture card"] : devices.Select(d => d.Name + (d.Audio != null ? "" : " (no audio)")).ToArray());
            cardKeys.Clear();
            cardKeys.AddRange(devices.Select(d => d.Key));
            var current = _owner.Capture?.CurrentDevice?.Key;
            cardRow.SetSelected((uint)Math.Max(0, cardKeys.IndexOf(current ?? "")));
            cardRow.SetSensitive(devices.Count > 1);

            formatItems.Clear();
            formatItems.Add(new FormatPreference.Auto());
            var device = _owner.Capture?.CurrentDevice;
            if (device?.Node is { } node) formatItems.AddRange(FormatSelector.Options(node.Descriptors, node.Usb?.Link));
            var actual = _owner.Capture?.CurrentStream;
            Replace(formats, formatItems.Select(f => f switch
            {
                FormatPreference.Pinned p => $"{p.Width}×{p.Height}" + (p.FrameRate is { } r ? $" @ {r:0.##}" : ""),
                _ => actual is null ? "Automatic" : $"Automatic ({actual.Width}×{actual.Height} {actual.FourCC} @ {actual.FrameRate:0.##})",
            }).ToArray());
            var index = formatItems.FindIndex(f => f == _settings.Preset.Format);
            formatRow.SetSelected((uint)Math.Max(0, index));
            formatRow.SetSensitive(formatItems.Count > 1);
        });
        OnSelected(cardRow, i =>
        {
            if (i < cardKeys.Count && cardKeys[i] != _owner.Capture?.CurrentDevice?.Key) _owner.Capture?.Select(cardKeys[i]);
        });
        OnSelected(formatRow, i =>
        {
            if (i < formatItems.Count) _settings.Edit(p => p with { Format = formatItems[i] });
        });

        var picture = Group(page, "Picture");
        Combo(picture, "Scale", [Strings.ScaleFit, Strings.ScaleFill, Strings.ScaleStretch, Strings.ScaleInteger],
            () => (int)_settings.Preset.Scale, i => _settings.Edit(p => p with { Scale = (ScaleMode)i }));
        Switch(picture, "Sharp pixels", "Nearest-neighbour scaling for pixel art and retro consoles.",
            () => _settings.Preset.Filter == FilterMode.Nearest, v => _settings.Edit(p => p with { Filter = v ? FilterMode.Nearest : FilterMode.Linear }));
        Combo(picture, "Colour range", ["Automatic", "Limited (16–235)", "Full (0–255)"],
            () => (int)_settings.Preset.ColorRange, i => _settings.Edit(p => p with { ColorRange = (ColorRange)i }));
        Combo(picture, "Colour matrix", ["Automatic", "BT.601 (SD)", "BT.709 (HD)"],
            () => (int)_settings.Preset.Matrix, i => _settings.Edit(p => p with { Matrix = (ColorMatrix)i }));
        return page;
    }

    private Adw.PreferencesPage Audio()
    {
        var page = Page("Audio", "audio-speakers-symbolic");
        var group = Group(page, "Sound from the capture card");
        var source = Adw.ActionRow.New();
        source.SetTitle("Input");
        group.Add(source);
        _refresh.Add(() => source.SetSubtitle(_owner.Capture?.CurrentDevice?.Audio is { } a ? $"{a.Name} ({a.HwName})" : "None found for this card"));
        Slider(group, "Volume", 0, 1, 0.05, () => _settings.Preset.Volume, v => _settings.Edit(p => p with { Volume = (float)v }));
        Switch(group, "Mute", null, () => _settings.Preset.Muted, v => _settings.Edit(p => p with { Muted = v }));
        var delay = Adw.SpinRow.NewWithRange(0, 500, 10);
        delay.SetTitle("Delay");
        delay.SetSubtitle("Milliseconds; lines sound up with a slower display.");
        group.Add(delay);
        _refresh.Add(() => delay.SetValue(_settings.Preset.AudioDelayMs));
        delay.OnNotify += (_, args) =>
        {
            if (!_refreshing && args.Pspec.GetName() == "value") _settings.Edit(p => p with { AudioDelayMs = (int)delay.GetValue() });
        };
        Combo(group, "Channels", ["Automatic", "Normal", "Swap left and right", "Left to both", "Right to both", "Mono"],
            () => (int)_settings.Preset.ChannelMode, i => _settings.Edit(p => p with { ChannelMode = (ChannelMode)i }));
        return page;
    }

    private Adw.PreferencesPage Image()
    {
        var page = Page("Image", "applications-graphics-symbolic");
        var look = Group(page, "Picture look");
        var presets = LookPreset.All.Select(p => p.Title).Append("Custom").ToArray();
        Combo(look, "Preset", presets,
            () => LookPreset.Of(_settings.Preset.Adjustments) is { } p ? LookPreset.All.ToList().IndexOf(p) : presets.Length - 1,
            i => { if (i < LookPreset.All.Count) _settings.Edit(p => p with { Adjustments = LookPreset.All[i].Values }); });
        Slider(look, "Brightness", -0.5, 0.5, 0.01, () => _settings.Preset.Adjustments.Brightness, v => EditLook(a => a with { Brightness = v }));
        Slider(look, "Contrast", 0.5, 2, 0.01, () => _settings.Preset.Adjustments.Contrast, v => EditLook(a => a with { Contrast = v }));
        Slider(look, "Saturation", 0, 2, 0.01, () => _settings.Preset.Adjustments.Saturation, v => EditLook(a => a with { Saturation = v }));
        Slider(look, "Gamma", 0.5, 2, 0.01, () => _settings.Preset.Adjustments.Gamma, v => EditLook(a => a with { Gamma = v }));
        Slider(look, "Sharpness", 0, 1, 0.01, () => _settings.Preset.Adjustments.Sharpness, v => EditLook(a => a with { Sharpness = v }));
        var reset = Gtk.Button.NewWithLabel("Reset");
        reset.SetValign(Gtk.Align.Center);
        reset.OnClicked += (_, _) => _settings.Edit(p => p with { Adjustments = new ImageAdjustments() });
        look.SetHeaderSuffix(reset);

        var lut = Group(page, "LUT (.cube)");
        var lutRow = Adw.ActionRow.New();
        lutRow.SetTitle("File");
        var choose = Gtk.Button.NewWithLabel("Choose…");
        choose.SetValign(Gtk.Align.Center);
        choose.OnClicked += async (_, _) => await ChooseLut();
        var remove = Gtk.Button.NewFromIconName("user-trash-symbolic");
        remove.SetValign(Gtk.Align.Center);
        remove.SetTooltipText("Remove LUT");
        A11y.SetLabel(remove, "Remove LUT");
        remove.OnClicked += (_, _) => _settings.Edit(p => p with { LutPath = null, LutName = null });
        lutRow.AddSuffix(choose);
        lutRow.AddSuffix(remove);
        lut.Add(lutRow);
        _refresh.Add(() =>
        {
            var preset = _settings.Preset;
            lutRow.SetSubtitle(preset.LutPath == null ? "None" : _settings.Lut == null ? $"{preset.LutName ?? preset.LutPath} (can't be read)" : preset.LutName ?? Path.GetFileName(preset.LutPath));
            remove.SetSensitive(preset.LutPath != null);
        });
        Slider(lut, "Strength", 0, 1, 0.01, () => _settings.Preset.LutIntensity, v => _settings.Edit(p => p with { LutIntensity = v }));
        return page;
    }

    private void EditLook(Func<ImageAdjustments, ImageAdjustments> change) => _settings.Edit(p => p with { Adjustments = change(p.Adjustments) });

    private async Task ChooseLut()
    {
        var dialog = Gtk.FileDialog.New();
        dialog.SetTitle("Choose a .cube LUT");
        var filter = Gtk.FileFilter.New();
        filter.SetName("Cube LUT (.cube)");
        filter.AddPattern("*.cube");
        filter.AddPattern("*.CUBE");
        dialog.SetDefaultFilter(filter);
        try
        {
            var file = await dialog.OpenAsync(_window);
            if (file?.GetPath() is not { } path) return;
            var parsed = CubeLUT.Parse(await File.ReadAllTextAsync(path), Path.GetFileNameWithoutExtension(path));
            _settings.Edit(p => p with { LutPath = path, LutName = parsed.Title });
        }
        catch (CubeLUT.ParseException e)
        {
            _owner.Toast($"Can't use this LUT: {e.Message}");
        }
        catch (Exception e) when (e is GLib.GException or IOException)
        {
            LinuxLog.Write($"lut dialog: {e.Message}"); // dismissed or unreadable
        }
    }

    private Adw.PreferencesPage Profiles()
    {
        var page = Page("Profiles", "view-list-symbolic");
        var group = Group(page, "Console profiles");
        group.SetDescription("A profile sets the format, picture, look and sound for a kind of source. Applying one changes the settings of the card on screen.");
        foreach (var profile in _settings.Store.Presets.LoadProfiles())
        {
            var row = Adw.ActionRow.New();
            row.SetTitle(profile.Name);
            row.SetSubtitle(Describe(profile.Settings));
            var apply = Gtk.Button.NewWithLabel("Apply");
            apply.SetValign(Gtk.Align.Center);
            apply.OnClicked += (_, _) =>
            {
                _settings.ApplyProfile(profile);
                _owner.Toast($"Applied {profile.Name}");
            };
            row.AddSuffix(apply);
            group.Add(row);
        }
        return page;
    }

    private static string Describe(ProfileSettings s)
    {
        var parts = new List<string>
        {
            s.Format is FormatPreference.Pinned p ? $"{p.Width}×{p.Height}" + (p.FrameRate is { } r ? $"@{r:0}" : "") : "Auto format",
            s.Scale.ToString(),
        };
        if (s.Filter == FilterMode.Nearest) parts.Add("sharp pixels");
        if (LookPreset.Of(s.Adjustments) is { } look && look != LookPreset.Standard) parts.Add(look.Title);
        if (s.AudioDelayMs > 0) parts.Add($"{s.AudioDelayMs} ms delay");
        return string.Join(" · ", parts);
    }

    private Adw.PreferencesPage About()
    {
        var page = Page("About", "help-about-symbolic");
        var group = Group(page, "HDMI Captain");
        Info(group, "Version", typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "dev");
        Info(group, "Graphics", "OpenGL ES 3 (shared renderer)");
        Info(group, "Settings folder", _settings.Store.Folder);
        Info(group, "Log", LinuxLog.FilePath);
        var licences = Group(page, "Open-source components");
        Info(licences, "GTK 4, libadwaita", "LGPL-2.1-or-later");
        Info(licences, "Gir.Core", "MIT");
        Info(licences, "libjpeg-turbo", "IJG, BSD-3-Clause, zlib");
        Info(licences, ".NET", "MIT");
        return page;
    }

    // MARK: - Row helpers (each registers how it refreshes from the model)

    private static Adw.PreferencesPage Page(string title, string icon)
    {
        var page = Adw.PreferencesPage.New();
        page.SetTitle(title);
        page.SetIconName(icon);
        return page;
    }

    private static Adw.PreferencesGroup Group(Adw.PreferencesPage page, string title)
    {
        var group = Adw.PreferencesGroup.New();
        group.SetTitle(title);
        page.Add(group);
        return group;
    }

    private static void Info(Adw.PreferencesGroup group, string title, string value)
    {
        var row = Adw.ActionRow.New();
        row.SetTitle(title);
        row.SetSubtitle(value);
        row.SetSubtitleSelectable(true);
        group.Add(row);
    }

    private Adw.SwitchRow Switch(Adw.PreferencesGroup group, string title, string? subtitle, Func<bool> get, Action<bool> set)
    {
        var row = Adw.SwitchRow.New();
        row.SetTitle(title);
        if (subtitle != null) row.SetSubtitle(subtitle);
        group.Add(row);
        _refresh.Add(() => row.SetActive(get()));
        row.OnNotify += (_, args) =>
        {
            if (!_refreshing && args.Pspec.GetName() == "active") set(row.GetActive());
        };
        return row;
    }

    private void Combo(Adw.PreferencesGroup group, string title, string[] items, Func<int> get, Action<int> set)
    {
        var row = Adw.ComboRow.New();
        row.SetTitle(title);
        row.SetModel(Gtk.StringList.New(items));
        group.Add(row);
        _refresh.Add(() => row.SetSelected((uint)Math.Clamp(get(), 0, items.Length - 1)));
        OnSelected(row, set);
    }

    private void OnSelected(Adw.ComboRow row, Action<int> set) => row.OnNotify += (_, args) =>
    {
        if (!_refreshing && args.Pspec.GetName() == "selected") set((int)row.GetSelected());
    };

    private void Slider(Adw.PreferencesGroup group, string title, double min, double max, double step, Func<double> get, Action<double> set)
    {
        var row = Adw.ActionRow.New();
        row.SetTitle(title);
        var scale = Gtk.Scale.NewWithRange(Gtk.Orientation.Horizontal, min, max, step);
        scale.SetHexpand(true);
        scale.SetSizeRequest(220, -1);
        scale.SetDrawValue(true);
        scale.SetValuePos(Gtk.PositionType.Right);
        scale.SetDigits(2);
        A11y.SetLabel(scale, title);
        row.AddSuffix(scale);
        group.Add(row);
        _refresh.Add(() => scale.SetValue(get()));
        scale.OnValueChanged += (_, _) =>
        {
            if (!_refreshing) set(scale.GetValue());
        };
    }

    private static void Replace(Gtk.StringList list, string[] items) => list.Splice(0, list.GetNItems(), items);
}
