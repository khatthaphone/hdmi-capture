using System.Text.Json;

namespace HDMICaptain.Core.Tests;

public class FormatSelectorTests
{
    public static TheoryData<string> SelectionCases() => Names("format-selection.json");

    public static TheoryData<string> BandwidthCases() => Names("format-bandwidth.json");

    private static TheoryData<string> Names(string file)
    {
        using var doc = Fixtures.Load(file);
        var data = new TheoryData<string>();
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
            data.Add(c.GetProperty("name").GetString()!);
        return data;
    }

    [Theory]
    [MemberData(nameof(SelectionCases))]
    public void SharedSelectionFixture(string name) => Run("format-selection.json", name);

    [Theory]
    [MemberData(nameof(BandwidthCases))]
    public void BandwidthFixture(string name) => Run("format-bandwidth.json", name);

    private static void Run(string file, string name)
    {
        using var doc = Fixtures.Load(file);
        var root = doc.RootElement;
        var c = root.GetProperty("cases").EnumerateArray().Single(e => e.GetProperty("name").GetString() == name);

        var formatsJson = c.TryGetProperty("formats", out var f)
            ? f
            : root.GetProperty("formatSets").GetProperty(c.GetProperty("formatSet").GetString()!);
        var formats = formatsJson.EnumerateArray().Select(ReadFormat).ToList();

        UsbLink? link = null;
        if (c.TryGetProperty("link", out var l) && l.ValueKind == JsonValueKind.String)
            link = l.GetString() == "usb2" ? UsbLink.Usb2 : UsbLink.Usb3;

        FormatPreference preference = new FormatPreference.Auto();
        if (c.TryGetProperty("preference", out var p) && p.ValueKind == JsonValueKind.Object)
            preference = new FormatPreference.Pinned(
                p.GetProperty("w").GetInt32(), p.GetProperty("h").GetInt32(),
                p.TryGetProperty("fps", out var fps) && fps.ValueKind == JsonValueKind.Number ? fps.GetDouble() : null);

        var choice = FormatSelector.Select(formats, preference, link);

        var expect = c.GetProperty("expect");
        if (expect.ValueKind == JsonValueKind.Null)
        {
            Assert.Null(choice);
            return;
        }
        Assert.NotNull(choice);
        Assert.Equal(
            $"{expect.GetProperty("w").GetInt32()}x{expect.GetProperty("h").GetInt32()} {expect.GetProperty("cc").GetString()}",
            $"{choice.Format.Width}x{choice.Format.Height} {choice.Format.FourCC}");
        Assert.Equal(expect.GetProperty("fps").GetDouble(), choice.FrameRate, 6);
    }

    private static FormatDescriptor ReadFormat(JsonElement o)
    {
        List<FrameRateRange> ranges = o.TryGetProperty("ranges", out var r)
            ? r.EnumerateArray().Select(x => new FrameRateRange(x[0].GetDouble(), x[1].GetDouble())).ToList()
            : o.GetProperty("rates").EnumerateArray().Select(x => new FrameRateRange(x.GetDouble(), x.GetDouble())).ToList();
        return new FormatDescriptor(o.GetProperty("w").GetInt32(), o.GetProperty("h").GetInt32(), o.GetProperty("cc").GetString()!, ranges);
    }

    private static FormatDescriptor Yuy2(int w, int h, params double[] rates) =>
        new(w, h, "YUY2", rates.Select(x => new FrameRateRange(x, x)).ToList());

    [Fact]
    public void OptionsAreUniqueAndBestFirst()
    {
        using var doc = Fixtures.Load("format-selection.json");
        var ms2131 = doc.RootElement.GetProperty("formatSets").GetProperty("ms2131").EnumerateArray().Select(ReadFormat).ToList();
        var options = FormatSelector.Options(ms2131);
        Assert.Equal(new FormatPreference.Pinned(1920, 1080, 30.0), options[0]);
        Assert.Equal(options.Count, options.Distinct().Count());
    }

    [Fact]
    public void Usb2OptionsHideRatesThatDontFit()
    {
        var yuy2 = Yuy2(1920, 1080, 60, 30, 5);
        Assert.Equal([new FormatPreference.Pinned(1920, 1080, 5.0)], FormatSelector.Options([yuy2], UsbLink.Usb2));
        Assert.Equal(60.0, FormatSelector.Options([yuy2], UsbLink.Usb3)[0].FrameRate);
    }

    [Fact]
    public void NearestSupportedRateClampsIntoRanges()
    {
        var f = new FormatDescriptor(640, 480, "YUY2", [new FrameRateRange(5, 15), new FrameRateRange(24, 30)]);
        Assert.Equal(15.0, f.NearestSupportedRate(18));
        Assert.Equal(24.0, f.NearestSupportedRate(21));
        Assert.Equal(30.0, f.NearestSupportedRate(60));
    }

    [Fact]
    public void BytesPerFrameDependsOnEncoding()
    {
        Assert.Null(new FormatDescriptor(1920, 1080, "MJPG", []).BytesPerFrame);
        Assert.Equal(1920 * 1080 * 1.5, new FormatDescriptor(1920, 1080, "NV12", []).BytesPerFrame);
        Assert.Equal(1920 * 1080 * 2.0, Yuy2(1920, 1080).BytesPerFrame);
    }
}
