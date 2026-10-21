#pragma once

#include <algorithm>
#include <cmath>

namespace hdmi {

// Values match ScaleMode in Kotlin and CaptureCore.
enum class ScaleMode : int { Fit = 0, Fill = 1, Stretch = 2, Integer = 3 };

struct Rect {
    float x = 0, y = 0, w = 0, h = 0;
};

// Port of CaptureCore's RenderGeometry: where the picture goes in the drawable (pixels, top-left
// origin) and which part of the source is visible (normalized texture coordinates).
struct RenderGeometry {
    Rect viewport;
    Rect source{0, 0, 1, 1};

    static RenderGeometry compute(float srcW, float srcH, float dstW, float dstH, ScaleMode mode) {
        RenderGeometry g;
        g.viewport = {0, 0, dstW, dstH};
        if (srcW <= 0 || srcH <= 0 || dstW <= 0 || dstH <= 0 || mode == ScaleMode::Stretch) return g;
        float sx = dstW / srcW, sy = dstH / srcH, scale;
        switch (mode) {
            case ScaleMode::Fill: scale = std::max(sx, sy); break;
            case ScaleMode::Integer: {
                float whole = std::floor(std::min(sx, sy));
                scale = whole >= 1 ? whole : std::min(sx, sy);
                break;
            }
            default: scale = std::min(sx, sy); break;
        }
        // Whole pixels, so float error can't push the origin to -1 and crop a column.
        float w = std::round(srcW * scale), h = std::round(srcH * scale);
        Rect rect{std::floor((dstW - w) / 2), std::floor((dstH - h) / 2), w, h};
        float x0 = std::max(rect.x, 0.f), y0 = std::max(rect.y, 0.f);
        float x1 = std::min(rect.x + rect.w, dstW), y1 = std::min(rect.y + rect.h, dstH);
        g.viewport = {x0, y0, x1 - x0, y1 - y0};
        g.source = {(x0 - rect.x) / rect.w, (y0 - rect.y) / rect.h, (x1 - x0) / rect.w, (y1 - y0) / rect.h};
        return g;
    }
};

}  // namespace hdmi
