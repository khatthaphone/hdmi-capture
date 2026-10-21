#include "uvc_source.h"

#include <android/log.h>
#include <libusb.h>

#include <cstring>
#include <ctime>
#include <sstream>

#define LOG_TAG "hdmicapture"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, LOG_TAG, __VA_ARGS__)

namespace hdmi {
namespace {

int64_t nowNs() {
    timespec ts{};
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return int64_t(ts.tv_sec) * 1000000000 + ts.tv_nsec;
}

// Four-character code of a format descriptor: "MJPG" for MJPEG, else the GUID's first 4 bytes ("YUY2", "NV12"…).
std::string fourCC(const uvc_format_desc_t *f) {
    if (f->bDescriptorSubtype == UVC_VS_FORMAT_MJPEG) return "MJPG";
    if (f->bDescriptorSubtype == UVC_VS_FORMAT_FRAME_BASED) return "H264";
    std::string cc(reinterpret_cast<const char *>(f->fourccFormat), 4);
    for (char &c : cc) if (c < 32 || c > 126) c = '?';
    return cc;
}

enum uvc_frame_format frameFormat(const std::string &cc) {
    if (cc == "MJPG") return UVC_FRAME_FORMAT_MJPEG;
    if (cc == "NV12") return UVC_FRAME_FORMAT_NV12;
    if (cc == "I420") return UVC_FRAME_FORMAT_I420;
    if (cc == "UYVY") return UVC_FRAME_FORMAT_UYVY;
    return UVC_FRAME_FORMAT_YUYV;
}

const char *speedName(int speed) {
    switch (speed) {
        case LIBUSB_SPEED_LOW: return "usb1-low";
        case LIBUSB_SPEED_FULL: return "usb1";
        case LIBUSB_SPEED_HIGH: return "usb2";
        case LIBUSB_SPEED_SUPER: return "usb3";
        case LIBUSB_SPEED_SUPER_PLUS: return "usb3-plus";
        default: return "unknown";
    }
}

}  // namespace

UvcSource::~UvcSource() { close(); }

std::string UvcSource::open(int fd) {
    close();
    // Android: no bus scanning (no permission for it); we wrap the fd UsbManager opened.
    libusb_set_option(nullptr, LIBUSB_OPTION_NO_DEVICE_DISCOVERY);
    uvc_error_t err = uvc_init(&ctx_, nullptr);
    if (err != UVC_SUCCESS) return std::string("uvc_init: ") + uvc_strerror(err);
    err = uvc_wrap(fd, ctx_, &devh_);
    if (err != UVC_SUCCESS) {
        uvc_exit(ctx_);
        ctx_ = nullptr;
        return std::string("uvc_wrap: ") + uvc_strerror(err);
    }
    LOGI("opened UVC device (fd %d)", fd);
    return "";
}

std::string UvcSource::describe() {
    if (!devh_) return "{}";
    libusb_device *dev = libusb_get_device(uvc_get_libusb_handle(devh_));
    std::ostringstream out;
    out << "{\"speed\":\"" << speedName(libusb_get_device_speed(dev)) << "\",\"formats\":[";
    bool first = true;
    for (const uvc_format_desc_t *f = uvc_get_format_descs(devh_); f; f = f->next) {
        std::string cc = fourCC(f);
        for (const uvc_frame_desc_t *fr = f->frame_descs; fr; fr = fr->next) {
            out << (first ? "" : ",") << "{\"cc\":\"" << cc << "\",\"w\":" << fr->wWidth << ",\"h\":" << fr->wHeight;
            first = false;
            if (fr->intervals) {
                out << ",\"rates\":[";
                for (const uint32_t *i = fr->intervals; *i; i++) out << (i == fr->intervals ? "" : ",") << 1e7 / double(*i);
                out << "]}";
            } else {
                // Continuous range: [min fps, max fps].
                double lo = fr->dwMaxFrameInterval ? 1e7 / fr->dwMaxFrameInterval : 0;
                double hi = fr->dwMinFrameInterval ? 1e7 / fr->dwMinFrameInterval : 0;
                out << ",\"ranges\":[[" << lo << "," << hi << "]]}";
            }
        }
    }
    out << "]}";
    return out.str();
}

std::string UvcSource::start(const std::string &cc, int width, int height, int fps, FrameRing *ring) {
    if (!devh_) return "not open";
    stop();
    uvc_stream_ctrl_t ctrl;
    uvc_error_t err = uvc_get_stream_ctrl_format_size(devh_, &ctrl, frameFormat(cc), width, height, fps);
    if (err != UVC_SUCCESS) return std::string("format: ") + uvc_strerror(err);
    compressed_ = cc == "MJPG";
    format_ = cc == "NV12" ? PixelFormat::NV12 : cc == "I420" ? PixelFormat::I420 : PixelFormat::YUY2;
    ring_ = ring;
    decoder_.setRing(compressed_ ? ring : nullptr);
    {
        std::lock_guard<std::mutex> lock(statsMutex_);
        stats_ = {};
    }
    err = uvc_start_streaming(devh_, &ctrl, &UvcSource::onFrame, this, 0);
    if (err != UVC_SUCCESS) {
        ring_ = nullptr;
        return std::string("start: ") + uvc_strerror(err);
    }
    streaming_ = true;
    LOGI("streaming %s %dx%d @ %d", cc.c_str(), width, height, fps);
    return "";
}

void UvcSource::stop() {
    if (devh_ && streaming_) uvc_stop_streaming(devh_);
    streaming_ = false;
    ring_ = nullptr;
    decoder_.setRing(nullptr);
}

void UvcSource::close() {
    stop();
    if (devh_) uvc_close(devh_);
    devh_ = nullptr;
    if (ctx_) uvc_exit(ctx_);
    ctx_ = nullptr;
}

UvcStats UvcSource::stats() {
    std::lock_guard<std::mutex> lock(statsMutex_);
    return stats_;
}

void UvcSource::onFrame(uvc_frame_t *frame, void *self) { static_cast<UvcSource *>(self)->deliver(frame); }

// libuvc's stream thread. One copy into the ring slot; no allocation once the slot is sized.
void UvcSource::deliver(uvc_frame_t *frame) {
    int64_t now = nowNs();
    if (compressed_) {
        decoder_.submit(static_cast<const uint8_t *>(frame->data), frame->data_bytes, now);
        std::lock_guard<std::mutex> lock(statsMutex_);
        stats_.compressed++;
        stats_.lastFrameNs = now;
        return;
    }
    FrameRing *ring = ring_.load();
    if (!ring) return;
    int w = int(frame->width), h = int(frame->height);
    size_t need = frameBytes(format_, w, h);
    if (frame->data_bytes < need) {
        std::lock_guard<std::mutex> lock(statsMutex_);
        stats_.shortFrames++;
        return;
    }
    Frame &slot = ring->acquire();
    if (slot.width != w || slot.height != h || slot.format != format_) slot.resize(format_, w, h);
    const auto *src = static_cast<const uint8_t *>(frame->data);
    size_t row = format_ == PixelFormat::YUY2 ? size_t(w) * 2 : size_t(w);
    if (format_ == PixelFormat::YUY2 && frame->step > row) {
        for (int y = 0; y < h; y++) std::memcpy(slot.data.data() + y * row, src + y * frame->step, row);
    } else {
        std::memcpy(slot.data.data(), src, need);
    }
    slot.fullRange = false;  // UVC YCbCr is limited range unless the card says otherwise
    slot.timestampNs = now;
    ring->publish();
    std::lock_guard<std::mutex> lock(statsMutex_);
    stats_.frames++;
    stats_.lastFrameNs = now;
}

}  // namespace hdmi
