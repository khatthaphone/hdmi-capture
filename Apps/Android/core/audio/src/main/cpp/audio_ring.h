#pragma once

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstring>
#include <vector>

namespace hdmi {

// Port of CaptureCore's AudioRingBuffer: single producer / single consumer ring of interleaved
// stereo floats, lock- and allocation-free on both sides (the consumer is the real-time callback).
class AudioRing {
public:
    explicit AudioRing(int capacityFrames) {
        int size = 1;
        while (size < capacityFrames) size <<= 1;
        capacity_ = size;
        mask_ = size - 1;
        storage_.assign(size_t(size) * 2, 0.f);
    }

    int available() const { return int(written_.load(std::memory_order_acquire) - consumed_.load(std::memory_order_acquire)); }

    int write(const float *source, int frames) {
        int64_t w = written_.load(std::memory_order_relaxed), r = consumed_.load(std::memory_order_acquire);
        int count = std::min<int64_t>(frames, capacity_ - (w - r));
        if (count <= 0) return 0;
        int start = int(w & mask_), first = std::min(count, capacity_ - start);
        std::memcpy(&storage_[size_t(start) * 2], source, sizeof(float) * first * 2);
        if (count > first) std::memcpy(&storage_[0], source + first * 2, sizeof(float) * (count - first) * 2);
        written_.store(w + count, std::memory_order_release);
        return count;
    }

    int read(float *destination, int frames) {
        int64_t r = consumed_.load(std::memory_order_relaxed), w = written_.load(std::memory_order_acquire);
        int count = std::min<int64_t>(frames, w - r);
        if (count <= 0) return 0;
        int start = int(r & mask_), first = std::min(count, capacity_ - start);
        std::memcpy(destination, &storage_[size_t(start) * 2], sizeof(float) * first * 2);
        if (count > first) std::memcpy(destination + first * 2, &storage_[0], sizeof(float) * (count - first) * 2);
        consumed_.store(r + count, std::memory_order_release);
        return count;
    }

    int discard(int frames) {
        int64_t r = consumed_.load(std::memory_order_relaxed), w = written_.load(std::memory_order_acquire);
        int count = std::min<int64_t>(frames, w - r);
        if (count <= 0) return 0;
        consumed_.store(r + count, std::memory_order_release);
        return count;
    }

private:
    int capacity_ = 0;
    int mask_ = 0;
    std::vector<float> storage_;
    std::atomic<int64_t> written_{0};
    std::atomic<int64_t> consumed_{0};
};

// Port of CaptureCore's DriftController (same constants): keeps buffered audio near the target
// (user delay + jitter margin) by consuming a frame more or less per callback.
class DriftController {
public:
    struct Plan {
        bool silence = true;
        int discard = 0;
        int consume = 0;
    };

    int targetFrames = 0;
    const int toleranceFrames = 240;
    const int jumpFrames = 4800;

    static int target(int delayFrames, int chunkFrames, int quantumFrames, int toleranceFrames = 240) {
        return delayFrames + chunkFrames / 2 + quantumFrames + toleranceFrames + 240;
    }

    Plan plan(int available, int request) {
        if (priming_) {
            if (available < std::max(targetFrames, request)) return {};
            priming_ = false;
            averageFill_ = available;
            carry_ = 0;
        }
        averageFill_ += (available - averageFill_) * 0.02;
        int discard = 0;
        double error = averageFill_ - targetFrames;
        if (error > jumpFrames) {
            discard = std::max(0, std::min(available - request, int(error)));
            averageFill_ -= discard;
        } else if (error < -jumpFrames) {
            priming_ = true;
            return {};
        }
        carry_ += std::clamp((averageFill_ - targetFrames) * 0.01, -3.0, 3.0);
        int whole = int(carry_);
        carry_ -= whole;
        int consume = std::max(1, request + whole);
        if (available - discard < consume) {
            priming_ = true;
            return {};
        }
        return {false, discard, consume};
    }

private:
    bool priming_ = true;
    double averageFill_ = 0;
    double carry_ = 0;
};

// Values match ChannelMode in Kotlin and CaptureCore.
enum class ChannelMode : int { Auto = 0, Normal, Swap, LeftToBoth, RightToBoth, Mono };

inline void applyChannelMode(ChannelMode mode, float *samples, int frames) {
    for (int i = 0; i < frames; i++) {
        float l = samples[2 * i], r = samples[2 * i + 1];
        switch (mode) {
            case ChannelMode::Auto:
            case ChannelMode::Normal: return;
            case ChannelMode::Swap: samples[2 * i] = r; samples[2 * i + 1] = l; break;
            case ChannelMode::LeftToBoth: samples[2 * i + 1] = l; break;
            case ChannelMode::RightToBoth: samples[2 * i] = r; break;
            case ChannelMode::Mono: samples[2 * i] = samples[2 * i + 1] = (l + r) * 0.5f; break;
        }
    }
}

// Linear-interpolates `frames` interleaved frames onto `outFrames` interleaved frames.
inline void stretch(const float *source, int frames, float *out, int outFrames) {
    if (frames <= 0) {
        std::fill(out, out + outFrames * 2, 0.f);
        return;
    }
    if (frames == outFrames) {
        std::memcpy(out, source, sizeof(float) * frames * 2);
        return;
    }
    double step = outFrames > 1 ? double(frames - 1) / (outFrames - 1) : 0;
    for (int i = 0; i < outFrames; i++) {
        double position = i * step;
        int i0 = int(position), i1 = std::min(i0 + 1, frames - 1);
        float t = float(position - i0);
        out[2 * i] = source[2 * i0] * (1 - t) + source[2 * i1] * t;
        out[2 * i + 1] = source[2 * i0 + 1] * (1 - t) + source[2 * i1 + 1] * t;
    }
}

}  // namespace hdmi
