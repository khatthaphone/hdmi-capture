#pragma once

#include <EGL/egl.h>
#include <GLES3/gl3.h>
#include <android/native_window.h>

#include <atomic>
#include <condition_variable>
#include <memory>
#include <mutex>
#include <thread>
#include <vector>

#include "frame.h"
#include "geometry.h"
#include "test_pattern.h"

namespace hdmi {

enum class FilterMode : int { Linear = 0, Nearest = 1 };
// Values match ColorRange in Kotlin and CaptureCore: Auto trusts the source's flag.
enum class ColorRange : int { Auto = 0, Limited = 1, Full = 2 };

struct RenderOptions {
    ScaleMode scale = ScaleMode::Fit;
    FilterMode filter = FilterMode::Linear;
    ColorRange range = ColorRange::Auto;
};

struct RenderStats {
    int64_t framesIn = 0;      // published by the source
    int64_t framesDrawn = 0;   // presented on screen
    int64_t lastLatencyNs = 0; // arrival → swap of the last presented frame
    int width = 0, height = 0;
};

struct Screenshot {
    int width = 0, height = 0;
    std::vector<uint8_t> rgba;  // top row first
};

// Owns the render thread and its EGL context. Frames arrive through ring(); the thread draws each
// new one as soon as it arrives (no queueing behind vsync beyond one frame) into the window.
// Pass 1 converts the frame to RGB at source size (that texture is also what screenshots read, so
// UI overlays never reach them); pass 2 scales it into the window.
class Renderer {
public:
    Renderer();
    ~Renderer();

    FrameRing &ring() { return ring_; }

    // Window from the SurfaceView; nullptr detaches and returns only once EGL let go of it.
    void setWindow(ANativeWindow *window);
    void setOptions(RenderOptions options);
    void startTestPattern(Pattern pattern, int width, int height, int fps);
    void stopSource();
    RenderStats stats();
    // Blocks until the render thread has read the current frame; empty if none yet.
    Screenshot screenshot();

private:
    void run();
    bool ensureContext();
    void attachWindow();
    void detachWindow();
    void upload(const Frame &frame);
    void convert(const Frame &frame);
    void present(int64_t arrivalNs);
    void wake();

    FrameRing ring_;
    std::unique_ptr<TestPatternSource> pattern_;

    std::mutex mutex_;
    std::condition_variable cv_;
    bool running_ = true;
    bool windowChanged_ = false;
    ANativeWindow *pendingWindow_ = nullptr;
    bool optionsChanged_ = false;
    RenderOptions options_;
    bool screenshotRequested_ = false;
    bool screenshotReady_ = false;
    Screenshot screenshot_;
    RenderStats stats_;

    // Render thread only.
    EGLDisplay display_ = EGL_NO_DISPLAY;
    EGLConfig config_ = nullptr;
    EGLContext context_ = EGL_NO_CONTEXT;
    EGLSurface surface_ = EGL_NO_SURFACE;
    EGLSurface pbuffer_ = EGL_NO_SURFACE;
    ANativeWindow *window_ = nullptr;
    RenderOptions active_;
    GLuint convertPrograms_[4] = {};
    GLuint scaleProgram_ = 0;
    GLuint planes_[3] = {};
    GLuint rgbTexture_ = 0;
    GLuint rgbFramebuffer_ = 0;
    GLuint vao_ = 0;
    int frameWidth_ = 0, frameHeight_ = 0;
    PixelFormat frameFormat_ = PixelFormat::NV12;
    bool hasFrame_ = false;
    int64_t lastArrivalNs_ = 0;
    float frameRateHint_ = 0;

    std::thread thread_;
};

}  // namespace hdmi
