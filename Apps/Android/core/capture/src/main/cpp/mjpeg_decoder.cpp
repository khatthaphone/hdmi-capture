#include "mjpeg_decoder.h"

#include <android/log.h>
#include <android/trace.h>
#include <turbojpeg.h>

#include <ctime>
#include <utility>

#define LOG_TAG "hdmicapture"
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, LOG_TAG, __VA_ARGS__)

namespace hdmi {
namespace {

int64_t nowNs() {
    timespec ts{};
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return int64_t(ts.tv_sec) * 1000000000 + ts.tv_nsec;
}

}  // namespace

MjpegDecoder::MjpegDecoder(int threads) {
    for (int i = 0; i < threads; i++) threads_.emplace_back([this] { run(); });
}

MjpegDecoder::~MjpegDecoder() {
    {
        std::lock_guard<std::mutex> lock(mutex_);
        running_ = false;
    }
    cv_.notify_all();
    for (auto &t : threads_) t.join();
}

void MjpegDecoder::setRing(FrameRing *ring) {
    std::lock_guard<std::mutex> lock(publishMutex_);
    ring_ = ring;
}

void MjpegDecoder::submit(const uint8_t *jpeg, size_t size, int64_t timestampNs) {
    {
        std::lock_guard<std::mutex> lock(mutex_);
        if (hasPending_) {
            std::lock_guard<std::mutex> statsLock(statsMutex_);
            stats_.dropped++;
        }
        pending_.jpeg.assign(jpeg, jpeg + size);
        pending_.timestampNs = timestampNs;
        pending_.sequence = ++nextSequence_;
        hasPending_ = true;
    }
    cv_.notify_one();
}

MjpegStats MjpegDecoder::stats() {
    std::lock_guard<std::mutex> lock(statsMutex_);
    return stats_;
}

void MjpegDecoder::run() {
    tjhandle tj = tj3Init(TJINIT_DECOMPRESS);
    Job job;
    Frame frame;
    bool warned = false;
    for (;;) {
        {
            std::unique_lock<std::mutex> lock(mutex_);
            cv_.wait(lock, [this] { return !running_ || hasPending_; });
            if (!running_) break;
            std::swap(job.jpeg, pending_.jpeg);
            job.timestampNs = pending_.timestampNs;
            job.sequence = pending_.sequence;
            hasPending_ = false;
        }

        ATrace_beginSection("mjpeg");
        int64_t start = nowNs();
        bool ok = tj3DecompressHeader(tj, job.jpeg.data(), job.jpeg.size()) == 0;
        int width = tj3Get(tj, TJPARAM_JPEGWIDTH), height = tj3Get(tj, TJPARAM_JPEGHEIGHT);
        int subsampling = tj3Get(tj, TJPARAM_SUBSAMP);
        PixelFormat format = subsampling == TJSAMP_420 ? PixelFormat::I420 : PixelFormat::I422;
        if (ok && subsampling != TJSAMP_420 && subsampling != TJSAMP_422) {
            if (!warned) LOGW("unsupported MJPEG subsampling %d", subsampling);
            warned = true;
            ok = false;
        }
        if (ok) {
            if (frame.width != width || frame.height != height || frame.format != format) frame.resize(format, width, height);
            int chromaHeight = format == PixelFormat::I420 ? height / 2 : height;
            unsigned char *planes[3] = {frame.data.data(), frame.data.data() + size_t(width) * height,
                                        frame.data.data() + size_t(width) * height + size_t(width / 2) * chromaHeight};
            int strides[3] = {width, width / 2, width / 2};
            // Warnings (a few corrupt MCUs from a missed USB packet) still give a usable picture.
            ok = tj3DecompressToYUVPlanes8(tj, job.jpeg.data(), job.jpeg.size(), planes, strides) == 0 ||
                 tj3GetErrorCode(tj) == TJERR_WARNING;
        }
        int64_t elapsed = nowNs() - start;
        ATrace_endSection();

        {
            std::lock_guard<std::mutex> lock(statsMutex_);
            if (ok) {
                stats_.decoded++;
                stats_.avgDecodeNs = stats_.avgDecodeNs == 0 ? elapsed : (stats_.avgDecodeNs * 15 + elapsed) / 16;
            } else {
                stats_.failed++;
            }
        }
        if (ok) publish(frame, job.sequence, job.timestampNs);
    }
    tj3Destroy(tj);
}

void MjpegDecoder::publish(Frame &frame, uint64_t sequence, int64_t timestampNs) {
    std::lock_guard<std::mutex> lock(publishMutex_);
    if (!ring_ || sequence <= lastPublished_) {
        std::lock_guard<std::mutex> statsLock(statsMutex_);
        stats_.dropped++;
        return;
    }
    // Hand the decoded buffer to the ring and take the slot's old buffer back: no copy, no allocation.
    Frame &slot = ring_->acquire();
    std::swap(slot.data, frame.data);
    slot.format = frame.format;
    slot.width = frame.width;
    slot.height = frame.height;
    slot.fullRange = true;  // JPEG (JFIF) YCbCr is full range
    slot.timestampNs = timestampNs;
    ring_->publish();
    lastPublished_ = sequence;
    // Our buffer is now the slot's previous one; resize() on the next decode if it differs.
    frame.width = 0;
}

}  // namespace hdmi
