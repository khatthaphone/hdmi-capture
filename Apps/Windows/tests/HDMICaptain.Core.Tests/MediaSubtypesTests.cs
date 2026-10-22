using System.Text.Json;

namespace HDMICaptain.Core.Tests;

public class MediaSubtypesTests
{
    [Fact]
    public void GuidsAreTheDocumentedMediaFoundationConstants()
    {
        // MFVideoFormat_MJPG / YUY2 / NV12 from mfapi.h.
        Assert.Equal(new Guid("47504A4D-0000-0010-8000-00AA00389B71"), MediaSubtypes.Mjpg);
        Assert.Equal(new Guid("32595559-0000-0010-8000-00AA00389B71"), MediaSubtypes.Yuy2);
        Assert.Equal(new Guid("3231564E-0000-0010-8000-00AA00389B71"), MediaSubtypes.Nv12);
    }

    [Theory]
    [InlineData("47504A4D-0000-0010-8000-00AA00389B71", "MJPG")]
    [InlineData("32595559-0000-0010-8000-00AA00389B71", "YUY2")]
    [InlineData("3231564E-0000-0010-8000-00AA00389B71", "NV12")]
    public void SubtypeToSelectorName(string guid, string expected) =>
        Assert.Equal(expected, MediaSubtypes.ToSelectorName(new Guid(guid)));

    [Fact]
    public void NonFourCcSubtypesAreNull()
    {
        Assert.Null(MediaSubtypes.ToFourCC(Guid.Empty));
        Assert.Null(MediaSubtypes.ToFourCC(new Guid("73646976-0000-0010-8000-00AA00389B72")));
    }

    [Theory]
    [InlineData("420v", "NV12")]
    [InlineData("yuvs", "YUY2")]
    [InlineData("dmb1", "MJPG")]
    [InlineData("MJPG", "MJPG")]
    [InlineData("YUY2", "YUY2")]
    [InlineData("NV12", "NV12")]
    public void AppleNamesMapToUvcNames(string input, string expected)
    {
        Assert.Equal(expected, MediaSubtypes.Canonicalize(input));
        Assert.Equal(MediaSubtypes.FromFourCC(expected), MediaSubtypes.ToSubtype(input));
    }

    [Fact]
    public void UnknownCodeHasNoSubtype() => Assert.Null(MediaSubtypes.ToSubtype("zzzz"));

    [Fact]
    public void EveryFourCcInTheFixturesRoundTrips()
    {
        var codes = new HashSet<string>();
        foreach (var file in new[] { "format-selection.json", "format-bandwidth.json" })
        {
            using var doc = Fixtures.Load(file);
            Collect(doc.RootElement, codes);
        }
        Assert.NotEmpty(codes);
        foreach (var cc in codes)
        {
            var subtype = MediaSubtypes.ToSubtype(cc);
            Assert.NotNull(subtype);
            Assert.Equal(MediaSubtypes.Canonicalize(cc), MediaSubtypes.ToSelectorName(subtype.Value));
        }
    }

    private static void Collect(JsonElement e, HashSet<string> codes)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name == "cc") codes.Add(p.Value.GetString()!);
                    Collect(p.Value, codes);
                }
                break;
            case JsonValueKind.Array:
                foreach (var x in e.EnumerateArray()) Collect(x, codes);
                break;
        }
    }
}
