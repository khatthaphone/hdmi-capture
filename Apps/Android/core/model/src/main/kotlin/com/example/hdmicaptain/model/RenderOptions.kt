package com.example.hdmicaptain.model

// Picture options, same cases as CaptureCore's RenderOptions. Ordinals are passed to the native
// renderer, so keep the order in sync with core/render/src/main/cpp (geometry.h, renderer.h).

enum class ScaleMode { Fit, Fill, Stretch, Integer }

enum class FilterMode { Linear, Nearest }

/** Auto trusts the source's full-range flag. */
enum class ColorRange { Auto, Limited, Full }

data class RenderOptions(
    val scale: ScaleMode = ScaleMode.Fit,
    val filter: FilterMode = FilterMode.Linear,
    val colorRange: ColorRange = ColorRange.Auto,
)

/** Generated reference images (CaptureCore's TestPattern); ordinals match test_pattern.h. */
enum class TestPattern(val title: String, val key: String) {
    SmpteBars("SMPTE Color Bars (75 %)", "smpteBars"),
    FullBars("Color Bars (100 %)", "fullBars"),
    Grid("Grid", "grid"),
    GrayRamp("Gray Ramp", "grayRamp");

    companion object {
        /** From the `testPattern` launch extra (same names as the Apple `-testPattern` argument). */
        fun fromKey(key: String?): TestPattern? = entries.firstOrNull { it.key == key }
    }
}
