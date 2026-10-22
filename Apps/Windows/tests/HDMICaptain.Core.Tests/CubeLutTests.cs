namespace HDMICaptain.Core.Tests;

public class CubeLutTests
{
    [Fact]
    public void Parses3D()
    {
        const string text = """
            # comment
            TITLE "Tiny"
            LUT_3D_SIZE 2
            0 0 0
            1 0 0
            0 1 0
            1 1 0
            0 0 1
            1 0 1
            0 1 1
            1 1 1
            """;
        var lut = CubeLUT.Parse(text);
        Assert.Equal("Tiny", lut.Title);
        Assert.Equal(2, lut.Size);
        Assert.Equal(24, lut.Entries.Length);
        Assert.Equal(CubeLUT.Identity(2).Entries, lut.Entries);
    }

    [Fact]
    public void ParsesWindowsLineEndings() =>
        Assert.Equal(2, CubeLUT.Parse("LUT_3D_SIZE 2\r\n0 0 0\r\n1 0 0\r\n0 1 0\r\n1 1 0\r\n0 0 1\r\n1 0 1\r\n0 1 1\r\n1 1 1\r\n").Size);

    [Fact]
    public void Bakes1DInto33Cube()
    {
        var lut = CubeLUT.Parse("LUT_1D_SIZE 2\n0 0 0\n1 1 1\n");
        Assert.Equal(33, lut.Size);
        Assert.Equal(1f, lut.Entries[32 * 3], 1e-6f); // r = 1 at the first row's last entry
    }

    [Fact]
    public void Errors()
    {
        Assert.Throws<CubeLUT.ParseException>(() => CubeLUT.Parse("0 0 0\n"));
        Assert.Throws<CubeLUT.ParseException>(() => CubeLUT.Parse("LUT_3D_SIZE 2\n0 0 0\n"));
        Assert.Throws<CubeLUT.ParseException>(() => CubeLUT.Parse("LUT_3D_SIZE 99\n"));
        Assert.Throws<CubeLUT.ParseException>(() => CubeLUT.Parse("LUT_3D_SIZE 2\nfoo bar\n"));
    }

    [Fact]
    public void DeviceKeys()
    {
        Assert.Equal("usb:1DE1:F105|name:UGREEN-25854", DeviceKey.Of(0x1DE1, 0xF105, null, "UGREEN-25854"));
        Assert.Equal("usb:345F:2131|serial:20230424", DeviceKey.Of(0x345F, 0x2131, "20230424", "UGREEN HDMI Capture"));
        Assert.Equal("usb:534D:2109", DeviceKey.Of(0x534D, 0x2109, " ", null));
    }
}
