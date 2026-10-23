#include "hc_render.h"

#include <algorithm>
#include <cstring>
#include <memory>
#include <mutex>

#include "gl_pipeline.h"
#include "platform.h"
#include "test_pattern.h"

using namespace hdmi;

struct hc_renderer {
    FrameRing ring;
    FrameRing patternRing;
    Frame *writing = nullptr;  // producer thread: the slot from the last acquire
    std::unique_ptr<TestPatternSource> pattern;
    std::atomic<bool> showingPattern{false};

    std::mutex mutex;  // guards everything below that a non-GL thread touches
    hc_frame_callback callback = nullptr;
    void *callbackUser = nullptr;
    RenderOptions options;
    Look look;
    bool lookChanged = false;
    SignalSample signal;
    std::vector<uint8_t> scopeGrid;
    int64_t scopeSequence = 0;
    int64_t framesDrawn = 0, lastLatencyNs = 0;
    int width = 0, height = 0;
    std::atomic<bool> scopesEnabled{false};

    // GL thread only.
    GlPipeline gl;
    bool glReady = false;
    Look activeLook;
    int convertCount = 0;
    std::vector<uint8_t> scratchGrid;

    hc_renderer() {
        auto wake = [this] {
            hc_frame_callback cb;
            void *user;
            {
                std::lock_guard<std::mutex> lock(mutex);
                cb = callback;
                user = callbackUser;
            }
            if (cb) cb(user);
        };
        ring.setOnPublish(wake);
        patternRing.setOnPublish(wake);
    }

    ~hc_renderer() {
        pattern.reset();
        ring.setOnPublish(nullptr);
        patternRing.setOnPublish(nullptr);
    }
};

extern "C" {

hc_renderer *hc_renderer_create(void) { return new hc_renderer(); }

void hc_renderer_destroy(hc_renderer *r) { delete r; }

void hc_renderer_set_frame_callback(hc_renderer *r, hc_frame_callback callback, void *user) {
    std::lock_guard<std::mutex> lock(r->mutex);
    r->callback = callback;
    r->callbackUser = user;
}

uint8_t *hc_renderer_acquire(hc_renderer *r, int32_t format, int32_t width, int32_t height) {
    if (format < 0 || format > 3 || width < 2 || height < 2) return nullptr;
    Frame &frame = r->ring.acquire();
    frame.resize(PixelFormat(format), width, height);
    r->writing = &frame;
    return frame.data.data();
}

void hc_renderer_publish(hc_renderer *r, int64_t timestamp_ns, int32_t full_range, int32_t matrix) {
    if (!r->writing) return;
    // Stamp the slot acquire() handed out before it becomes visible.
    r->writing->timestampNs = timestamp_ns > 0 ? timestamp_ns : nowNs();
    r->writing->fullRange = full_range != 0;
    r->writing->matrix = Matrix(std::clamp(matrix, 0, 2));
    r->writing = nullptr;
    r->ring.publish();
}

int64_t hc_renderer_monotonic_ns(void) { return nowNs(); }

void hc_renderer_start_pattern(hc_renderer *r, int32_t pattern, int32_t width, int32_t height, int32_t fps) {
    r->pattern.reset();
    r->pattern = std::make_unique<TestPatternSource>(r->patternRing, Pattern(std::clamp(pattern, 0, 3)), width, height, fps);
    r->showingPattern = true;
}

void hc_renderer_stop_pattern(hc_renderer *r) {
    r->pattern.reset();
    r->showingPattern = false;
}

void hc_renderer_set_options(hc_renderer *r, int32_t scale, int32_t filter, int32_t range) {
    std::lock_guard<std::mutex> lock(r->mutex);
    r->options.scale = ScaleMode(std::clamp(scale, 0, 3));
    r->options.filter = FilterMode(std::clamp(filter, 0, 1));
    r->options.range = ColorRange(std::clamp(range, 0, 2));
}

void hc_renderer_set_look(hc_renderer *r, float brightness, float contrast, float saturation, float gamma, float sharpness,
                          float lut_intensity, int32_t lut_size, const float *lut, const float *lut_min, const float *lut_max) {
    Look look;
    look.brightness = brightness;
    look.contrast = contrast;
    look.saturation = saturation;
    look.gamma = gamma;
    look.sharpness = sharpness;
    look.lutIntensity = lut_intensity;
    if (lut && lut_size > 1) {
        look.lutSize = lut_size;
        look.lut.assign(lut, lut + size_t(lut_size) * lut_size * lut_size * 3);
    }
    for (int i = 0; i < 3; i++) {
        if (lut_min) look.lutMin[i] = lut_min[i];
        if (lut_max) look.lutMax[i] = lut_max[i];
    }
    std::lock_guard<std::mutex> lock(r->mutex);
    r->look = std::move(look);
    r->lookChanged = true;
}

void hc_renderer_set_scopes(hc_renderer *r, int32_t enabled) { r->scopesEnabled = enabled != 0; }

int64_t hc_renderer_scopes(hc_renderer *r, uint8_t *out, int32_t size) {
    std::lock_guard<std::mutex> lock(r->mutex);
    if (r->scopeGrid.empty() || size < int32_t(r->scopeGrid.size())) return 0;
    std::memcpy(out, r->scopeGrid.data(), r->scopeGrid.size());
    return r->scopeSequence;
}

void hc_renderer_signal(hc_renderer *r, hc_signal *out) {
    std::lock_guard<std::mutex> lock(r->mutex);
    const SignalSample &s = r->signal;
    *out = {s.meanY, s.meanCb, s.meanCr, s.rangeY, s.rangeChroma, s.fullRange ? 1 : 0, s.count};
}

void hc_renderer_stats(hc_renderer *r, hc_stats *out) {
    std::lock_guard<std::mutex> lock(r->mutex);
    out->frames_in = r->showingPattern ? r->patternRing.published() : r->ring.published();
    out->frames_drawn = r->framesDrawn;
    out->last_latency_ns = r->lastLatencyNs;
    out->width = r->width;
    out->height = r->height;
}

int32_t hc_renderer_gl_init(hc_renderer *r) {
    r->glReady = r->gl.init();
    r->activeLook = Look{};
    if (!r->glReady) HC_LOGE("GL pipeline init failed");
    return r->glReady ? 1 : 0;
}

void hc_renderer_gl_release(hc_renderer *r) {
    if (r->glReady) r->gl.release();
    r->glReady = false;
}

int32_t hc_renderer_render(hc_renderer *r, int32_t width, int32_t height) {
    if (!r->glReady) return 0;
    GLint target = 0;
    glGetIntegerv(GL_FRAMEBUFFER_BINDING, &target);
    RenderOptions options;
    {
        std::lock_guard<std::mutex> lock(r->mutex);
        options = r->options;
        if (r->lookChanged) {
            bool lutChanged = r->look.lutSize != r->activeLook.lutSize || r->look.lut != r->activeLook.lut;
            r->activeLook = r->look;
            r->lookChanged = false;
            if (lutChanged) r->gl.uploadLook(r->activeLook);
        }
    }
    // Draw from the pattern ring while a test pattern shows; drop the other ring's frames.
    FrameRing &active = r->showingPattern ? r->patternRing : r->ring;
    FrameRing &idle = r->showingPattern ? r->ring : r->patternRing;
    if (idle.take()) idle.release();
    int64_t arrival = 0;
    if (Frame *frame = active.take()) {
        arrival = frame->timestampNs;
        r->gl.convert(*frame, options.range);
        SignalSample signal;
        bool sampled = r->convertCount % 10 == 0;
        bool scoped = r->scopesEnabled && r->convertCount % 5 == 0;
        if (sampled) {
            std::lock_guard<std::mutex> lock(r->mutex);
            signal = r->signal;
        }
        if (sampled) sampleSignal(*frame, signal);
        if (scoped) sampleScopes(*frame, r->scratchGrid);
        active.release();
        r->convertCount++;
        if (sampled || scoped) {
            std::lock_guard<std::mutex> lock(r->mutex);
            if (sampled) r->signal = signal;
            if (scoped) {
                r->scopeGrid.swap(r->scratchGrid);
                r->scopeSequence++;
            }
        }
    }
    r->gl.drawScaled(GLuint(target), width, height, options, r->activeLook);
    std::lock_guard<std::mutex> lock(r->mutex);
    if (r->gl.hasFrame()) r->framesDrawn++;
    r->width = r->gl.width();
    r->height = r->gl.height();
    if (arrival > 0) r->lastLatencyNs = nowNs() - arrival;
    return r->gl.hasFrame() ? 1 : 0;
}

int32_t hc_renderer_read_pass1(hc_renderer *r, uint8_t *rgba, int32_t capacity, int32_t *width, int32_t *height) {
    if (!r->glReady) return 0;
    Screenshot shot;
    if (!r->gl.readPass1(shot) || capacity < int32_t(shot.rgba.size())) return 0;
    std::memcpy(rgba, shot.rgba.data(), shot.rgba.size());
    *width = shot.width;
    *height = shot.height;
    return int32_t(shot.rgba.size());
}

}  // extern "C"
