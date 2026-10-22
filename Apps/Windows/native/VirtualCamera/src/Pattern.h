// Test pattern generator for the virtual camera: SMPTE 75 % bars (same layout as
// CaptureCore's TestPattern.smpteBars) in BT.709 limited range, plus a frame counter
// drawn as 7-segment digits straight into the YUV planes.
#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace vcam
{
    enum class PixelFormat
    {
        NV12, // 8-bit Y plane, then interleaved CbCr at half width and half height
        YUY2, // packed Y0 Cb Y1 Cr
    };

    struct Ycc
    {
        uint8_t y;
        uint8_t cb;
        uint8_t cr;
    };

    // A view of one frame in memory. For NV12 the CbCr plane starts at data + pitch * height.
    struct Frame
    {
        PixelFormat format;
        uint32_t width;
        uint32_t height;
        uint8_t* data;
        ptrdiff_t pitch; // bytes per row (Y plane for NV12)
    };

    // Tightly packed frame size in bytes.
    size_t FrameBytes(PixelFormat format, uint32_t width, uint32_t height);
    // Tightly packed row pitch in bytes.
    ptrdiff_t PackedPitch(PixelFormat format, uint32_t width);

    // BT.709 limited-range conversion of RGB 0...1 (rounded like Swift's .rounded()).
    Ycc Bt709Limited(double r, double g, double b);

    // The SMPTE 75 % bars value at pixel (col, row).
    Ycc SmpteSample(uint32_t col, uint32_t row, uint32_t width, uint32_t height);

    // Fills the whole frame with SMPTE bars.
    void RenderBars(const Frame& frame);

    // Fills a rectangle (pixel coordinates, end exclusive; snapped to even values).
    void FillRect(const Frame& frame, uint32_t x0, uint32_t y0, uint32_t x1, uint32_t y1, Ycc color);

    // Layout of the counter box: 8 digits, top-left of the frame.
    struct CounterLayout
    {
        uint32_t boxX, boxY, boxW, boxH;
        uint32_t digitX0, digitY; // first digit's top-left
        uint32_t digitW, digitH, digitPitch, thickness;
    };
    constexpr int CounterDigits = 8;
    CounterLayout GetCounterLayout(uint32_t width, uint32_t height);
    // Centre pixel of a segment ('a'...'g') of digit `index` (0 = leftmost).
    void SegmentCenter(const CounterLayout& layout, int index, char segment, uint32_t* x, uint32_t* y);

    // Draws the frame number (mod 10^8) as white 7-segment digits on a black box.
    void RenderCounter(const Frame& frame, uint64_t frameNumber);

    // Reads one pixel back (for tests). NV12 chroma comes from the 2x2 block, YUY2 chroma from the pair.
    Ycc ReadPixel(const Frame& frame, uint32_t x, uint32_t y);

    // Test helpers (vcam selftest / grab).
    // Compares bar centres against reference BT.709 limited-range 75 % values (hard-coded, not
    // derived from Bt709Limited). Returns the number of mismatches; appends details to `report`.
    int CheckBars(const Frame& frame, int tolerance, std::string* report);
    // Reads the counter back from the segment centres; -1 if a digit is unreadable.
    int64_t DecodeCounter(const Frame& frame);

    // Keeps a packed copy of the bars so each frame is a row copy plus the counter.
    class PatternCache
    {
    public:
        void Prepare(PixelFormat format, uint32_t width, uint32_t height);
        bool IsPrepared(PixelFormat format, uint32_t width, uint32_t height) const;
        // Renders bars + counter into `frame`, which must match the prepared format and size.
        void Render(const Frame& frame, uint64_t frameNumber) const;

    private:
        PixelFormat format_ = PixelFormat::NV12;
        uint32_t width_ = 0;
        uint32_t height_ = 0;
        std::vector<uint8_t> base_;
    };
}
