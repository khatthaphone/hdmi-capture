namespace HDMICaptain.Core.Tests;

public class AudioFormatFixTests
{
    public static TheoryData<string> Cases()
    {
        using var doc = Fixtures.Load("ms2109-audio.json");
        var data = new TheoryData<string>();
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
            data.Add(c.GetProperty("name").GetString()!);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesSharedFixture(string name)
    {
        using var doc = Fixtures.Load("ms2109-audio.json");
        var c = doc.RootElement.GetProperty("cases").EnumerateArray().Single(e => e.GetProperty("name").GetString() == name);
        CaptureAudioFormat Read(string key) => new(
            c.GetProperty(key).GetProperty("sampleRate").GetInt32(),
            c.GetProperty(key).GetProperty("channels").GetInt32(),
            c.GetProperty(key).GetProperty("bitsPerSample").GetInt32());
        var expect = c.GetProperty("expect");

        var (format, reinterpreted) = AudioFormatFix.Apply(Read("format"));

        Assert.Equal(expect.GetProperty("reinterpret").GetBoolean(), reinterpreted);
        Assert.Equal(new CaptureAudioFormat(
            expect.GetProperty("sampleRate").GetInt32(),
            expect.GetProperty("channels").GetInt32(),
            expect.GetProperty("bitsPerSample").GetInt32()), format);
    }

    [Fact]
    public void ReinterpretationKeepsTheByteRate()
    {
        static int BytesPerSecond(CaptureAudioFormat f) => f.SampleRate * f.Channels * f.BitsPerSample / 8;
        Assert.Equal(BytesPerSecond(AudioFormatFix.Ms2109Reported), BytesPerSecond(AudioFormatFix.Ms2109Actual));
    }
}
