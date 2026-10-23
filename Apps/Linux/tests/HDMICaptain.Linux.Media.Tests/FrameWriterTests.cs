using HDMICaptain.Linux.Media.Render;
using HDMICaptain.Linux.Media.Video;

namespace HDMICaptain.Linux.Media.Tests;

/// <summary>Captured buffers through <see cref="FrameWriter"/> into the renderer, checked on pass 1 (headless GL).</summary>
public unsafe class FrameWriterTests
{
    [Fact]
    public void MjpegDecodesAsFullRangeBt601()
    {
        // A JFIF 4:2:2 1280×720 frame of (191, 0, 0) must come back as the same RGB, not BT.709-shifted.
        var bgra = new byte[1280 * 720 * 4];
        for (var i = 0; i < bgra.Length; i += 4) (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]) = (0, 0, 191, 255);
        var jpeg = TurboJpeg.CompressBgra(bgra, 1280, 720, TurboJpeg.Samp422);

        using var gl = new RendererPixelTests.HeadlessGl(1280, 720);
        using var r = new GlesRenderer();
        using var writer = new FrameWriter(r);
        Assert.True(r.GlInit());
        fixed (byte* p = jpeg) Assert.True(writer.Write("MJPG", p, jpeg.Length, 1280, 720, 0, GlesRenderer.MonotonicNs), writer.LastError);
        gl.Bind();
        Assert.True(r.GlRender(1280, 720));
        RendererPixelTests.AssertPixel(r.GlReadPass1()!.Value.Rgba, 1280, 640, 360, 191, 0, 0);
        r.GlRelease();
    }

    [Fact]
    public void RejectsBuffersThatAreNotJpegs()
    {
        using var r = new GlesRenderer();
        using var writer = new FrameWriter(r);
        var junk = new byte[] { 0x02, 0x82, 0x00, 0x00, 0x00 };
        fixed (byte* p = junk) Assert.False(writer.Write("MJPG", p, junk.Length, 1920, 1080, 0, 0));
        Assert.Equal(1, writer.Rejected);
    }

    [Fact]
    public void PaddedNv12RowsAreUnpadded()
    {
        // 64×32 NV12 with 128-byte rows: Y = 235 (white) in the picture, 0 in the padding.
        const int w = 64, h = 32, pitch = 128;
        var buffer = new byte[pitch * h * 3 / 2];
        for (var y = 0; y < h; y++) buffer.AsSpan(y * pitch, w).Fill(235);
        buffer.AsSpan(pitch * h).Fill(128);

        using var gl = new RendererPixelTests.HeadlessGl(w, h);
        using var r = new GlesRenderer();
        using var writer = new FrameWriter(r);
        Assert.True(r.GlInit());
        fixed (byte* p = buffer) Assert.True(writer.Write("NV12", p, buffer.Length, w, h, pitch, 1));
        gl.Bind();
        r.GlRender(w, h);
        var shot = r.GlReadPass1()!.Value.Rgba;
        RendererPixelTests.AssertPixel(shot, w, w - 1, h - 1, 255, 255, 255);
        RendererPixelTests.AssertPixel(shot, w, 0, h / 2, 255, 255, 255);
        r.GlRelease();
    }
}
