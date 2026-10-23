using HDMICaptain.Core;
using HDMICaptain.Linux.Media;
using HDMICaptain.Linux.Media.Audio;
using HDMICaptain.Linux.Media.Capture;
using HDMICaptain.Linux.Media.Render;
using HDMICaptain.Linux.Media.Video;

namespace HDMICaptain.Linux.App;

/// <summary>
/// The viewer window (spec §14.7, task 114): the video surface with status pages, a hover control bar, toasts and a
/// right-click menu on top. Capture runs in <see cref="CaptureController"/> on its own threads; this class only
/// shows its state. Overlays are GTK widgets and never reach screenshots (they read pass 1).
/// </summary>
public sealed unsafe class MainWindow
{
    private const int HideAfterMs = 3000;

    private readonly Adw.Application _app;
    private readonly LaunchOptions _options;
    private readonly Adw.ApplicationWindow _window;
    private readonly Adw.ToolbarView _toolbar;
    private readonly Adw.ToastOverlay _toasts;
    private readonly Gtk.Overlay _overlay;
    private readonly Adw.StatusPage _status;
    private readonly Gtk.Spinner _spinner;
    private readonly Gtk.Box _statusButtons;
    private readonly Gtk.Revealer _bar;
    private readonly Gtk.Label _badge;
    private readonly Gtk.Label _stats;
    private readonly ControlBar _controls;
    private readonly GlesRenderer _renderer = new();
    private readonly VideoView _video;
    private readonly GuidesView _guides = new();
    private TestPatternKind? _pattern;
    private GuideSettings? _qaGuides;
    private readonly FrameWriter _writer;
    private readonly SynchronizationContext _ui;
    private readonly SignalDebouncer _signal = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private CaptureController? _capture;
    private LinuxPower? _power;
    private LinuxAudioPipeline? _audio;
    private string? _audioDevice;
    private bool _audioMissing;
    private ViewerPhase _phase = new ViewerPhase.Scanning();
    private readonly ViewerSettings _settings;
    private bool _clean, _diagnostics;
    private FormatPreference? _openedWith;
    private uint _hideTimer, _tickTimer;
    private long _lastFramesIn;
    private bool _silenced;
    private bool _closed;

    /// <param name="cardKey">A card window (Multi-View): shows only this card. Null for the main window.</param>
    public MainWindow(Adw.Application app, LaunchOptions options, ViewerSettings settings, string? cardKey)
    {
        _app = app;
        _options = options;
        _settings = settings;
        CardKey = cardKey;
        _ui = SynchronizationContext.Current!;
        // -qaShowStats shows diagnostics for this run only; it is never written to prefs (CLAUDE.md QA rule).
        _diagnostics = options.QaShowStats || settings.Prefs.ShowStats;
        _clean = settings.Prefs.CleanMode;

        _window = Adw.ApplicationWindow.New(app);
        _window.SetTitle("HDMI Captain");
        var state = settings.Store.State;
        _window.SetDefaultSize(state.WindowWidth ?? 960, state.WindowHeight ?? 600);
        _window.AddCssClass("hc-viewer");

        _video = new VideoView(_renderer);
        _writer = new FrameWriter(_renderer);

        _overlay = Gtk.Overlay.New();
        _overlay.SetChild(_video.Widget);
        _overlay.AddOverlay(_guides.Widget);

        // Status page (spinner or icon, title, message, buttons) over the video while there is no picture.
        _status = Adw.StatusPage.New();
        _status.AddCssClass("hc-status");
        _spinner = Gtk.Spinner.New();
        _spinner.SetSizeRequest(32, 32);
        _statusButtons = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        _statusButtons.SetHalign(Gtk.Align.Center);
        var statusChild = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        statusChild.Append(_spinner);
        statusChild.Append(_statusButtons);
        _status.SetChild(statusChild);
        _overlay.AddOverlay(_status);

        _badge = Gtk.Label.New(Strings.AudioBadge);
        _badge.AddCssClass("osd");
        _badge.AddCssClass("hc-badge");
        _badge.SetHalign(Gtk.Align.Center);
        _badge.SetValign(Gtk.Align.Start);
        _badge.SetMarginTop(16);
        _badge.SetVisible(false);
        _overlay.AddOverlay(_badge);

        _stats = Gtk.Label.New("");
        _stats.AddCssClass("osd");
        _stats.AddCssClass("monospace");
        _stats.AddCssClass("hc-stats");
        _stats.SetHalign(Gtk.Align.Start);
        _stats.SetValign(Gtk.Align.Start);
        _stats.SetMarginTop(12);
        _stats.SetMarginStart(12);
        _stats.SetVisible(_diagnostics);
        _overlay.AddOverlay(_stats);

        _controls = new ControlBar(this);
        _bar = Gtk.Revealer.New();
        _bar.SetTransitionType(Gtk.RevealerTransitionType.Crossfade);
        _bar.SetChild(_controls.Widget);
        _bar.SetHalign(Gtk.Align.Center);
        _bar.SetValign(Gtk.Align.End);
        _bar.SetMarginBottom(20);
        _bar.SetRevealChild(true);
        _overlay.AddOverlay(_bar);

        _toasts = Adw.ToastOverlay.New();
        _toasts.SetChild(_overlay);

        _toolbar = Adw.ToolbarView.New();
        _toolbar.AddTopBar(Adw.HeaderBar.New());
        _toolbar.SetContent(_toasts);
        _window.SetContent(_toolbar);

        Actions.Install(this, _window, app);
        AttachInput();
        Styles.Load();

        _window.OnCloseRequest += (_, _) =>
        {
            Shutdown();
            return false;
        };
        _window.OnNotify += (_, args) =>
        {
            if (args.Pspec.GetName() == "is-active" && _window.IsActive) Viewers.FocusAudio(this);
        };
        _window.OnRealize += (_, _) =>
        {
            if (_settings.Prefs.FloatOnTop) GLib.Functions.IdleAdd(GLib.Constants.PRIORITY_DEFAULT_IDLE, () => { X11Window.SetAbove(_window, true); return false; });
        };

        Start();
    }

    public Gtk.Window Window => _window;

    /// <summary>The card this window is pinned to (card windows), or null (the main window).</summary>
    public string? CardKey { get; }
    public bool IsPrimary => CardKey == null;

    /// <summary>The card on screen right now (live), for Multi-View claims and taps.</summary>
    public string? ShowingCard => _phase is ViewerPhase.Live or ViewerPhase.NoSignal && _pattern == null ? _capture?.CurrentDevice?.Key : null;

    /// <summary>Every captured frame, on the capture thread, before it is released (the grid's tiles tap it).</summary>
    public event V4l2Source.FrameHandler? FrameTap;

    public event Action? Closed;

    public void Close() => _window.Close();

    public void OnClaimsChanged() => _capture?.ClaimsChanged();

    /// <summary>Another window has the sound (focus follows); doesn't change the card's saved volume.</summary>
    public void SetSilenced(bool silenced)
    {
        if (_silenced == silenced) return;
        _silenced = silenced;
        if (_audio != null) _audio.Settings = EffectiveAudio;
    }

    private AudioSettings EffectiveAudio => _silenced ? _settings.Preset.AudioSettings with { Muted = true } : _settings.Preset.AudioSettings;
    internal ViewerSettings Settings => _settings;
    internal CaptureController? Capture => _capture;
    internal bool IsMuted => _settings.Preset.Muted;
    internal ScaleMode Scale => _settings.Preset.Scale;
    internal bool IsFullScreen => _window.IsFullscreen();

    public void Present() => _window.Present();

    // MARK: - Capture

    private void Start()
    {
        _settings.Changed += ApplySettings;
        ApplySettings();
        if (_clean) ApplyClean();
        if (_options.QaPhase is { } qa && QaPhases.Parse(qa) is { } phase)
        {
            // DEBUG: one status screen without a card (screenshots, accessibility checks); capture doesn't start.
            _audioMissing = qa is "audioMissing" or "micDenied";
            if (phase is ViewerPhase.Live or ViewerPhase.NoSignal) _renderer.StartPattern(TestPatternKind.SmpteBars);
            SetPhase(phase);
            return;
        }
        if (_options.TestPattern is { } pattern)
        {
            ShowPattern(pattern);
        }
        else
        {
            _power = new LinuxPower(_app, () => _window);
            var options = new CaptureControllerOptions
            {
                PinnedToPreferred = CardKey != null,
                // Viewers is UI-thread state; the controller asks from its own thread, so it reads a snapshot.
                IsClaimedElsewhere = key => _claims.Contains(key),
            };
            _capture = CaptureController.CreateDefault(CardKey ?? _settings.Store.State.PreferredDeviceKey, _power, options);
            if (CardKey == null)
                _capture.Catalog.PreferredChanged += key => _ui.Post(_ => _settings.Store.UpdateState(st => st with { PreferredDeviceKey = key }), null);
            _capture.Log += LinuxLog.Write;
            _capture.FrameArrived += OnFrame;
            _capture.StateChanged += state => _ui.Post(_ => OnCaptureState(state), null);
            _capture.Start();
        }
        _tickTimer = GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, 500, () =>
        {
            Tick();
            return true;
        });
        if (_options.QaAction is { } action) GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, 1500, () =>
        {
            Actions.RunQa(this, action);
            return false;
        });
    }

    // Capture thread: copy or decode straight into the renderer's next slot.
    private void OnFrame(byte* data, int length, V4l2Stream stream, long timestampNs)
    {
        _writer.Write(stream.FourCC, data, length, stream.Width, stream.Height, stream.BytesPerLine, timestampNs);
        FrameTap?.Invoke(data, length, stream, timestampNs);
    }

    private volatile IReadOnlySet<string> _claims = new HashSet<string>();

    /// <summary>Refreshes the cards other viewers hold (snapshot read by the control thread).</summary>
    internal void RefreshClaims(IEnumerable<string> keys) => _claims = keys.ToHashSet();

    private void OnCaptureState(CaptureState state)
    {
        var phase = ViewerPhase.From(state, _capture?.CurrentDevice?.Name);
        if (_capture?.CurrentDevice is { } device) _settings.UseDevice(device.Key);
        if (phase is ViewerPhase.Live)
        {
            _signal.Reset();
            StartAudio(_capture?.CurrentDevice);
        }
        else if (state is not CaptureState.Recovering { Reason: RecoveryReason.NoFrames })
        {
            StopAudio();
        }
        SetPhase(phase);
    }

    private void StartAudio(CaptureDevice? device)
    {
        var hw = device?.Audio is { } card ? $"plughw:{card.Index},0" : null;
        if (hw == _audioDevice && _audio != null) return;
        StopAudio();
        _audioDevice = hw;
        if (hw == null)
        {
            LinuxLog.Write($"audio: {device?.Name ?? "card"} has no ALSA card on the same USB device");
            SetAudioMissing(true);
            return;
        }
        _audio = new LinuxAudioPipeline(
            () => AlsaCapture.TryOpen(hw, out var capture) is var r && r == 0 ? capture! : throw new AudioOpenException(r, $"{hw}: {AlsaCapture.Error(r)}"),
            () => PulsePlayback.TryOpen(out var playback) == 0 ? playback! : throw new AudioOpenException(-Errno.EIO, "no sound server"))
        {
            Settings = EffectiveAudio,
        };
        _audio.Log += m => LinuxLog.Write($"audio: {m}");
        _audio.Failed += _ => _ui.Post(_ => SetAudioMissing(true), null);
        _audio.Start();
        SetAudioMissing(false);
    }

    private void StopAudio()
    {
        _audio?.Dispose();
        _audio = null;
        _audioDevice = null;
    }

    private void SetAudioMissing(bool missing)
    {
        _audioMissing = missing;
        UpdateBadge();
    }

    // MARK: - Phase and status

    private void SetPhase(ViewerPhase phase)
    {
        _phase = phase;
        // A test pattern covers whatever the card is doing.
        if (_pattern != null) phase = new ViewerPhase.Live();
        var copy = _video.Error != null && phase is ViewerPhase.Live
            ? new StatusCopy("dialog-warning-symbolic", Strings.StatusNoGl, _video.Error)
            : StatusCopy.For(phase);
        _status.SetVisible(copy != null);
        if (copy != null)
        {
            _status.SetTitle(copy.Title);
            _status.SetDescription(copy.Detail is { } d ? $"{copy.Message}\n\n{d}" : copy.Message);
            _status.SetIconName(copy.Spinner || copy.Icon.Length == 0 ? null : copy.Icon);
            _spinner.SetVisible(copy.Spinner);
            _spinner.SetSpinning(copy.Spinner);
            while (_statusButtons.GetFirstChild() is { } child) _statusButtons.Remove(child);
            foreach (var action in new[] { copy.Primary, copy.Secondary })
            {
                if (action == null) continue;
                var button = Gtk.Button.NewWithLabel(action.Title);
                button.AddCssClass("pill");
                if (action == copy.Primary) button.AddCssClass("suggested-action");
                button.OnClicked += (_, _) => OnStatusAction(action.Id);
                _statusButtons.Append(button);
            }
            _statusButtons.SetVisible(copy.Primary != null || copy.Secondary != null);
        }
        _window.SetTitle(phase is ViewerPhase.Live && _capture?.CurrentDevice is { } d2 ? $"{d2.Name} — HDMI Captain" : "HDMI Captain");
        UpdateBadge();
        ShowControls();
    }

    private void UpdateBadge() => _badge.SetVisible(_audioMissing && _phase is ViewerPhase.Live);

    private void OnStatusAction(StatusActionId id)
    {
        switch (id)
        {
            case StatusActionId.Retry:
                _capture?.Retry();
                break;
            case StatusActionId.Help:
                ConnectionHelp.Show(_window);
                break;
        }
    }

    /// <summary>Twice a second: no-signal detection from the renderer's Y/Cb/Cr sample, and the stats line.</summary>
    private void Tick()
    {
        if (_phase is ViewerPhase.Live or ViewerPhase.NoSignal && _options.QaPhase == null)
        {
            var s = _renderer.Signal;
            if (s.Count > 0)
            {
                var stats = new SignalAnalyzer.Stats(s.MeanY, 0, s.MeanCb, s.MeanCr, 0, s.RangeY, s.RangeChroma);
                if (_signal.Observe(SignalAnalyzer.Classify(stats, s.FullRange != 0), _clock.Elapsed.TotalSeconds))
                    SetPhase(_signal.Current.IsNoSignal ? new ViewerPhase.NoSignal(_signal.Current is SignalContent.Black) : new ViewerPhase.Live());
            }
        }
        if (_diagnostics) UpdateStats();
        var r = _renderer.Stats;
        _guides.Update(_qaGuides ?? _settings.Prefs.Guides, r.Width, r.Height, _settings.Preset.Scale);
    }

    /// <summary>A generated pattern instead of the card (menu, -testPattern); null returns to the live input.</summary>
    internal void ShowPattern(TestPatternKind? pattern)
    {
        _pattern = pattern;
        if (pattern is { } p) _renderer.StartPattern(p);
        else _renderer.StopPattern();
        _signal.Reset();
        SetPhase(pattern != null ? new ViewerPhase.Live() : _capture is { } c ? ViewerPhase.From(c.State, c.CurrentDevice?.Name) : new ViewerPhase.Scanning());
    }

    internal TestPatternKind? Pattern => _pattern;

    internal void EditGuides(Func<GuideSettings, GuideSettings> change)
    {
        var next = change(_settings.Prefs.Guides);
        _settings.EditPrefs(p => p.Guides = next);
        var r = _renderer.Stats;
        _guides.Update(next, r.Width, r.Height, _settings.Preset.Scale);
    }

    /// <summary>QA only: shows guides for this run without saving them (CLAUDE.md QA rule).</summary>
    internal void PreviewGuides(GuideSettings guides)
    {
        _qaGuides = guides;
        var r = _renderer.Stats;
        _guides.Update(guides, r.Width, r.Height, _settings.Preset.Scale);
    }

    internal void ShowScopes() => ScopesWindow.Show(_window, _renderer);

    private void UpdateStats()
    {
        var r = _renderer.Stats;
        var fps = (r.FramesIn - _lastFramesIn) * 2.0;
        _lastFramesIn = r.FramesIn;
        var stream = _capture?.CurrentStream;
        var counters = _capture?.Counters;
        var lines = new List<string>
        {
            stream is null ? $"{r.Width}×{r.Height} pattern" : $"{stream.Width}×{stream.Height} {stream.FourCC} @ {stream.FrameRate:0.##}",
            $"{fps:0.0} fps in · drawn {r.FramesDrawn} · arrival→draw {r.LastLatencyNs / 1e6:0.0} ms",
        };
        if (counters != null) lines.Add($"skipped {counters.Skipped} · gaps {counters.SequenceGaps} · empty {counters.Empty} · rejected {_writer.Rejected} · decode {_writer.LastMilliseconds:0.0} ms");
        if (_audio != null) lines.Add($"audio {_audio.BufferedMs:0.0} ms (target {_audio.TargetMs:0}) · {(_audio.Correction - 1) * 1e6:+0;-0} ppm · underflow {_audio.UnderflowFrames}");
        _stats.SetLabel(string.Join('\n', lines));
    }

    // MARK: - Input: hover bar, cursor, double-click, right-click

    private void AttachInput()
    {
        var motion = Gtk.EventControllerMotion.New();
        motion.OnMotion += (_, _) => ShowControls();
        _overlay.AddController(motion);

        var click = Gtk.GestureClick.New();
        click.SetButton(0);
        click.OnPressed += (gesture, args) =>
        {
            if (gesture.GetCurrentButton() == 3) Actions.ShowContextMenu(this, _overlay, args.X, args.Y);
            else if (args.NPress == 2) ToggleFullScreen();
        };
        _overlay.AddController(click);
    }

    internal void ShowControls()
    {
        _bar.SetRevealChild(true);
        _video.Widget.SetCursorFromName(null);
        if (_hideTimer != 0) GLib.Source.Remove(_hideTimer);
        _hideTimer = GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, HideAfterMs, () =>
        {
            _hideTimer = 0;
            if (_phase is ViewerPhase.Live && !_options.QaPinControls && !_controls.IsBusy)
            {
                _bar.SetRevealChild(false);
                _video.Widget.SetCursorFromName("none");
            }
            return false;
        });
    }

    // MARK: - Commands (control bar, menu, shortcuts)

    internal void ToggleFullScreen()
    {
        if (_window.IsFullscreen()) _window.Unfullscreen();
        else _window.Fullscreen();
        _toolbar.SetRevealTopBars(!_window.IsFullscreen() && !_clean);
        _controls.Sync();
    }

    internal void ExitFullScreen()
    {
        if (_window.IsFullscreen()) ToggleFullScreen();
    }

    internal void ToggleMute() => _settings.Edit(p => p with { Muted = !p.Muted });

    internal void SetVolume(double volume) => _settings.Edit(p => p with { Volume = (float)Math.Clamp(volume, 0, 1), Muted = false });

    internal double Volume => _settings.Preset.Volume;

    internal void SetScale(ScaleMode scale) => _settings.Edit(p => p with { Scale = scale });

    internal bool SharpPixels => _settings.Preset.Filter == FilterMode.Nearest;

    internal void ToggleSharpPixels() => _settings.Edit(p => p with { Filter = p.Filter == FilterMode.Nearest ? FilterMode.Linear : FilterMode.Nearest });

    /// <summary>The preset or prefs changed (here, in Settings, or a new card): push them to the renderer, audio and capture.</summary>
    private void ApplySettings()
    {
        var preset = _settings.Preset;
        _renderer.SetOptions(preset.RenderOptions);
        _renderer.SetLook(preset.Adjustments, preset.LutPath != null ? _settings.Lut : null, preset.LutIntensity);
        if (_audio != null) _audio.Settings = EffectiveAudio;
        if (_capture != null && preset.Format != _openedWith)
        {
            var reopen = _openedWith != null;
            _openedWith = preset.Format;
            _capture.Preference = preset.Format;
            if (reopen) _capture.Retry();
        }
        _video.Widget.QueueRender();
        _controls.Sync();
    }

    internal bool Clean => _clean;

    /// <summary>Clean mode: no header bar or window frame, just the picture.</summary>
    internal void ToggleClean()
    {
        _clean = !_clean;
        _settings.EditPrefs(p => p.CleanMode = _clean);
        ApplyClean();
    }

    private void ApplyClean()
    {
        _toolbar.SetRevealTopBars(!_clean && !_window.IsFullscreen());
        _window.SetDecorated(!_clean);
    }

    internal bool Diagnostics => _diagnostics;

    internal void ToggleDiagnostics()
    {
        _diagnostics = !_diagnostics;
        if (!_options.QaShowStats) _settings.EditPrefs(p => p.ShowStats = _diagnostics);
        _stats.SetVisible(_diagnostics);
        if (_diagnostics) UpdateStats();
    }

    internal void OpenSettings() => SettingsWindow.Show(this);

    /// <summary>X11 only (EWMH); on Wayland the compositor's window menu offers it.</summary>
    internal void SetKeepOnTop(bool onTop)
    {
        _settings.EditPrefs(p => p.FloatOnTop = onTop);
        if (!X11Window.SetAbove(_window, onTop)) LinuxLog.Write("keep on top: not available on this display (Wayland)");
    }

    internal void Reconnect() => _capture?.Retry();

    internal void Toast(string text)
    {
        var toast = Adw.Toast.New(text);
        toast.SetTimeout(2);
        _toasts.AddToast(toast);
    }

    /// <summary>Window size presets: the picture at 50/100/200 % of the source (letterboxing aside, Wayland can't lock aspect).</summary>
    internal void SizeTo(double factor)
    {
        var r = _renderer.Stats;
        if (r.Width <= 0)
        {
            Toast(Strings.ToastNoPictureToCapture);
            return;
        }
        var scale = Math.Max(1, _window.GetScaleFactor());
        var header = _toolbar.GetRevealTopBars() ? 47 : 0;
        _window.SetDefaultSize((int)(r.Width * factor / scale), (int)(r.Height * factor / scale) + header);
    }

    /// <summary>The clean pass-1 frame as a texture (screenshots, copy). Null when there is no picture.</summary>
    internal Gdk.Texture? CaptureFrame()
    {
        if (_video.ReadPass1() is not { } frame) return null;
        using var bytes = GLib.Bytes.New(frame.Rgba);
        return Gdk.MemoryTexture.New(frame.Width, frame.Height, Gdk.MemoryFormat.R8g8b8a8, bytes, (nuint)(frame.Width * 4));
    }

    internal void SaveScreenshot()
    {
        var texture = CaptureFrame();
        if (texture == null)
        {
            Toast(Strings.ToastNoPictureToCapture);
            return;
        }
        try
        {
            var dir = Path.Combine(GLib.Functions.GetUserSpecialDir(GLib.UserDirectory.DirectoryPictures)
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures"), "HDMI Captain");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"HDMI Captain {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
            if (!texture.SaveToPng(path)) throw new IOException("PNG encoder failed");
            LinuxLog.Write($"screenshot: {path}");
            Toast(Strings.ToastScreenshotSaved);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Toast(Strings.Format(Strings.ToastScreenshotFailed, e.Message));
        }
    }

    internal void CopyFrame()
    {
        var texture = CaptureFrame();
        if (texture == null)
        {
            Toast(Strings.ToastNoPictureToCopy);
            return;
        }
        _window.GetClipboard().SetTexture(texture);
        Toast(Strings.ToastFrameCopied);
    }

    private void Shutdown()
    {
        if (_closed) return;
        _closed = true;
        Closed?.Invoke();
        if (!_window.IsFullscreen() && !_window.IsMaximized())
        {
            var (w, h) = (_window.GetWidth(), _window.GetHeight());
            if (w > 200 && h > 150) _settings.Store.UpdateState(st => st with { WindowWidth = w, WindowHeight = h });
        }
        _settings.Changed -= ApplySettings;
        if (_tickTimer != 0) GLib.Source.Remove(_tickTimer);
        if (_hideTimer != 0) GLib.Source.Remove(_hideTimer);
        _tickTimer = _hideTimer = 0;
        StopAudio();
        _capture?.Dispose();
        _capture = null;
        _power?.Dispose();
        _renderer.StopPattern();
        _writer.Dispose();
    }
}
