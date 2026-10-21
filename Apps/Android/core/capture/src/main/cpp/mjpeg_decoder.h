#pragma once

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <mutex>
#include <thread>
#include <vector>

#include "frame.h"

namespace hdmi {

struct MjpegStats {
    int64_t decoded = 0;
    int64_t dropped = 0;  // replaced by a newer frame before a decoder was free, or finished out of order
    int64_t failed = 0;   // corrupt frames (missed USB packets) or unsupported subsampling
    int64_t avgDecodeNs = 0;
};

// MJPEG → planar YCbCr (I422/I420) with libjpeg-turbo on two threads, so 1080p60 holds on phones
// whose single core can't decode a frame in 16 ms. Colour conversion stays on the GPU.
// The newest frame always wins: a frame waiting for a decoder is replaced, never queued, and a
// frame that finishes after a newer one is dropped, so latency never builds up.
class MjpegDecoder {
public:
    explicit MjpegDecoder(int threads = 2);
    ~MjpegDecoder();

    void setRing(FrameRing *ring);
    // Called on the USB thread: copies the compressed frame (reusing the buffer's capacity).
    void submit(const uint8_t *jpeg, size_t size, int64_t timestampNs);
    MjpegStats stats();

private:
    struct Job {
        std::vector<uint8_t> jpeg;
        int64_t timestampNs = 0;
        uint64_t sequence = 0;
    };

    void run();
    void publish(Frame &frame, uint64_t sequence, int64_t timestampNs);

    std::mutex mutex_;
    std::condition_variable cv_;
    Job pending_;
    bool hasPending_ = false;
    bool running_ = true;
    uint64_t nextSequence_ = 0;

    std::mutex publishMutex_;
    FrameRing *ring_ = nullptr;
    uint64_t lastPublished_ = 0;

    std::mutex statsMutex_;
    MjpegStats stats_;
    std::vector<std::thread> threads_;
};

}  // namespace hdmi
