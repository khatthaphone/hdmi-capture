using System.Text.Json;

namespace HDMICaptain.Core.Tests;

public class SignalAnalyzerTests
{
    public static TheoryData<string> ClassifyCases() => Names("signal-classify.json");

    public static TheoryData<string> MonitorCases() => Names("signal-monitor.json");

    private static TheoryData<string> Names(string file)
    {
        using var doc = Fixtures.Load(file);
        var data = new TheoryData<string>();
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
            data.Add(c.GetProperty("name").GetString()!);
        return data;
    }

    private static SignalContent Content(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String) return e.GetString() == "black" ? new SignalContent.Black() : new SignalContent.Picture();
        var rgb = e.GetProperty("solid").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        return new SignalContent.Solid(rgb[0], rgb[1], rgb[2]);
    }

    [Theory]
    [MemberData(nameof(ClassifyCases))]
    public void SharedClassifyFixture(string name)
    {
        using var doc = Fixtures.Load("signal-classify.json");
        var c = doc.RootElement.GetProperty("cases").EnumerateArray().Single(e => e.GetProperty("name").GetString() == name);
        var s = c.GetProperty("stats");
        double D(string k) => s.GetProperty(k).GetDouble();
        var stats = new SignalAnalyzer.Stats(D("meanY"), 0, D("meanCb"), D("meanCr"), 0, D("rangeY"), D("rangeChroma"));

        var got = SignalAnalyzer.Classify(stats, c.GetProperty("fullRange").GetBoolean());

        var want = Content(c.GetProperty("expect"));
        if (want is SignalContent.Solid w)
        {
            var solid = Assert.IsType<SignalContent.Solid>(got);
            Assert.InRange(solid.Red, w.Red - 1, w.Red + 1);
            Assert.InRange(solid.Green, w.Green - 1, w.Green + 1);
            Assert.InRange(solid.Blue, w.Blue - 1, w.Blue + 1);
        }
        else Assert.Equal(want, got);
    }

    [Theory]
    [MemberData(nameof(MonitorCases))]
    public void SharedMonitorFixture(string name)
    {
        using var doc = Fixtures.Load("signal-monitor.json");
        var c = doc.RootElement.GetProperty("cases").EnumerateArray().Single(e => e.GetProperty("name").GetString() == name);
        var debouncer = new SignalDebouncer(c.GetProperty("persistence").GetDouble());
        foreach (var step in c.GetProperty("steps").EnumerateArray())
        {
            var t = step.GetProperty("t").GetDouble();
            debouncer.Observe(Content(step.GetProperty("content")), t);
            Assert.True(Content(step.GetProperty("expect")).IsNoSignal == debouncer.Current.IsNoSignal, $"{name} @ {t}");
        }
    }

    [Fact]
    public void StatsFromSamples()
    {
        var flat = Enumerable.Repeat(16, 100).ToArray();
        var neutral = Enumerable.Repeat(128, 100).ToArray();
        var stats = SignalAnalyzer.ComputeStats(flat, neutral, neutral);
        Assert.Equal(16.0, stats.MeanY, 9);
        Assert.Equal(0.0, stats.RangeY, 9);
        Assert.Equal(new SignalContent.Black(), SignalAnalyzer.Classify(stats, fullRange: false));
        var text = Enumerable.Range(0, 100).Select(i => i % 10 == 0 ? 200 : 16).ToArray();
        Assert.Equal(new SignalContent.Picture(), SignalAnalyzer.Classify(SignalAnalyzer.ComputeStats(text, neutral, neutral), fullRange: false));
    }
}
