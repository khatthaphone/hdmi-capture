#include "Pattern.h"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>

namespace vcam
{
    namespace
    {
        uint8_t ToByte(double v)
        {
            return static_cast<uint8_t>(std::clamp(std::round(v), 0.0, 255.0));
        }

        uint8_t* ChromaPlane(const Frame& frame)
        {
            return frame.data + frame.pitch * static_cast<ptrdiff_t>(frame.height);
        }

        // Segment bits: a=0 (top), b=1 (top right), c=2 (bottom right), d=3 (bottom),
        // e=4 (bottom left), f=5 (top left), g=6 (middle).
        constexpr uint8_t DigitSegments[10] = { 0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F };

        constexpr Ycc Black = { 16, 128, 128 };
        constexpr Ycc White = { 235, 128, 128 };

        struct Rect
        {
            uint32_t x0, y0, x1, y1;
        };

        Rect SegmentRect(const CounterLayout& l, int index, int segment)
        {
            const uint32_t dx = l.digitX0 + static_cast<uint32_t>(index) * l.digitPitch;
            const uint32_t dy = l.digitY;
            const uint32_t w = l.digitW, h = l.digitH, t = l.thickness;
            const uint32_t mid = ((h - t) / 2) & ~1u;
            switch (segment)
            {
            case 0: return { dx, dy, dx + w, dy + t };
            case 1: return { dx + w - t, dy, dx + w, dy + mid + t };
            case 2: return { dx + w - t, dy + mid, dx + w, dy + h };
            case 3: return { dx, dy + h - t, dx + w, dy + h };
            case 4: return { dx, dy + mid, dx + t, dy + h };
            case 5: return { dx, dy, dx + t, dy + mid + t };
            default: return { dx, dy + mid, dx + w, dy + mid + t };
            }
        }
    }

    size_t FrameBytes(PixelFormat format, uint32_t width, uint32_t height)
    {
        const size_t pixels = static_cast<size_t>(width) * height;
        return format == PixelFormat::NV12 ? pixels * 3 / 2 : pixels * 2;
    }

    ptrdiff_t PackedPitch(PixelFormat format, uint32_t width)
    {
        return format == PixelFormat::NV12 ? static_cast<ptrdiff_t>(width) : static_cast<ptrdiff_t>(width) * 2;
    }

    Ycc Bt709Limited(double r, double g, double b)
    {
        const double luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        const double cb = (b - luma) / 1.8556;
        const double cr = (r - luma) / 1.5748;
        return { ToByte(16 + 219 * luma), ToByte(128 + 224 * cb), ToByte(128 + 224 * cr) };
    }

    Ycc SmpteSample(uint32_t col, uint32_t row, uint32_t width, uint32_t height)
    {
        // Same layout as CaptureCore's TestPattern.smpteBars.
        static const double bars[7][3] = { { 1, 1, 1 }, { 1, 1, 0 }, { 0, 1, 1 }, { 0, 1, 0 }, { 1, 0, 1 }, { 1, 0, 0 }, { 0, 0, 1 } };
        static const double reverse[7][3] = { { 0, 0, 1 }, { 0, 0, 0 }, { 1, 0, 1 }, { 0, 0, 0 }, { 0, 1, 1 }, { 0, 0, 0 }, { 1, 1, 1 } };
        const double x = static_cast<double>(col) / width;
        const double y = static_cast<double>(row) / height;
        const int seventh = std::min(6, static_cast<int>(x * 7));
        if (y < 0.67)
        {
            const double* c = bars[seventh];
            return Bt709Limited(c[0] * 0.75, c[1] * 0.75, c[2] * 0.75);
        }
        if (y < 0.75)
        {
            const double* c = reverse[seventh];
            return Bt709Limited(c[0] * 0.75, c[1] * 0.75, c[2] * 0.75);
        }
        // -I, 100 % white, +Q, black, then PLUGE (below black, black, above black).
        const double segment = x * 7;
        if (segment < 1.25) return { 16, 158, 95 };
        if (segment < 2.5) return Bt709Limited(1, 1, 1);
        if (segment < 3.75) return { 34, 155, 166 };
        if (segment < 5) return Black;
        if (segment < 5.33) return { 12, 128, 128 };
        if (segment < 5.67) return Black;
        if (segment < 6) return { 20, 128, 128 };
        return Black;
    }

    void RenderBars(const Frame& frame)
    {
        const uint32_t w = frame.width, h = frame.height;
        if (frame.format == PixelFormat::NV12)
        {
            uint8_t* uv = ChromaPlane(frame);
            for (uint32_t row = 0; row < h; row++)
            {
                uint8_t* yRow = frame.data + frame.pitch * static_cast<ptrdiff_t>(row);
                uint8_t* cRow = uv + frame.pitch * static_cast<ptrdiff_t>(row / 2);
                for (uint32_t col = 0; col < w; col++)
                {
                    const Ycc p = SmpteSample(col, row, w, h);
                    yRow[col] = p.y;
                    if ((row % 2) == 0 && (col % 2) == 0)
                    {
                        cRow[col] = p.cb;
                        cRow[col + 1] = p.cr;
                    }
                }
            }
        }
        else
        {
            for (uint32_t row = 0; row < h; row++)
            {
                uint8_t* line = frame.data + frame.pitch * static_cast<ptrdiff_t>(row);
                for (uint32_t col = 0; col < w; col++)
                {
                    const Ycc p = SmpteSample(col, row, w, h);
                    line[col * 2] = p.y;
                    if ((col % 2) == 0)
                    {
                        line[col * 2 + 1] = p.cb;
                        line[col * 2 + 3] = p.cr;
                    }
                }
            }
        }
    }

    void FillRect(const Frame& frame, uint32_t x0, uint32_t y0, uint32_t x1, uint32_t y1, Ycc color)
    {
        x0 &= ~1u;
        y0 &= ~1u;
        x1 = std::min((x1 + 1) & ~1u, frame.width & ~1u);
        y1 = std::min((y1 + 1) & ~1u, frame.height & ~1u);
        if (x0 >= x1 || y0 >= y1)
            return;

        if (frame.format == PixelFormat::NV12)
        {
            for (uint32_t row = y0; row < y1; row++)
                std::memset(frame.data + frame.pitch * static_cast<ptrdiff_t>(row) + x0, color.y, x1 - x0);
            uint8_t* uv = ChromaPlane(frame);
            for (uint32_t row = y0 / 2; row < y1 / 2; row++)
            {
                uint8_t* c = uv + frame.pitch * static_cast<ptrdiff_t>(row);
                for (uint32_t col = x0; col < x1; col += 2)
                {
                    c[col] = color.cb;
                    c[col + 1] = color.cr;
                }
            }
        }
        else
        {
            for (uint32_t row = y0; row < y1; row++)
            {
                uint8_t* line = frame.data + frame.pitch * static_cast<ptrdiff_t>(row);
                for (uint32_t col = x0; col < x1; col += 2)
                {
                    uint8_t* p = line + col * 2;
                    p[0] = color.y;
                    p[1] = color.cb;
                    p[2] = color.y;
                    p[3] = color.cr;
                }
            }
        }
    }

    CounterLayout GetCounterLayout(uint32_t width, uint32_t height)
    {
        CounterLayout l{};
        l.digitH = std::max(10u, (height / 10) & ~1u);
        l.digitW = std::max(6u, (l.digitH / 2) & ~1u);
        l.thickness = std::max(2u, (l.digitH / 10) & ~1u);
        l.digitPitch = l.digitW + l.thickness * 2;
        const uint32_t margin = l.thickness * 2;
        l.boxX = (width / 32) & ~1u;
        l.boxY = (height / 24) & ~1u;
        l.boxW = margin * 2 + CounterDigits * l.digitPitch - (l.digitPitch - l.digitW);
        l.boxH = margin * 2 + l.digitH;
        l.digitX0 = l.boxX + margin;
        l.digitY = l.boxY + margin;
        return l;
    }

    void SegmentCenter(const CounterLayout& layout, int index, char segment, uint32_t* x, uint32_t* y)
    {
        const Rect r = SegmentRect(layout, index, segment - 'a');
        const uint32_t t = layout.thickness;
        // Vertical segments overlap the horizontal ones at their ends; use the middle of
        // their own span, and the middle of the thickness band for horizontal ones.
        *x = (r.x1 - r.x0) == t ? r.x0 + t / 2 : (r.x0 + r.x1) / 2;
        *y = (r.y1 - r.y0) == t ? r.y0 + t / 2 : (r.y0 + r.y1) / 2;
    }

    void RenderCounter(const Frame& frame, uint64_t frameNumber)
    {
        const CounterLayout l = GetCounterLayout(frame.width, frame.height);
        if (l.boxX + l.boxW > frame.width || l.boxY + l.boxH > frame.height)
            return;
        FillRect(frame, l.boxX, l.boxY, l.boxX + l.boxW, l.boxY + l.boxH, Black);
        uint64_t value = frameNumber;
        for (int i = CounterDigits - 1; i >= 0; i--)
        {
            const uint8_t mask = DigitSegments[value % 10];
            value /= 10;
            for (int s = 0; s < 7; s++)
            {
                if ((mask >> s) & 1)
                {
                    const Rect r = SegmentRect(l, i, s);
                    FillRect(frame, r.x0, r.y0, r.x1, r.y1, White);
                }
            }
        }
    }

    Ycc ReadPixel(const Frame& frame, uint32_t x, uint32_t y)
    {
        if (frame.format == PixelFormat::NV12)
        {
            const uint8_t* c = ChromaPlane(frame) + frame.pitch * static_cast<ptrdiff_t>(y / 2) + (x & ~1u);
            return { frame.data[frame.pitch * static_cast<ptrdiff_t>(y) + x], c[0], c[1] };
        }
        const uint8_t* pair = frame.data + frame.pitch * static_cast<ptrdiff_t>(y) + (x / 2) * 4;
        return { pair[(x & 1) * 2], pair[1], pair[3] };
    }

    int CheckBars(const Frame& frame, int tolerance, std::string* report)
    {
        struct Probe
        {
            double x, y;
            const char* name;
            Ycc expected;
        };
        // SMPTE RP 219 / BT.709 limited-range 75 % bar values.
        static const Probe probes[] = {
            { 0.5 / 7, 0.5, "75% white", { 180, 128, 128 } },
            { 1.5 / 7, 0.5, "75% yellow", { 168, 44, 136 } },
            { 2.5 / 7, 0.5, "75% cyan", { 145, 147, 44 } },
            { 3.5 / 7, 0.5, "75% green", { 133, 63, 52 } },
            { 4.5 / 7, 0.5, "75% magenta", { 63, 193, 204 } },
            { 5.5 / 7, 0.5, "75% red", { 51, 109, 212 } },
            { 6.5 / 7, 0.5, "75% blue", { 28, 212, 120 } },
            { 0.5 / 7, 0.71, "reverse blue", { 28, 212, 120 } },
            { 1.5 / 7, 0.71, "reverse black", { 16, 128, 128 } },
            { 0.5 / 7, 0.9, "-I", { 16, 158, 95 } },
            { 1.8 / 7, 0.9, "100% white", { 235, 128, 128 } },
        };
        int failures = 0;
        for (const Probe& p : probes)
        {
            const uint32_t x = static_cast<uint32_t>(p.x * frame.width);
            const uint32_t y = static_cast<uint32_t>(p.y * frame.height);
            const Ycc got = ReadPixel(frame, x, y);
            const bool ok = std::abs(got.y - p.expected.y) <= tolerance && std::abs(got.cb - p.expected.cb) <= tolerance &&
                std::abs(got.cr - p.expected.cr) <= tolerance;
            if (!ok)
            {
                failures++;
                if (report)
                {
                    char line[160];
                    std::snprintf(line, sizeof line, "  %s at (%u,%u): got Y=%u Cb=%u Cr=%u, expected %u/%u/%u\n", p.name, x, y,
                        got.y, got.cb, got.cr, p.expected.y, p.expected.cb, p.expected.cr);
                    report->append(line);
                }
            }
        }
        return failures;
    }

    int64_t DecodeCounter(const Frame& frame)
    {
        const CounterLayout l = GetCounterLayout(frame.width, frame.height);
        int64_t value = 0;
        for (int i = 0; i < CounterDigits; i++)
        {
            uint8_t mask = 0;
            for (int s = 0; s < 7; s++)
            {
                uint32_t x = 0, y = 0;
                SegmentCenter(l, i, static_cast<char>('a' + s), &x, &y);
                if (ReadPixel(frame, x, y).y > 126)
                    mask |= static_cast<uint8_t>(1u << s);
            }
            int digit = -1;
            for (int d = 0; d < 10; d++)
            {
                if (DigitSegments[d] == mask)
                    digit = d;
            }
            if (digit < 0)
                return -1;
            value = value * 10 + digit;
        }
        return value;
    }

    void PatternCache::Prepare(PixelFormat format, uint32_t width, uint32_t height)
    {
        if (IsPrepared(format, width, height))
            return;
        format_ = format;
        width_ = width;
        height_ = height;
        base_.assign(FrameBytes(format, width, height), 0);
        RenderBars({ format, width, height, base_.data(), PackedPitch(format, width) });
    }

    bool PatternCache::IsPrepared(PixelFormat format, uint32_t width, uint32_t height) const
    {
        return !base_.empty() && format_ == format && width_ == width && height_ == height;
    }

    void PatternCache::Render(const Frame& frame, uint64_t frameNumber) const
    {
        const ptrdiff_t packed = PackedPitch(format_, width_);
        const uint32_t rows = format_ == PixelFormat::NV12 ? height_ + height_ / 2 : height_;
        for (uint32_t row = 0; row < rows; row++)
            std::memcpy(frame.data + frame.pitch * static_cast<ptrdiff_t>(row), base_.data() + packed * static_cast<ptrdiff_t>(row), static_cast<size_t>(packed));
        RenderCounter(frame, frameNumber);
    }
}
