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
        Title = Strings.Get("Settings_WindowTitle");
        AppWindow.TitleBar.PreferredTheme = Microsoft.UI.Windowing.TitleBarTheme.Dark;
        AppWindow.Resize(new SizeInt32(900, 680));

        _nav.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
        _nav.IsSettingsVisible = false;
        _nav.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
        _nav.OpenPaneLength = 200;
        AutomationProperties.SetName(_nav, Strings.Get("Settings_SectionsName"));
        AddSection(Strings.Get("Settings_General"), "", General);
        AddSection(Strings.Get("Settings_Video"), "", Video);
        AddSection(Strings.Get("Settings_Audio"), "", Audio);
        AddSection(Strings.Get("Settings_Image"), "", Image);
        AddSection(Strings.Get("Settings_Profiles"), "", Profiles);
        AddSection(Strings.Get("Settings_About"), "", About);
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
        var page = Page(Strings.Get("Settings_General"));
        page.Children.Add(Toggle(Strings.Get("Settings_ShowControls"), Strings.Get("Settings_ShowControlsNote"),
            () => _model.ShowControlsOnHover, v => _model.ShowControlsOnHover = v));
        page.Children.Add(Toggle(Strings.Get("Settings_KeepOnTop"), null, () => _model.FloatOnTop, v => _model.FloatOnTop = v));
        page.Children.Add(Toggle(Strings.Get("Settings_CleanMode"), Strings.Get("Settings_CleanModeNote"),
            () => _model.CleanMode, v => _model.CleanMode = v));
        page.Children.Add(Toggle(Strings.Get("Settings_LockAspect"), Strings.Get("Settings_LockAspectNote"),
            () => _model.LockAspect, v => _model.LockAspect = v));
        page.Children.Add(Toggle(Strings.Get("Settings_ShowDiagnostics"), Strings.Get("Settings_ShowDiagnosticsNote"),
            () => _model.ShowDiagnostics, v => _model.ShowDiagnostics = v));
        return page;
    }

    private UIElement Video()
    {
        var page = Page(Strings.Get("Settings_Video"));

        var cards = new ComboBox { Header = Strings.Get("Settings_CaptureCard"), MinWidth = 320 };
        AutomationProperties.SetName(cards, Strings.Get("Settings_CaptureCard"));
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
            cards.PlaceholderText = devices.Count == 0 ? Strings.Get("Card_NoneConnected") : Strings.Get("Settings_ChooseCard");
            var current = _capture?.CurrentDevice?.Key;
            cards.SelectedIndex = current is null ? -1 : cardKeys.IndexOf(current);
        });
        page.Children.Add(cards);
        page.Children.Add(Note(Strings.Get("Settings_PerCardNote")));

        _formatBox = new ComboBox { Header = Strings.Get("Settings_Format"), MinWidth = 320 };
        AutomationProperties.SetName(_formatBox, Strings.Get("Settings_Format"));
        _formatBox.SelectionChanged += (_, _) =>
        {
            if (_refreshing || _formatBox.SelectedIndex < 0 || _formatBox.SelectedIndex >= _formatItems.Count) return;
            _model.Format = _formatItems[_formatBox.SelectedIndex];
        };
        _formatNote = Note(Strings.Get("Settings_FormatNote"));
        page.Children.Add(_formatBox);
        page.Children.Add(_formatNote);
        _refresh.Add(SelectFormat);
        LoadFormats();

        page.Children.Add(Combo(Strings.Get("Settings_Scale"), [.. Enum.GetValues<ScaleMode>().Select(m => (ViewerModel.ScaleTitle(m), m))],
            () => _model.Scale, v => _model.Scale = v));
        page.Children.Add(Toggle(Strings.Get("Settings_PixelSharp"), Strings.Get("Settings_PixelSharpNote"),
            () => _model.SharpPixels, v => _model.SharpPixels = v));
        page.Children.Add(Combo(Strings.Get("Settings_ColorRange"), [(Strings.Get("Common_Automatic"), ColorRange.Auto), (Strings.Get("Settings_RangeLimited"), ColorRange.Limited), (Strings.Get("Settings_RangeFull"), ColorRange.Full)],
            () => _model.ColorRange, v => _model.ColorRange = v));
        page.Children.Add(Note(Strings.Get("Settings_ColorRangeNote")));
        page.Children.Add(Combo(Strings.Get("Settings_ColorMatrix"), [(Strings.Get("Common_Automatic"), ColorMatrix.Auto), (Strings.Get("Settings_Bt601"), ColorMatrix.Bt601), (Strings.Get("Settings_Bt709"), ColorMatrix.Bt709)],
            () => _model.Matrix, v => _model.Matrix = v));
        page.Children.Add(Note(Strings.Get("Settings_ColorMatrixNote")));
        return page;
    }

    private UIElement Audio()
    {
        var page = Page(Strings.Get("Settings_Audio"));
        var input = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var output = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _refresh.Add(() => input.Text = _capture?.CurrentDevice is { } d
            ? Strings.Format("Settings_SoundFrom", d.Audio?.Name ?? Strings.Get("Settings_TheCaptureCard"))
            : Strings.Get("Settings_SoundFromLater"));
        _refresh.Add(() =>
        {
            string? name = null;
            try { name = AudioEndpoints.DefaultRender()?.Name; }
            catch (Exception) { /* no audio service: say so below */ }
            output.Text = name is null
                ? Strings.Get("Settings_NoOutput")
                : Strings.Format("Settings_PlaysThrough", name);
        });
        page.Children.Add(input);
        page.Children.Add(output);
        page.Children.Add(Slider(Strings.Get("Settings_AudioDelay"), 0, 500, 10, () => _model.AudioDelayMs, v => _model.AudioDelayMs = (int)v, v => Strings.Format("Settings_Milliseconds", v)));
        page.Children.Add(Note(Strings.Get("Settings_AudioDelayNote")));
        page.Children.Add(Combo(Strings.Get("Settings_Channels"), [
            (Strings.Get("Common_Automatic"), ChannelMode.Auto), (Strings.Get("Channel_Stereo"), ChannelMode.Normal), (Strings.Get("Channel_Swap"), ChannelMode.Swap),
            (Strings.Get("Channel_LeftToBoth"), ChannelMode.LeftToBoth), (Strings.Get("Channel_RightToBoth"), ChannelMode.RightToBoth), (Strings.Get("Channel_Mono"), ChannelMode.Mono)],
            () => _model.ChannelMode, v => _model.ChannelMode = v));
        page.Children.Add(Slider(Strings.Get("Settings_Volume"), 0, 100, 1, () => _model.Volume, v => _model.Volume = v, v => $"{v:0}%"));
        page.Children.Add(Toggle(Strings.Get("Control_Mute"), null, () => _model.Muted, v => _model.Muted = v));
        return page;
    }

    private UIElement Image()
    {
        var page = Page(Strings.Get("Settings_Image"));
        var looks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var look in LookPreset.All)
        {
            var button = new Button { Content = look.Title };
            AutomationProperties.SetName(button, Strings.Format("Settings_LookName", look.Title));
            button.Click += (_, _) => _model.ApplyLook(look);
            looks.Children.Add(button);
        }
        page.Children.Add(new TextBlock { Text = Strings.Get("Settings_Look") });
        page.Children.Add(looks);
        page.Children.Add(Slider(Strings.Get("Settings_Brightness"), -0.5, 0.5, 0.01, () => _model.Brightness, v => _model.Brightness = v, v => $"{v:0.00}"));
        page.Children.Add(Slider(Strings.Get("Settings_Contrast"), 0.5, 1.5, 0.01, () => _model.Contrast, v => _model.Contrast = v, v => $"{v:0.00}"));
        page.Children.Add(Slider(Strings.Get("Settings_Saturation"), 0, 2, 0.01, () => _model.Saturation, v => _model.Saturation = v, v => $"{v:0.00}"));
        page.Children.Add(Slider(Strings.Get("Settings_Gamma"), 0.5, 2, 0.01, () => _model.Gamma, v => _model.Gamma = v, v => $"{v:0.00}"));
        page.Children.Add(Slider(Strings.Get("Settings_Sharpness"), 0, 1, 0.01, () => _model.Sharpness, v => _model.Sharpness = v, v => $"{v:0.00}"));
        var reset = new Button { Content = Strings.Get("Settings_Reset") };
        AutomationProperties.SetName(reset, Strings.Get("Settings_ResetName"));
        reset.Click += (_, _) => _model.ResetAdjustments();
        _refresh.Add(() => reset.IsEnabled = !_model.AdjustmentsAreIdentity);
        page.Children.Add(reset);
        return page;
    }

    private UIElement Profiles()
    {
        var page = Page(Strings.Get("Settings_Profiles"));
        page.Children.Add(Note(Strings.Get("Settings_ProfilesNote")));
        _profileList = new StackPanel { Spacing = 4 };
        page.Children.Add(_profileList);
        RebuildProfiles();
        var save = new Button { Content = Strings.Get("Settings_SaveProfileButton") };
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
            row.Children.Add(new TextBlock { Text = profile.IsBuiltIn ? Strings.Format("Settings_BuiltIn", profile.Name) : profile.Name, VerticalAlignment = VerticalAlignment.Center });
            var apply = new Button { Content = Strings.Get("Settings_Apply") };
            AutomationProperties.SetName(apply, Strings.Format("Settings_ApplyName", profile.Name));
            apply.Click += (_, _) => _model.ApplyProfile(profile);
            Grid.SetColumn(apply, 1);
            row.Children.Add(apply);
            if (!profile.IsBuiltIn)
            {
                var delete = new Button { Content = Strings.Get("Settings_Delete") };
                AutomationProperties.SetName(delete, Strings.Format("Settings_DeleteName", profile.Name));
                delete.Click += (_, _) => { _model.DeleteProfile(profile); RebuildProfiles(); };
                Grid.SetColumn(delete, 2);
                row.Children.Add(delete);
            }
            list.Children.Add(row);
        }
    }

    private async Task SaveProfileAsync(XamlRoot root)
    {
        var name = new TextBox { PlaceholderText = Strings.Get("Settings_ProfileName") };
        AutomationProperties.SetName(name, Strings.Get("Settings_ProfileName"));
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = Strings.Get("Settings_SaveProfileTitle"),
            Content = name,
            PrimaryButtonText = Strings.Get("Settings_Save"),
            CloseButtonText = Strings.Get("Settings_Cancel"),
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
        var page = Page(Strings.Get("Settings_About"));
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        page.Children.Add(new TextBlock { Text = Strings.Get("App_Name"), FontSize = 20, FontWeight = FontWeights.SemiBold });
        page.Children.Add(new TextBlock { Text = Strings.Format("Settings_Version", version?.ToString(3) ?? "1.0.0") });
        page.Children.Add(Note(Strings.Get("Settings_AboutText")));
        page.Children.Add(new TextBlock { Text = Strings.Get("Settings_ThirdParty"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0) });
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
        AutomationProperties.SetName(licences, Strings.Get("Settings_ThirdParty"));
        page.Children.Add(licences);
        return page;
    }

    private static string ReadLicences()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY.md");
            return File.Exists(path) ? File.ReadAllText(path) : Strings.Get("Settings_LicensesMissing");
        }
        catch (IOException) { return Strings.Get("Settings_LicensesUnreadable"); }
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
        SetFormatItems([]); // Strings.Get("Common_Automatic") only until the list arrives
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
        FormatPreference.Pinned p when p.FrameRate is { } rate => Strings.Format("Settings_FormatRate", p.Width, p.Height, rate),
        FormatPreference.Pinned p => Strings.Format("Settings_FormatSize", p.Width, p.Height),
        _ => Strings.Get("Common_Automatic"),
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
        var toggle = new ToggleSwitch { Header = header, OnContent = Strings.Get("Common_On"), OffContent = Strings.Get("Common_Off") };
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
