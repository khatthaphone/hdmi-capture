using System.Text.Json;

namespace HDMICaptain.Core.Tests;

public class RenderGeometryTests
{
    public static TheoryData<string> Cases()
    {
        using var doc = Fixtures.Load("render-geometry.json");
        var data = new TheoryData<string>();
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
            data.Add(c.GetProperty("name").GetString()!);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesSharedFixture(string name)
    {
        using var doc = Fixtures.Load("render-geometry.json");
        var c = doc.RootElement.GetProperty("cases").EnumerateArray().Single(e => e.GetProperty("name").GetString() == name);
        double[] Arr(string key) => c.GetProperty(key).EnumerateArray().Select(e => e.GetDouble()).ToArray();
        var source = Arr("source");
        var drawable = Arr("drawable");
        var mode = Enum.Parse<ScaleMode>(c.GetProperty("mode").GetString()!, ignoreCase: true);

        var g = RenderGeometry.Compute(source[0], source[1], drawable[0], drawable[1], mode);

        AssertRect(Arr("viewport"), g.Viewport);
        AssertRect(Arr("sourceRect"), g.SourceRect);
    }

    private static void AssertRect(double[] expected, Rect actual)
    {
        Assert.Equal(expected[0], actual.X, 4);
        Assert.Equal(expected[1], actual.Y, 4);
        Assert.Equal(expected[2], actual.Width, 4);
        Assert.Equal(expected[3], actual.Height, 4);
    }
}
