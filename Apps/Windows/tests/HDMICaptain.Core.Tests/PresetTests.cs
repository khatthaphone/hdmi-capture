namespace HDMICaptain.Core.Tests;

public class PresetTests
{
    [Fact]
    public void PresetRoundTrips()
    {
        var preset = new DevicePreset
        {
            Format = new FormatPreference.Pinned(1920, 1080, 60.0),
            Volume = 0.5f,
            Muted = true,
            Scale = ScaleMode.Integer,
            Filter = FilterMode.Nearest,
            AudioDelayMs = 120,
            ChannelMode = ChannelMode.Swap,
            Adjustments = new ImageAdjustments { Brightness = 0.1 },
            LutName = "Rec709",
        };
        Assert.Equal(preset, DevicePreset.Decode(preset.Encode()));
    }

    [Fact]
    public void JsonUsesTheSwiftKeysAndRawValues()
    {
        var json = new DevicePreset { Scale = ScaleMode.Integer, ChannelMode = ChannelMode.LeftToBoth, Format = new FormatPreference.Pinned(1280, 720, null) }.Encode();
        Assert.Contains("\"scale\":\"integer\"", json);
        Assert.Contains("\"channelMode\":\"leftToBoth\"", json);
        Assert.Contains("\"colorRange\":\"auto\"", json);
        Assert.Contains("\"audioDelayMs\":0", json);
        Assert.Contains("\"format\":{\"pinned\":{\"width\":1280,\"height\":720}}", json);
        Assert.Contains("\"format\":{\"auto\":{}}", new DevicePreset().Encode());
    }

    [Fact]
    public void ReadsWhatSwiftWrites() =>
        Assert.Equal(
            new DevicePreset { Format = new FormatPreference.Pinned(1920, 1080, 30), Scale = ScaleMode.Fill, ColorRange = ColorRange.Full },
            DevicePreset.Decode("""{"format":{"pinned":{"width":1920,"height":1080,"frameRate":30}},"scale":"fill","colorRange":"full","audio":{"automatic":{}},"profileID":"00000000-0000-0000-0000-00000000B002"}"""));

    [Fact]
    public void ToleratesOldNewAndBrokenData()
    {
        Assert.Equal(new DevicePreset { Volume = 0.3f }, DevicePreset.Decode("""{"volume":0.3,"someFutureField":true}"""));
        Assert.Equal(new DevicePreset(), DevicePreset.Decode("""{"scale":"NoSuchMode"}"""));
        Assert.Equal(new DevicePreset(), DevicePreset.Decode("not json"));
        Assert.Equal(new DevicePreset(), DevicePreset.Decode(null));
    }

    [Fact]
    public void SplitsIntoRenderAndAudio()
    {
        var preset = new DevicePreset { Scale = ScaleMode.Fill, AudioDelayMs = 40 };
        Assert.Equal(new RenderOptions(Scale: ScaleMode.Fill), preset.RenderOptions);
        Assert.Equal(preset, new DevicePreset().With(preset.RenderOptions).With(preset.AudioSettings));
    }

    [Fact]
    public void LooksMatchApple()
    {
        Assert.Equal(LookPreset.Standard, LookPreset.Of(new ImageAdjustments()));
        Assert.Equal(LookPreset.Vivid, LookPreset.Of(new ImageAdjustments { Contrast = 1.15, Saturation = 1.35, Sharpness = 0.2 }));
        Assert.Null(LookPreset.Of(new ImageAdjustments { Brightness = 0.3 }));
    }

    [Fact]
    public void RetroProfileAppliesIntegerNearest()
    {
        var retro = Profile.BuiltIns.First(p => p.Name == "Retro");
        var applied = retro.Settings.ApplyTo(new DevicePreset { Volume = 0.4f });
        Assert.Equal(ScaleMode.Integer, applied.Scale);
        Assert.Equal(FilterMode.Nearest, applied.Filter);
        Assert.Equal(0.4f, applied.Volume); // sound level isn't part of a profile
    }

    [Fact]
    public void UserProfilesRoundTripWithoutBuiltIns()
    {
        var mine = new Profile
        {
            Id = "u1",
            Name = "PS2",
            Settings = new ProfileSettings(new DevicePreset { Scale = ScaleMode.Fill, AudioDelayMs = 80 }),
        };
        var decoded = Profile.Decode(Profile.Encode([.. Profile.BuiltIns, mine]));
        Assert.Equal([mine], decoded);
        Assert.Empty(Profile.Decode("garbage"));
    }

    [Fact]
    public void ViewerPrefsRoundTripAndDefaults()
    {
        var prefs = new ViewerPrefs
        {
            FloatOnTop = true,
            Guides = new GuideSettings { Thirds = true, Mask = GuideMask.Scope, Opacity = 0.4 },
            RecentMinutes = 30,
        };
        var json = prefs.Encode();
        Assert.Contains("\"mask\":\"2.39:1\"", json);
        Assert.Contains("\"floatOnTop\":true", json);
        Assert.Equal(prefs, ViewerPrefs.Decode(json));
        Assert.Equal(new ViewerPrefs(), ViewerPrefs.Decode("{}"));
        Assert.True(ViewerPrefs.Decode("{}").LockAspect);
        Assert.Equal(new ViewerPrefs(), ViewerPrefs.Decode("oops"));
    }

    [Fact]
    public void MaskRectLetterboxesAndPillarboxes()
    {
        var video = new Rect(0, 0, 1920, 1080);
        var scope = GuideSettings.MaskRect(GuideMask.Scope, video);
        Assert.Equal(1920, scope.Width, 6);
        Assert.Equal(1920 / 2.39, scope.Height, 6);
        Assert.Equal((1080 - scope.Height) / 2, scope.Y, 6);
        var vertical = GuideSettings.MaskRect(GuideMask.Vertical, video);
        Assert.Equal(1080, vertical.Height, 6);
        Assert.Equal(1080 * 9.0 / 16.0, vertical.Width, 6);
        Assert.True(new GuideSettings().IsEmpty);
        Assert.False(new GuideSettings { Center = true }.IsEmpty);
    }

    [Fact]
    public void ProFeatureTitles()
    {
        Assert.Equal(["Recording", "Save Recent", "Live streaming", "Virtual camera"], Enum.GetValues<ProFeature>().Select(f => f.Title()));
    }
}
