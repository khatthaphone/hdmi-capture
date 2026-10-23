#include "test_pattern.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstring>
#include <ctime>

namespace hdmi {
namespace {

struct YCC {
    uint8_t y, cb, cr;
};

// BT.709 limited-range conversion of RGB 0…1 (CaptureCore's TestPattern.ycbcr).
YCC ycbcr(double r, double g, double b) {
    double luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;
    double cb = (b - luma) / 1.8556;
    double cr = (r - luma) / 1.5748;
    auto byte = [](double v) { return uint8_t(std::clamp(std::round(v), 0.0, 255.0)); };
    return {byte(16 + 219 * luma), byte(128 + 224 * cb), byte(128 + 224 * cr)};
}

const double kBars[8][3] = {{1, 1, 1}, {1, 1, 0}, {0, 1, 1}, {0, 1, 0}, {1, 0, 1}, {1, 0, 0}, {0, 0, 1}, {0, 0, 0}};

YCC sample(Pattern pattern, double x, double y, int col, int row, int width, int height) {
    switch (pattern) {
        case Pattern::FullBars: {
            const double *c = kBars[std::min(7, int(x * 8))];
            return ycbcr(c[0], c[1], c[2]);
        }
        case Pattern::SmpteBars: {
            int seventh = std::min(6, int(x * 7));
            if (y < 0.67) {
                const double *c = kBars[seventh];
                return ycbcr(c[0] * 0.75, c[1] * 0.75, c[2] * 0.75);
            }
            if (y < 0.75) {
                // Reverse blue bars: blue, black, magenta, black, cyan, black, white (75 %).
                const double reverse[7][3] = {{0, 0, .75}, {0, 0, 0}, {.75, 0, .75}, {0, 0, 0}, {0, .75, .75}, {0, 0, 0}, {.75, .75, .75}};
                return ycbcr(reverse[seventh][0], reverse[seventh][1], reverse[seventh][2]);
            }
            // -I, 100 % white, +Q, black, then PLUGE (below black, black, above black).
            double segment = x * 7;
            if (segment < 1.25) return {16, 158, 95};
            if (segment < 2.5) return ycbcr(1, 1, 1);
            if (segment < 3.75) return {34, 155, 166};
            if (segment < 5) return {16, 128, 128};
            if (segment < 5.33) return {12, 128, 128};
            if (segment < 5.67) return {16, 128, 128};
            if (segment < 6) return {20, 128, 128};
            return {16, 128, 128};
        }
        case Pattern::Grid: {
            int cell = std::max(8, height / 18);
            bool onLine = col % cell == 0 || row % cell == 0 || col == width - 1 || row == height - 1;
            bool center = std::abs(col - width / 2) <= 1 || std::abs(row - height / 2) <= 1;
            return onLine || center ? YCC{235, 128, 128} : YCC{16, 128, 128};
        }
        case Pattern::GrayRamp: {
            double level = y < 0.5 ? x : double(std::min(10, int(x * 11))) / 10;
            return ycbcr(level, level, level);
        }
    }
    return {16, 128, 128};
}

int64_t nowNs() {
    timespec ts{};
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return int64_t(ts.tv_sec) * 1000000000 + ts.tv_nsec;
}

}  // namespace

void drawPattern(Pattern pattern, Frame &frame) {
    int w = frame.width, h = frame.height;
    uint8_t *yPlane = frame.data.data();
    uint8_t *cPlane = yPlane + size_t(w) * h;
    for (int row = 0; row < h; row++) {
        for (int col = 0; col < w; col++) {
            YCC v = sample(pattern, double(col) / w, double(row) / h, col, row, w, h);
            yPlane[size_t(row) * w + col] = v.y;
            if (row % 2 == 0 && col % 2 == 0) {
                cPlane[size_t(row / 2) * w + col] = v.cb;
                cPlane[size_t(row / 2) * w + col + 1] = v.cr;
            }
        }
    }
}

TestPatternSource::TestPatternSource(FrameRing &ring, Pattern pattern, int width, int height, int fps)
    : ring_(ring), fps_(std::max(1, fps)) {
    image_.resize(PixelFormat::NV12, width, height);
    drawPattern(pattern, image_);
    thread_ = std::thread([this] { run(); });
}

TestPatternSource::~TestPatternSource() {
    running_ = false;
    if (thread_.joinable()) thread_.join();
}

void TestPatternSource::run() {
    auto interval = std::chrono::nanoseconds(1000000000LL / fps_);
    auto next = std::chrono::steady_clock::now();
    while (running_) {
        Frame &slot = ring_.acquire();
        if (slot.data.size() != image_.data.size()) slot.resize(image_.format, image_.width, image_.height);
        std::memcpy(slot.data.data(), image_.data.data(), image_.data.size());
        slot.format = image_.format;
        slot.width = image_.width;
        slot.height = image_.height;
        slot.fullRange = false;
        slot.timestampNs = nowNs();
        ring_.publish();
        next += interval;
        std::this_thread::sleep_until(next);
    }
}

}  // namespace hdmi
