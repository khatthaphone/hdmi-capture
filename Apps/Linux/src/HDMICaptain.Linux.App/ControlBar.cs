using HDMICaptain.Core;

namespace HDMICaptain.Linux.App;

/// <summary>
/// The hover control bar (Apps/Shared/ControlBar.swift on Apple): mute + volume, scale, screenshot, full screen and
/// the window menu, in an OSD pill over the video. Every button has an accessible name and a tooltip.
/// </summary>
public sealed class ControlBar
{
    private readonly MainWindow _owner;
    private readonly Gtk.Button _mute;
    private readonly Gtk.Scale _volume;
    private readonly Gtk.MenuButton _scale;
    private readonly Gtk.Button _fullScreen;
    private readonly Gtk.MenuButton _menu;
    private bool _syncing;

    public ControlBar(MainWindow owner)
    {
        _owner = owner;
        var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
        box.AddCssClass("osd");
        box.AddCssClass("hc-bar");

        _mute = IconButton("audio-volume-high-symbolic", Strings.ControlMute, () => _owner.ToggleMute());
        box.Append(_mute);

        _volume = Gtk.Scale.NewWithRange(Gtk.Orientation.Horizontal, 0, 1, 0.05);
        _volume.SetDrawValue(false);
        _volume.SetSizeRequest(110, -1);
        _volume.SetValue(1);
        _volume.SetTooltipText(Strings.ControlVolume);
        A11y.SetLabel(_volume, Strings.ControlVolume);
        _volume.OnValueChanged += (_, _) =>
        {
            if (!_syncing) _owner.SetVolume(_volume.GetValue());
            Sync();
        };
        box.Append(_volume);

        box.Append(Gtk.Separator.New(Gtk.Orientation.Vertical));

        _scale = Gtk.MenuButton.New();
        _scale.SetIconName("zoom-fit-best-symbolic");
        _scale.SetMenuModel(Actions.ScaleMenu());
        _scale.SetTooltipText("Scale");
        A11y.SetLabel(_scale, "Scale");
        box.Append(_scale);

        box.Append(IconButton("camera-photo-symbolic", Strings.ControlScreenshot, () => _owner.SaveScreenshot()));

        _fullScreen = IconButton("view-fullscreen-symbolic", Strings.ControlFullScreen, () => _owner.ToggleFullScreen());
        box.Append(_fullScreen);

        _menu = Gtk.MenuButton.New();
        _menu.SetIconName("open-menu-symbolic");
        _menu.SetMenuModel(Actions.MainMenu());
        _menu.SetTooltipText(Strings.ControlMenu);
        A11y.SetLabel(_menu, Strings.ControlMenu);
        box.Append(_menu);

        Widget = box;
    }

    public Gtk.Widget Widget { get; }

    /// <summary>A menu is open: keep the bar on screen.</summary>
    public bool IsBusy => _scale.GetActive() || _menu.GetActive();

    /// <summary>Reflects the window's state (mute, volume, full screen) without feeding it back.</summary>
    public void Sync()
    {
        _syncing = true;
        var muted = _owner.IsMuted || _owner.Volume <= 0;
        _mute.SetIconName(muted ? "audio-volume-muted-symbolic" : _owner.Volume < 0.5 ? "audio-volume-low-symbolic" : "audio-volume-high-symbolic");
        var muteName = _owner.IsMuted ? Strings.ControlUnmute : Strings.ControlMute;
        _mute.SetTooltipText(muteName);
        A11y.SetLabel(_mute, muteName);
        if (Math.Abs(_volume.GetValue() - _owner.Volume) > 0.001) _volume.SetValue(_owner.Volume);
        var full = _owner.IsFullScreen;
        _fullScreen.SetIconName(full ? "view-restore-symbolic" : "view-fullscreen-symbolic");
        var fullName = full ? Strings.ControlExitFullScreen : Strings.ControlFullScreen;
        _fullScreen.SetTooltipText(fullName);
        A11y.SetLabel(_fullScreen, fullName);
        _syncing = false;
    }

    private static Gtk.Button IconButton(string icon, string name, Action onClick)
    {
        var button = Gtk.Button.NewFromIconName(icon);
        button.SetTooltipText(name);
        A11y.SetLabel(button, name);
        button.OnClicked += (_, _) => onClick();
        return button;
    }
}
