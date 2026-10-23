using HDMICaptain.Core;

namespace HDMICaptain.Linux.App;

/// <summary>
/// The open viewer windows and the grid (Multi-View, task 118). V4L2 lets one process stream a card once, so a card
/// belongs to at most one viewer: card windows are pinned to theirs, the main window skips cards held elsewhere, and
/// the grid taps a viewer's frames instead of opening the card again. Also routes sound to the focused window.
/// UI thread only.
/// </summary>
public static class Viewers
{
    private static readonly List<MainWindow> Open = [];
    private static Adw.Application? _app;
    private static LaunchOptions _options = LaunchOptions.Default;
    private static PrefsStore? _store;

    public static GridWindow? Grid { get; internal set; }

    public static IReadOnlyList<MainWindow> Windows => Open;

    public static MainWindow? Primary => Open.FirstOrDefault(w => w.IsPrimary);

    public static void OpenPrimary(Adw.Application app, LaunchOptions options, PrefsStore store)
    {
        (_app, _options, _store) = (app, options, store);
        Show(new MainWindow(app, options, new ViewerSettings(store), cardKey: null));
    }

    /// <summary>A window of its own for one card; a card that already has a viewer just comes forward.</summary>
    public static void OpenCard(string deviceKey)
    {
        if (_app == null || _store == null) return;
        if (Open.FirstOrDefault(w => w.CardKey == deviceKey || w.ShowingCard == deviceKey) is { } open)
        {
            open.Present();
            return;
        }
        Grid?.Release(deviceKey);
        Show(new MainWindow(_app, _options with { TestPattern = null, QaAction = null, QaPhase = null }, new ViewerSettings(_store), deviceKey));
        ClaimsChanged();
    }

    /// <summary>Opens a window for every connected card the main window isn't showing.</summary>
    public static void OpenAllCards()
    {
        foreach (var device in Primary?.Capture?.Catalog.Devices ?? [])
            if (Open.All(w => w.ShowingCard != device.Key && w.CardKey != device.Key)) OpenCard(device.Key);
    }

    public static void ShowGrid()
    {
        if (Primary is not { } primary) return;
        Grid ??= new GridWindow(primary, _options);
        Grid.Present();
    }

    private static void Show(MainWindow window)
    {
        Open.Add(window);
        window.Closed += () =>
        {
            Open.Remove(window);
            ClaimsChanged();
            if (window.IsPrimary)
            {
                // The main window quits the app, like the last-window rule for the primary viewer elsewhere.
                foreach (var other in Open.ToList()) other.Close();
                Grid?.Close();
            }
        };
        window.Present();
    }

    /// <summary>A card held by a card window other than <paramref name="asker"/>, or by the grid's own session.</summary>
    public static bool IsClaimedByOther(MainWindow asker, string deviceKey) =>
        Open.Any(w => !ReferenceEquals(w, asker) && w.CardKey == deviceKey) || Grid?.Owns(deviceKey) == true;

    /// <summary>The viewer capturing <paramref name="deviceKey"/> right now (the grid taps its frames).</summary>
    public static MainWindow? ViewerShowing(string deviceKey) => Open.FirstOrDefault(w => w.ShowingCard == deviceKey);

    public static void ClaimsChanged()
    {
        foreach (var w in Open)
        {
            w.RefreshClaims(Open.Where(o => !ReferenceEquals(o, w) && o.CardKey != null).Select(o => o.CardKey!)
                .Concat(Grid?.OwnedKeys ?? []));
            w.OnClaimsChanged();
        }
    }

    /// <summary>"Sound follows the focused window": only the focused viewer or the grid is heard.</summary>
    public static void FocusAudio(object focused)
    {
        var follow = _store?.Prefs.AudioFollowsFocus ?? true;
        foreach (var w in Open) w.SetSilenced(follow && !ReferenceEquals(w, focused));
        Grid?.SetSilenced(follow && !ReferenceEquals(Grid, focused));
    }
}
