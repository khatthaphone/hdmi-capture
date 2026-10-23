using HDMICaptain.Core;
using HDMICaptain.Linux.App;

namespace HDMICaptain.Linux.App.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("hc-prefs").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void PresetsArePerCardAndSurviveARestart()
    {
        var settings = new ViewerSettings(new PrefsStore(_folder));
        settings.UseDevice("usb:1DE1:F105|name:UGREEN-25854");
        settings.Edit(p => p with { Scale = ScaleMode.Integer, AudioDelayMs = 120, Format = new FormatPreference.Pinned(1280, 720, 60) });
        settings.UseDevice("usb:345F:2130|name:USB3 Video");
        Assert.Equal(ScaleMode.Fit, settings.Preset.Scale); // a new card starts from the defaults

        var reopened = new ViewerSettings(new PrefsStore(_folder));
        reopened.UseDevice("usb:1DE1:F105|name:UGREEN-25854");
        Assert.Equal(ScaleMode.Integer, reopened.Preset.Scale);
        Assert.Equal(120, reopened.Preset.AudioDelayMs);
        Assert.Equal(new FormatPreference.Pinned(1280, 720, 60), reopened.Preset.Format);
    }

    [Fact]
    public void PrefsUseTheSharedKeys()
    {
        var store = new PrefsStore(_folder);
        store.Update(p => { p.CleanMode = true; p.ShowStats = true; });
        var json = File.ReadAllText(Path.Combine(_folder, "prefs.json"));
        Assert.Contains("\"cleanMode\":true", json.Replace(" ", ""));
        Assert.Contains("\"showStats\":true", json.Replace(" ", ""));
        Assert.True(new PrefsStore(_folder).Prefs.CleanMode);
    }

    [Fact]
    public void StateRemembersThePreferredCardAndWindowSize()
    {
        new PrefsStore(_folder).UpdateState(s => s with { PreferredDeviceKey = "usb:A", WindowWidth = 1280, WindowHeight = 760 });
        var state = new PrefsStore(_folder).State;
        Assert.Equal(("usb:A", 1280, 760), (state.PreferredDeviceKey, state.WindowWidth, state.WindowHeight));
    }

    [Fact]
    public void DamagedFilesReadAsDefaults()
    {
        File.WriteAllText(Path.Combine(_folder, "prefs.json"), "{ not json");
        File.WriteAllText(Path.Combine(_folder, "state.json"), "]");
        var store = new PrefsStore(_folder);
        Assert.False(store.Prefs.CleanMode);
        Assert.Null(store.State.PreferredDeviceKey);
    }

    [Fact]
    public void ProfilesApplyToTheCardOnScreen()
    {
        var settings = new ViewerSettings(new PrefsStore(_folder));
        settings.UseDevice("usb:A");
        var retro = settings.Store.Presets.LoadProfiles().First(p => p.Settings.Filter == FilterMode.Nearest);
        settings.ApplyProfile(retro);
        Assert.Equal(FilterMode.Nearest, settings.Preset.Filter);
        Assert.Equal(retro.Settings.Scale, settings.Preset.Scale);
    }

    [Theory]
    [InlineData("noAccess")]
    [InlineData("inUse")]
    [InlineData("waiting")]
    [InlineData("failed")]
    [InlineData("black")]
    public void EveryQaPhaseHasAStatusCard(string name)
    {
        var phase = QaPhases.Parse(name);
        Assert.NotNull(phase);
        Assert.NotNull(StatusCopy.For(phase));
    }

    [Fact]
    public void WaitingWithoutACardIsTheConnectScreen()
    {
        Assert.IsType<ViewerPhase.Scanning>(ViewerPhase.From(new CaptureState.Recovering(RecoveryReason.Waiting), null));
        Assert.IsType<ViewerPhase.Waiting>(ViewerPhase.From(new CaptureState.Recovering(RecoveryReason.Waiting), "UGREEN-25854"));
    }
}
