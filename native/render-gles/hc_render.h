#pragma once

// C ABI of the shared renderer for hosts that own the GL context themselves (Linux: GtkGLArea).
// Frames go in from any thread (acquire → fill → publish); everything marked "GL" must run with the
// host's GLES 3 context current, on the thread that owns it.

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct hc_renderer hc_renderer;

typedef struct {
    int64_t frames_in;       // published by the active source
    int64_t frames_drawn;    // drawn by hc_renderer_render
    int64_t last_latency_ns; // arrival → end of the draw that showed it
    int32_t width, height;
} hc_stats;

typedef struct {
    double mean_y, mean_cb, mean_cr, range_y, range_chroma;
    int32_t full_range;
    int64_t count;
} hc_signal;

typedef void (*hc_frame_callback)(void *user);

hc_renderer *hc_renderer_create(void);
void hc_renderer_destroy(hc_renderer *r);  // after hc_renderer_gl_release

// Called (on the producer's thread) whenever a frame is published, so the host can schedule a draw.
void hc_renderer_set_frame_callback(hc_renderer *r, hc_frame_callback callback, void *user);

// Capture frames. format: 0 NV12, 1 YUY2, 2 I420, 3 I422. Returns a buffer of frame size to fill
// (planes back to back, each row exactly as wide as the plane). One producer thread at a time.
uint8_t *hc_renderer_acquire(hc_renderer *r, int32_t format, int32_t width, int32_t height);
// matrix: 0 auto (BT.709 from 720 lines up), 1 BT.601, 2 BT.709.
void hc_renderer_publish(hc_renderer *r, int64_t timestamp_ns, int32_t full_range, int32_t matrix);
int64_t hc_renderer_monotonic_ns(void);

// Test patterns (0 smpteBars, 1 fullBars, 2 grid, 3 grayRamp) on their own ring; while one runs,
// card frames are dropped.
void hc_renderer_start_pattern(hc_renderer *r, int32_t pattern, int32_t width, int32_t height, int32_t fps);
void hc_renderer_stop_pattern(hc_renderer *r);

// scale: 0 fit, 1 fill, 2 stretch, 3 integer · filter: 0 linear, 1 nearest · range: 0 auto, 1 limited, 2 full ·
// matrix: 0 auto (the frame's), 1 BT.601, 2 BT.709
void hc_renderer_set_options(hc_renderer *r, int32_t scale, int32_t filter, int32_t range, int32_t matrix);
// lut: lut_size³ RGB floats (red fastest), or NULL/0 for none.
void hc_renderer_set_look(hc_renderer *r, float brightness, float contrast, float saturation, float gamma, float sharpness,
                          float lut_intensity, int32_t lut_size, const float *lut, const float *lut_min, const float *lut_max);

void hc_renderer_set_scopes(hc_renderer *r, int32_t enabled);
// Copies the latest kScope grid (320×180×3 bytes) into out; returns its sequence (0 = none yet).
int64_t hc_renderer_scopes(hc_renderer *r, uint8_t *out, int32_t size);
void hc_renderer_signal(hc_renderer *r, hc_signal *out);
void hc_renderer_stats(hc_renderer *r, hc_stats *out);

// GL: create/delete the GL objects in the current context.
int32_t hc_renderer_gl_init(hc_renderer *r);
void hc_renderer_gl_release(hc_renderer *r);
// GL: converts the newest frame (if any), then draws into the framebuffer bound now, of the given
// size in pixels. Returns 1 when a picture was drawn, 0 when there is none yet (cleared black).
int32_t hc_renderer_render(hc_renderer *r, int32_t width, int32_t height);
// GL: the same into the region (x, y, w, h) of the bound framebuffer, GL coordinates (origin bottom-left).
// Several renderers can share one context and draw the tiles of a grid.
int32_t hc_renderer_render_region(hc_renderer *r, int32_t x, int32_t y, int32_t width, int32_t height);
// GL: the clean pass-1 picture as RGBA, top row first. Returns bytes written (0 = no frame or too small).
int32_t hc_renderer_read_pass1(hc_renderer *r, uint8_t *rgba, int32_t capacity, int32_t *width, int32_t *height);

#ifdef __cplusplus
}
#endif
