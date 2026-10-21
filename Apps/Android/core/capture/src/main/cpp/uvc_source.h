#pragma once

// libusb.h first: libuvc only declares uvc_wrap (open from an fd) when it sees libusb ≥ 1.0.27.
#include <libusb.h>
#include <libuvc/libuvc.h>

#include <atomic>
#include <cstdint>
#include <mutex>
#include <string>
#include <vector>

#include "frame.h"

namespace hdmi {

struct UvcStats {
    int64_t frames = 0;       // complete frames handed to the ring
    int64_t shortFrames = 0;  // dropped: fewer bytes than the format needs (missed isochronous packets)
    int64_t compressed = 0;   // MJPEG frames received (decoded from task 074 on)
    int64_t lastFrameNs = 0;
};

// A UVC capture card opened from the file descriptor Android's UsbManager gives us
// (libusb_wrap_sys_device). Claims only the UVC control and streaming interfaces, so the card's
// audio stays with Android's USB audio driver (AudioRecord/Oboe).
class UvcSource {
public:
    ~UvcSource();

    // Returns an empty string on success, else the error.
    std::string open(int fd);
    // Formats as JSON: {"speed": "usb2|usb3|…", "formats": [{"cc", "w", "h", "rates": [fps…]}]}.
    std::string describe();
    std::string start(const std::string &fourCC, int width, int height, int fps, FrameRing *ring);
    void stop();
    void close();
    UvcStats stats();

private:
    static void onFrame(uvc_frame_t *frame, void *self);
    void deliver(uvc_frame_t *frame);

    uvc_context_t *ctx_ = nullptr;
    uvc_device_handle_t *devh_ = nullptr;
    std::atomic<FrameRing *> ring_{nullptr};
    PixelFormat format_ = PixelFormat::YUY2;
    bool compressed_ = false;
    bool streaming_ = false;
    std::mutex statsMutex_;
    UvcStats stats_;
};

}  // namespace hdmi
