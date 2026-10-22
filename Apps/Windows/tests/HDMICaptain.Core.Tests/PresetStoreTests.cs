namespace HDMICaptain.Core.Tests;

public sealed class PresetStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "hdmicaptain-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }

    [Fact]
    public void PresetsComeBackForTheSameCardAfterARestart()
    {
        var key = DeviceKey.Of(0x3188, 0x1000, null, "USB3.0 Video");
        var preset = new DevicePreset
        {
            Format = new FormatPreference.Pinned(1920, 1080, 60),
            Scale = ScaleMode.Integer,
            Matrix = ColorMatrix.Bt601,
            AudioDelayMs = 80,
            Adjustments = new ImageAdjustments { Gamma = 1.2 },
        };
        new PresetStore(_folder).Set(key, preset);

        var reopened = new PresetStore(_folder);
        Assert.Equal(preset, reopened.Get(key));
        Assert.True(reopened.Has(key));
        Assert.Equal(new DevicePreset(), reopened.Get("usb:0000:0000"));
    }

    [Fact]
    public void ADamagedEntryDoesNotLoseTheOthers()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "presets.json"),
            "{\"bad\":{\"scale\":\"nonsense\"},\"good\":{\"scale\":\"fill\"}}");
        var store = new PresetStore(_folder);
        Assert.Equal(ScaleMode.Fill, store.Get("good").Scale);
        Assert.False(store.Has("bad"));
    }

    [Fact]
    public void GarbageFileReadsAsEmpty()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "presets.json"), "{{{");
        Assert.False(new PresetStore(_folder).Has("x"));
    }

    [Fact]
    public void AtomicWriteReplacesAndLeavesNoTempFile()
    {
        var path = Path.Combine(_folder, "nested", "a.json");
        AtomicFile.WriteAllText(path, "one");
        AtomicFile.WriteAllText(path, "two");
        Assert.Equal("two", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
        Assert.Null(AtomicFile.ReadAllText(Path.Combine(_folder, "missing.json")));
    }

    [Fact]
    public void ProfilesKeepBuiltInsFirstAndStoreOnlyUserOnes()
    {
        var store = new PresetStore(_folder);
        var mine = new Profile { Id = "p1", Name = "Switch dock", Settings = new ProfileSettings { Scale = ScaleMode.Fill } };
        store.SaveProfiles([.. store.LoadProfiles(), mine]);

        var loaded = new PresetStore(_folder).LoadProfiles();
        Assert.Equal(Profile.BuiltIns.Count + 1, loaded.Count);
        Assert.All(loaded.Take(Profile.BuiltIns.Count), p => Assert.True(p.IsBuiltIn));
        Assert.Equal("Switch dock", loaded[^1].Name);
        Assert.False(loaded[^1].IsBuiltIn);
    }
}
