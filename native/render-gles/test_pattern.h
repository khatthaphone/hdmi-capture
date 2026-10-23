#pragma once

#include <atomic>
#include <thread>

#include "frame.h"

namespace hdmi {

// Values match TestPattern in Kotlin and CaptureCore's TestPattern (same images).
enum class Pattern : int { SmpteBars = 0, FullBars = 1, Grid = 2, GrayRamp = 3 };

// Fills an NV12 limited-range BT.709 frame with a reference pattern.
void drawPattern(Pattern pattern, Frame &frame);

// A capture source that publishes a generated pattern at a fixed rate, through the same ring a
// USB card uses: it exercises the whole display path on the emulator and in CI.
class TestPatternSource {
public:
    TestPatternSource(FrameRing &ring, Pattern pattern, int width, int height, int fps);
    ~TestPatternSource();

private:
    void run();

    FrameRing &ring_;
    Frame image_;
    int fps_;
    std::atomic<bool> running_{true};
    std::thread thread_;
};

}  // namespace hdmi
