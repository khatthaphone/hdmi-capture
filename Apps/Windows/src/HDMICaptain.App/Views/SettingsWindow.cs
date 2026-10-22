using System.ComponentModel;
using System.Reflection;
using HDMICaptain.App.ViewModels;
using HDMICaptain.Core;
using HDMICaptain.Media.Audio;
using HDMICaptain.Media.Capture;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Text;
using Windows.Graphics;

namespace HDMICaptain.App.Views;

/// <summary>
/// The settings window (spec §13.7, Mac SettingsView): NavigationView with General, Video, Audio, Image, Profiles
/// and About. Built in code so there are no resource keys to get wrong; every control has an automation name and
/// a tab stop. One instance: <see cref="Show"/> opens or activates it. Everything binds to <see cref="SettingsModel"/>.
/// </summary>
public sealed class SettingsWindow : Window
{
    private static SettingsWindow? _instance;

    // Native types per card key: opening a card to list them is slow, so do it once per session.
    private static readonly Dictionary<string, IReadOnlyList<FormatPreference.Pinned>> FormatCache = [];

    private readonly SettingsModel _model;
    private readonly CaptureController? _capture;
    private readonly NavigationView _nav = new();
    private readonly List<Action> _refresh = [];
    private bool _refreshing;
    private ComboBox? _formatBox;
    private TextBlock? _formatNote;
    private List<FormatPreference> _formatItems = [];
    private StackPanel? _profileList;

    /// <summary>Opens the settings window, or brings the open one to the front.</summary>
    public static void Show(SettingsModel model, CaptureController? capture)
    {
        if (_instance is null) _instance = new SettingsWindow(model, capture);
        _instance.Activate();
    }

    /// <summary>For QA: closes the window if it is open.</summary>
    public static void CloseIfOpen() => _instance?.Close();

    private SettingsWindow(SettingsModel model, CaptureController? capture)
    {
        _model = model;
        _capture = capture;
        Title = "HDMI Captain Settings";
        AppWindow.Resize(new SizeInt32(900, 680));

        _nav.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
        _nav.IsSettingsVisible = false;
        _nav.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
        _nav.OpenPaneLength = 200;
        AutomationProperties.SetName(_nav, "Settings sections");
        AddSection("General", "", General);
        AddSection("Video", "", Video);
        AddSection("Audio", "", Audio);
        AddSection("Image", "", Image);
        AddSection("Profiles", "", Profiles);
        AddSection("About", "", About);
        _nav.SelectionChanged += (_, e) =>
        {
            if (e.SelectedItemContainer?.Tag is Func<UIElement> build) ShowPage(build);
        };
        Content = _nav;
        _nav.SelectedItem = _nav.MenuItems[0];

        _model.PropertyChanged += OnModelChanged;
        Closed += (_, _) =>
        {
            _model.PropertyChanged -= OnModelChanged;
            _model.Flush();
            _instance = null;
        };
    }

    private void AddSection(string title, string glyph, Func<UIElement> build) =>
        _nav.MenuItems.Add(new NavigationViewItem { Content = title, Icon = new FontIcon { Glyph = glyph }, Tag = build });

    private void ShowPage(Func<UIElement> build)
    {
        _refresh.Clear();
        _formatBox = null;
        _profileList = null;
        var panel = (StackPanel)build();
        panel.Margin = new Thickness(32, 24, 32, 32);
        panel.MaxWidth = 640;
        panel.HorizontalAlignment = HorizontalAlignment.Left;
        _nav.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Refresh();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        Refresh();
        if (e.PropertyName is nameof(SettingsModel.Device) or "") LoadFormats();
    }

    private void Refresh()
    {
        _refreshing = true;
        try { foreach (var action in _refresh) action(); }
        finally { _refreshing = false; }
    }

    // MARK: - Pages

    private UIElement General()
    {
        var page = Page("General");
        page.Children.Add(Toggle("Show controls near the bottom edge", "The control bar appears when the pointer nears the bottom of the window.",
            () => _model.ShowControlsOnHover, v => _model.ShowControlsOnHover = v));
        page.Children.Add(Toggle("Keep window on top", null, () => _model.FloatOnTop, v => _model.FloatOnTop = v));
        page.Children.Add(Toggle("Hide window buttons (clean mode)", "Removes the title bar and borders for a clean picture.",
            () => _model.CleanMode, v => _model.CleanMode = v));
        page.Children.Add(Toggle("Keep the window's aspect ratio", "Resizing keeps the picture's shape, so there are no black bars.",
            () => _model.LockAspect, v => _model.LockAspect = v));
        page.Children.Add(Toggle("Show diagnostics", "Frame rate, dropped frames and the capture format over the picture.",
            () => _model.ShowDiagnostics, v => _model.ShowDiagnostics = v));
        return page;
    }

    private UIElement Video()
    {
        var page = Page("Video");

        var cards = new ComboBox { Header = "Capture card", MinWidth = 320 };
        AutomationProperties.SetName(cards, "Capture card");
        var cardKeys = new List<string>();
        cards.SelectionChanged += (_, _) =>
        {
            if (_refreshing || cards.SelectedIndex < 0 || cards.SelectedIndex >= cardKeys.Count) return;
            if (cardKeys[cards.SelectedIndex] != _capture?.CurrentDevice?.Key) _capture?.Select(cardKeys[cards.SelectedIndex]);
        };
        _refresh.Add(() =>
        {
            var devices = _capture?.Catalog.Devices ?? [];
            cardKeys = [.. devices.Select(d => d.Key)];
            cards.Items.Clear();
            foreach (var d in devices) cards.Items.Add(d.Name);
            cards.IsEnabled = devices.Count > 0;
            cards.PlaceholderText = devices.Count == 0 ? "No capture card connected" : "Choose a capture card";
            var current = _capture?.CurrentDevice?.Key;
            cards.SelectedIndex = current is null ? -1 : cardKeys.IndexOf(current);
        });
        page.Children.Add(cards);
        page.Children.Add(Note("These settings are remembered for each capture card."));

        _formatBox = new ComboBox { Header = "Format", MinWidth = 320 };
        AutomationProperties.SetName(_formatBox, "Format");
        _formatBox.SelectionChanged += (_, _) =>
        {
            if (_refreshing || _formatBox.SelectedIndex < 0 || _formatBox.SelectedIndex >= _formatItems.Count) return;
            _model.Format = _formatItems[_formatBox.SelectedIndex];
        };
        _formatNote = Note("Changing the format reconnects the capture card for a moment.");
        page.Children.Add(_formatBox);
        page.Children.Add(_formatNote);
        _refresh.Add(SelectFormat);
        LoadFormats();

        page.Children.Add(Combo("Scale", [.. Enum.GetValues<ScaleMode>().Select(m => (ViewerModel.ScaleTitle(m), m))],
            () => _model.Scale, v => _model.Scale = v));
        page.Children.Add(Toggle("Pixel-sharp", "Keeps hard edges when scaling up. Best for retro consoles.",
            () => _model.SharpPixels, v => _model.SharpPixels = v));
        page.Children.Add(Combo("Color range", [("Automatic", ColorRange.Auto), ("Limited (16 to 235)", ColorRange.Limited), ("Full (0 to 255)", ColorRange.Full)],
            () => _model.ColorRange, v => _model.ColorRange = v));
        page.Children.Add(Note("Pick Limited or Full if blacks look gray or crushed."));
        page.Children.Add(Combo("Color matrix", [("Automatic", ColorMatrix.Auto), ("BT.601 (standard definition)", ColorMatrix.Bt601), ("BT.709 (high definition)", ColorMatrix.Bt709)],
            () => _model.Matrix, v => _model.Matrix = v));
        page.Children.Add(Note("Pick another matrix if colors look off, for example skin looks too green or too red."));
        return page;
    }

    private UIElement Audio()
    {
        var page = Page("Audio");
        var input = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var output = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _refresh.Add(() => input.Text = _capture?.CurrentDevice is { } d
            ? $"Sound comes from {d.Audio?.Name ?? "the capture card"}."
            : "Sound comes from the capture card once one is connected.");
        _refresh.Add(() =>
        {
            string? name = null;
            try { name = AudioEndpoints.DefaultRender()?.Name; }
            catch (Exception) { /* no audio service: say so below */ }
            output.Text = name is null
                ? "No sound output found."
                : $"Plays through {name}. Change the output in Windows sound settings.";
        });
        page.Children.Add(input);
        page.Children.Add(output);
        page.Children.Add(Slider("Audio delay", 0, 500, 10, () => _model.AudioDelayMs, v => _model.AudioDelayMs = (int)v, v => $"{v:0} ms"));
        page.Children.Add(Note("Delays the sound to line it up with a slower display."));
        page.Children.Add(Combo("Channels", [
            ("Automatic", ChannelMode.Auto), ("Stereo", ChannelMode.Normal), ("Swap left and right", ChannelMode.Swap),
            ("Left to both", ChannelMode.LeftToBoth), ("Right to both", ChannelMode.RightToBoth), ("Mono", ChannelMode.Mono)],
            () => _model.ChannelMode, v => _model.ChannelMode = v));
        page.Children.Add(Slider("Volume", 0, 100, 1, () => _model.Volume, v => _model.Volume = v, v => $"{v:0}%"));
        page.Children.Add(Toggle("Mute", null, () => _model.Muted, v => _model.Muted = v));
        return page;
    }

    private UIElement Image()
    {
        var page = Page("Image");
        var looks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var look in LookPreset.All)
        {
            var button = new Button { Content = look.Title };
            AutomationProperties.SetName(button, $"{look.Title} look");
            button.Click += (_, _) => _model.ApplyLook(look);
            looks.Children.Add(button);
        }
        page.Children.Add(new TextBlock { Text = "Look" });
        page.Children.Add(looks);
        page.Children.Add(Slider("Brightness", -0.5, 0.5, 0.01, () => _model.Brightness, v => _model.Brightness = v, v => $"{v:0.00}"));
        page.Children.Add(Slider("Contrast", 0.5, 1.5, 0.01, () => _model.Contrast, v => _model.Contrast = v, v => $"{v:0.00}"));
        page.Children.Add(Slider("Saturation", 0, 2, 0.01, () => _model.Saturation, v => _model.Saturation = v, v => $"{v:0.00}"));
        page.Children.Add(Slider("Gamma", 0.5, 2, 0.01, () => _model.Gamma, v => _model.Gamma = v, v => $"{v:0.00}"));
        page.Children.Add(Slider("Sharpness", 0, 1, 0.01, () => _model.Sharpness, v => _model.Sharpness = v, v => $"{v:0.00}"));
        var reset = new Button { Content = "Reset" };
        AutomationProperties.SetName(reset, "Reset picture adjustments");
        reset.Click += (_, _) => _model.ResetAdjustments();
        _refresh.Add(() => reset.IsEnabled = !_model.AdjustmentsAreIdentity);
        page.Children.Add(reset);
        return page;
    }

    private UIElement Profiles()
    {
        var page = Page("Profiles");
        page.Children.Add(Note("A profile bundles the format, scale, filter, color, image and delay settings, for example for an old console. Applying one changes the current capture card's settings."));
        _profileList = new StackPanel { Spacing = 4 };
        page.Children.Add(_profileList);
        RebuildProfiles();
        var save = new Button { Content = "Save Current Settings as Profile…" };
        save.Click += async (_, _) => await SaveProfileAsync(save.XamlRoot);
        page.Children.Add(save);
        return page;
    }

    private void RebuildProfiles()
    {
        if (_profileList is not { } list) return;
        list.Children.Clear();
        foreach (var profile in _model.Profiles.ToList())
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = profile.IsBuiltIn ? $"{profile.Name} (built in)" : profile.Name, VerticalAlignment = VerticalAlignment.Center });
            var apply = new Button { Content = "Apply" };
            AutomationProperties.SetName(apply, $"Apply {profile.Name}");
            apply.Click += (_, _) => _model.ApplyProfile(profile);
            Grid.SetColumn(apply, 1);
            row.Children.Add(apply);
            if (!profile.IsBuiltIn)
            {
                var delete = new Button { Content = "Delete" };
                AutomationProperties.SetName(delete, $"Delete {profile.Name}");
                delete.Click += (_, _) => { _model.DeleteProfile(profile); RebuildProfiles(); };
                Grid.SetColumn(delete, 2);
                row.Children.Add(delete);
            }
            list.Children.Add(row);
        }
    }

    private async Task SaveProfileAsync(XamlRoot root)
    {
        var name = new TextBox { PlaceholderText = "Profile name" };
        AutomationProperties.SetName(name, "Profile name");
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Save Profile",
            Content = name,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(name.Text);
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _model.SaveCurrentAsProfile(name.Text);
            RebuildProfiles();
        }
    }

    private UIElement About()
    {
        var page = Page("About");
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        page.Children.Add(new TextBlock { Text = "HDMI Captain", FontSize = 20, FontWeight = FontWeights.SemiBold });
        page.Children.Add(new TextBlock { Text = $"Version {version?.ToString(3) ?? "1.0.0"}" });
        page.Children.Add(Note("Shows the picture and plays the sound from your HDMI capture card. Nothing is recorded unless you record."));
        page.Children.Add(new TextBlock { Text = "Third-party licenses", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0) });
        var licences = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            Height = 320,
            Text = ReadLicences(),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(licences, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(licences, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(licences, "Third-party licenses");
        page.Children.Add(licences);
        return page;
    }

    private static string ReadLicences()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY.md");
            return File.Exists(path) ? File.ReadAllText(path) : "The license list isn't included in this build.";
        }
        catch (IOException) { return "Couldn't read the license list."; }
    }

    // MARK: - Format list

    private void LoadFormats()
    {
        if (_formatBox is null) return;
        var device = _capture?.CurrentDevice ?? _model.Device;
        if (device is null)
        {
            SetFormatItems([]);
            return;
        }
        if (FormatCache.TryGetValue(device.Key, out var cached))
        {
            SetFormatItems(cached);
            return;
        }
        SetFormatItems([]); // "Automatic" only until the list arrives
        var queue = DispatcherQueue;
        var link = device.Usb?.Link;
        _ = Task.Run(() =>
        {
            IReadOnlyList<FormatPreference.Pinned> options;
            try { options = FormatSelector.Options(MfDevices.Descriptors(MfDevices.NativeTypes(device.SymbolicLink)), link); }
            catch (Exception e)
            {
                AppLog.Write($"[settings] couldn't list formats: {e.Message}");
                options = [];
            }
            if (options.Count > 0) FormatCache[device.Key] = options;
            queue.TryEnqueue(() =>
            {
                if (_instance == this && (_capture?.CurrentDevice ?? _model.Device)?.Key == device.Key) SetFormatItems(options);
            });
        });
    }

    private void SetFormatItems(IReadOnlyList<FormatPreference.Pinned> options)
    {
        List<FormatPreference> items = [new FormatPreference.Auto(), .. options];
        // Keep a pin that this card doesn't list now (or before the list loads) visible, so it isn't silently dropped.
        if (_model.Format is FormatPreference.Pinned pinned && !items.Contains(pinned)) items.Add(pinned);
        _formatItems = items;
        SelectFormat();
    }

    private void SelectFormat()
    {
        if (_formatBox is null) return;
        var was = _refreshing;
        _refreshing = true;
        try
        {
            _formatBox.Items.Clear();
            foreach (var item in _formatItems) _formatBox.Items.Add(FormatTitle(item));
            _formatBox.SelectedIndex = Math.Max(0, _formatItems.IndexOf(_model.Format));
        }
        finally { _refreshing = was; }
    }

    private static string FormatTitle(FormatPreference preference) => preference switch
    {
        FormatPreference.Pinned p when p.FrameRate is { } rate => $"{p.Width} × {p.Height} at {rate:0.##} fps",
        FormatPreference.Pinned p => $"{p.Width} × {p.Height}",
        _ => "Automatic",
    };

    // MARK: - Control builders

    private static StackPanel Page(string title)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 28, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        return panel;
    }

    private static TextBlock Note(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.7,
        FontSize = 12,
    };

    private UIElement Toggle(string header, string? note, Func<bool> get, Action<bool> set)
    {
        var toggle = new ToggleSwitch { Header = header, OnContent = "On", OffContent = "Off" };
        AutomationProperties.SetName(toggle, header);
        toggle.Toggled += (_, _) => { if (!_refreshing) set(toggle.IsOn); };
        _refresh.Add(() => toggle.IsOn = get());
        if (note is null) return toggle;
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(toggle);
        panel.Children.Add(Note(note));
        return panel;
    }

    private UIElement Combo<T>(string header, IReadOnlyList<(string Title, T Value)> items, Func<T> get, Action<T> set)
    {
        var box = new ComboBox { Header = header, MinWidth = 320 };
        AutomationProperties.SetName(box, header);
        foreach (var (title, _) in items) box.Items.Add(title);
        box.SelectionChanged += (_, _) =>
        {
            if (!_refreshing && box.SelectedIndex >= 0) set(items[box.SelectedIndex].Value);
        };
        _refresh.Add(() =>
        {
            var current = get();
            box.SelectedIndex = items.ToList().FindIndex(i => EqualityComparer<T>.Default.Equals(i.Value, current));
        });
        return box;
    }

    private UIElement Slider(string header, double min, double max, double step, Func<double> get, Action<double> set, Func<double, string> format)
    {
        var slider = new Slider { Minimum = min, Maximum = max, StepFrequency = step, SmallChange = step, LargeChange = step * 10, Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
        AutomationProperties.SetName(slider, header);
        slider.ValueChanged += (_, e) =>
        {
            label.Text = format(e.NewValue);
            if (!_refreshing) set(e.NewValue);
        };
        _refresh.Add(() =>
        {
            slider.Value = get();
            label.Text = format(slider.Value);
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(slider);
        row.Children.Add(label);
        var panel = new StackPanel { Spacing = 0 };
        panel.Children.Add(new TextBlock { Text = header });
        panel.Children.Add(row);
        return panel;
    }
}
