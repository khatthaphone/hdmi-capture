#pragma once

#include <cstdint>
#include <functional>
#include <mutex>
#include <vector>

namespace hdmi {

// Wire formats a capture source can deliver. Values match VideoRenderer.PixelFormat in Kotlin.
enum class PixelFormat : int {
    NV12 = 0,  // Y plane, then interleaved CbCr at half width and height
    YUY2 = 1,  // packed Y0 Cb Y1 Cr
    I420 = 2,  // planar Y, Cb, Cr; chroma at half width and height (MJPEG 4:2:0 decode)
    I422 = 3,  // planar Y, Cb, Cr; chroma at half width (MJPEG 4:2:2 decode)
};

inline size_t frameBytes(PixelFormat format, int width, int height) {
    switch (format) {
        case PixelFormat::NV12:
        case PixelFormat::I420: return size_t(width) * height * 3 / 2;
        case PixelFormat::YUY2:
        case PixelFormat::I422: return size_t(width) * height * 2;
    }
    return 0;
}

struct Frame {
    PixelFormat format = PixelFormat::NV12;
    int width = 0;
    int height = 0;
    bool fullRange = false;
    int64_t timestampNs = 0;  // CLOCK_MONOTONIC when the frame arrived
    std::vector<uint8_t> data;

    void resize(PixelFormat f, int w, int h) {
        format = f;
        width = w;
        height = h;
        data.resize(frameBytes(f, w, h));
    }
};

// Triple buffer between one producer (USB/decoder/test pattern thread) and the render thread.
// The producer never waits; the renderer always gets the newest frame and skips stale ones.
// No allocation once the slots have their size.
class FrameRing {
public:
    // Producer: a slot that is neither the latest nor being read.
    Frame &acquire() {
        std::lock_guard<std::mutex> lock(mutex_);
        for (int i = 0; i < 3; i++) {
            if (i != latest_ && i != reading_) {
                writing_ = i;
                break;
            }
        }
        return slots_[writing_];
    }

    void publish() {
        {
            std::lock_guard<std::mutex> lock(mutex_);
            latest_ = writing_;
            fresh_ = true;
            published_++;
        }
        if (onPublish_) onPublish_();
    }

    // Consumer: the newest unseen frame, held until release().
    Frame *take() {
        std::lock_guard<std::mutex> lock(mutex_);
        if (!fresh_) return nullptr;
        fresh_ = false;
        reading_ = latest_;
        return &slots_[reading_];
    }

    void release() {
        std::lock_guard<std::mutex> lock(mutex_);
        reading_ = -1;
    }

    bool hasFresh() {
        std::lock_guard<std::mutex> lock(mutex_);
        return fresh_;
    }

    int64_t published() {
        std::lock_guard<std::mutex> lock(mutex_);
        return published_;
    }

    void setOnPublish(std::function<void()> callback) { onPublish_ = std::move(callback); }

private:
    std::mutex mutex_;
    Frame slots_[3];
    int writing_ = 0;
    int latest_ = -1;
    int reading_ = -1;
    bool fresh_ = false;
    int64_t published_ = 0;
    std::function<void()> onPublish_;
};

}  // namespace hdmi
