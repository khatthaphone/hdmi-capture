#include "renderer.h"

#include <android/api-level.h>
#include <android/log.h>
#include <android/performance_hint.h>
#include <android/trace.h>
#include <sys/resource.h>
#include <unistd.h>

#include <algorithm>
#include <chrono>
#include <cerrno>
#include <cstring>
#include <cmath>
#include <string>
#include <ctime>

#define LOG_TAG "hdmirender"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

namespace hdmi {
namespace {

int64_t nowNs() {
    timespec ts{};
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return int64_t(ts.tv_sec) * 1000000000 + ts.tv_nsec;
}

// Full-window quad from gl_VertexID (triangle strip); uv (0,0) is the top-left of the picture.
const char *kVertex = R"(#version 300 es
uniform vec4 uvRect;
out vec2 uv;
void main() {
    vec2 c = vec2(float(gl_VertexID & 1), float(gl_VertexID >> 1));
    gl_Position = vec4(c.x * 2.0 - 1.0, 1.0 - c.y * 2.0, 0.0, 1.0);
    uv = uvRect.xy + c * uvRect.zw;
}
)";

// Pass 1 fragment shaders: exact texel reads at the output pixel (output row 0 = picture row 0),
// then the same YCbCr → RGB maths as MetalRenderer.
const char *kConvertHeader = R"(#version 300 es
precision highp float;
precision highp int;
uniform mat3 matrix;
uniform vec3 offset;
uniform vec3 scale;
uniform sampler2D plane0;
uniform sampler2D plane1;
uniform sampler2D plane2;
out vec4 color;
vec4 rgb(float y, vec2 c) { return vec4(clamp(matrix * ((vec3(y, c) - offset) * scale), 0.0, 1.0), 1.0); }
)";

const char *kConvertBody[4] = {
    // NV12
    R"(void main() {
        ivec2 p = ivec2(gl_FragCoord.xy);
        color = rgb(texelFetch(plane0, p, 0).r, texelFetch(plane1, p / 2, 0).rg);
    })",
    // YUY2: one RGBA texel = Y0 Cb Y1 Cr
    R"(void main() {
        ivec2 p = ivec2(gl_FragCoord.xy);
        vec4 t = texelFetch(plane0, ivec2(p.x / 2, p.y), 0);
        color = rgb((p.x & 1) == 0 ? t.r : t.b, t.ga);
    })",
    // I420
    R"(void main() {
        ivec2 p = ivec2(gl_FragCoord.xy);
        color = rgb(texelFetch(plane0, p, 0).r, vec2(texelFetch(plane1, p / 2, 0).r, texelFetch(plane2, p / 2, 0).r));
    })",
    // I422
    R"(void main() {
        ivec2 p = ivec2(gl_FragCoord.xy);
        ivec2 q = ivec2(p.x / 2, p.y);
        color = rgb(texelFetch(plane0, p, 0).r, vec2(texelFetch(plane1, q, 0).r, texelFetch(plane2, q, 0).r));
    })",
};

// Pass 2: scale the RGB picture into the window, with the picture look (MetalRenderer's
// adjustColor + applyLUT; identity when adjust == (0, 1, 1, 1) and lut.x == 0).
const char *kScale = R"(#version 300 es
precision mediump float;
precision mediump sampler3D;
uniform sampler2D picture;
uniform sampler3D table;
uniform vec4 adjust;   // brightness, contrast, saturation, 1/gamma
uniform vec4 sharpen;  // amount, texel x, texel y
uniform vec4 lut;      // intensity, size
uniform vec3 lutMin;
uniform vec3 lutMax;
in vec2 uv;
out vec4 color;
void main() {
    vec3 c = texture(picture, uv).rgb;
    if (sharpen.x > 0.0) {
        vec3 n = texture(picture, uv + vec2(sharpen.y, 0.0)).rgb + texture(picture, uv - vec2(sharpen.y, 0.0)).rgb
               + texture(picture, uv + vec2(0.0, sharpen.z)).rgb + texture(picture, uv - vec2(0.0, sharpen.z)).rgb;
        c += (c - n * 0.25) * sharpen.x * 2.0;
    }
    c = (c - 0.5) * adjust.y + 0.5 + adjust.x;
    float luma = dot(c, vec3(0.2126, 0.7152, 0.0722));
    c = clamp(mix(vec3(luma), c, adjust.z), 0.0, 1.0);
    if (adjust.w != 1.0) c = pow(c, vec3(adjust.w));
    if (lut.x > 0.0) {
        float n = lut.y;
        vec3 t = clamp((c - lutMin) / (lutMax - lutMin), 0.0, 1.0);
        c = mix(c, clamp(texture(table, t * ((n - 1.0) / n) + 0.5 / n).rgb, 0.0, 1.0), lut.x);
    }
    color = vec4(c, 1.0);
}
)";

GLuint compile(GLenum type, const char *source) {
    GLuint shader = glCreateShader(type);
    glShaderSource(shader, 1, &source, nullptr);
    glCompileShader(shader);
    GLint ok = 0;
    glGetShaderiv(shader, GL_COMPILE_STATUS, &ok);
    if (!ok) {
        char log[1024];
        glGetShaderInfoLog(shader, sizeof log, nullptr, log);
        LOGE("shader: %s", log);
    }
    return shader;
}

GLuint link(const char *fragment) {
    GLuint program = glCreateProgram();
    GLuint vs = compile(GL_VERTEX_SHADER, kVertex), fs = compile(GL_FRAGMENT_SHADER, fragment);
    glAttachShader(program, vs);
    glAttachShader(program, fs);
    glLinkProgram(program);
    glDeleteShader(vs);
    glDeleteShader(fs);
    GLint ok = 0;
    glGetProgramiv(program, GL_LINK_STATUS, &ok);
    if (!ok) {
        char log[1024];
        glGetProgramInfoLog(program, sizeof log, nullptr, log);
        LOGE("link: %s", log);
    }
    return program;
}

void setTexture(GLuint texture, GLint internal, int w, int h, GLenum format) {
    glBindTexture(GL_TEXTURE_2D, texture);
    glTexImage2D(GL_TEXTURE_2D, 0, internal, w, h, 0, format, GL_UNSIGNED_BYTE, nullptr);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_NEAREST);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
}

void updateTexture(GLuint texture, int w, int h, GLenum format, const uint8_t *data) {
    glBindTexture(GL_TEXTURE_2D, texture);
    glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, w, h, format, GL_UNSIGNED_BYTE, data);
}

}  // namespace

Renderer::Renderer() {
    ring_.setOnPublish([this] { wake(); });
    patternRing_.setOnPublish([this] { wake(); });
    thread_ = std::thread([this] { run(); });
}

Renderer::~Renderer() {
    stopSource();
    {
        std::lock_guard<std::mutex> lock(mutex_);
        running_ = false;
    }
    cv_.notify_all();
    if (thread_.joinable()) thread_.join();
    ring_.setOnPublish(nullptr);
    patternRing_.setOnPublish(nullptr);
}

void Renderer::wake() {
    { std::lock_guard<std::mutex> lock(mutex_); }
    cv_.notify_all();
}

void Renderer::setWindow(ANativeWindow *window) {
    std::unique_lock<std::mutex> lock(mutex_);
    if (pendingWindow_) ANativeWindow_release(pendingWindow_);
    pendingWindow_ = window;
    windowChanged_ = true;
    cv_.notify_all();
    // surfaceDestroyed must not return while EGL still renders into the window.
    cv_.wait_for(lock, std::chrono::seconds(2), [this] { return !windowChanged_ || !running_; });
}

void Renderer::setOptions(RenderOptions options) {
    std::lock_guard<std::mutex> lock(mutex_);
    options_ = options;
    optionsChanged_ = true;
    cv_.notify_all();
}

void Renderer::startTestPattern(Pattern pattern, int width, int height, int fps) {
    pattern_.reset();
    pattern_ = std::make_unique<TestPatternSource>(patternRing_, pattern, width, height, fps);
    showingPattern_ = true;
}

void Renderer::stopSource() {
    pattern_.reset();
    showingPattern_ = false;
}

void Renderer::setEncoder(int id, ANativeWindow *window) {
    std::unique_lock<std::mutex> lock(mutex_);
    pendingEncoders_.emplace_back(id, window);
    encodersChanged_ = true;
    cv_.notify_all();
    cv_.wait_for(lock, std::chrono::seconds(2), [this] { return !encodersChanged_ || !running_; });
}

void Renderer::setExternal(ANativeWindow *window) {
    std::unique_lock<std::mutex> lock(mutex_);
    if (pendingExternal_) ANativeWindow_release(pendingExternal_);
    pendingExternal_ = window;
    externalChanged_ = true;
    cv_.notify_all();
    cv_.wait_for(lock, std::chrono::seconds(2), [this] { return !externalChanged_ || !running_; });
}

void Renderer::applyEncoderChanges(std::vector<std::pair<int, ANativeWindow *>> changes) {
    for (auto &[id, window] : changes) {
        auto existing = encoders_.find(id);
        if (existing != encoders_.end()) {
            eglMakeCurrent(display_, pbuffer_, pbuffer_, context_);
            eglDestroySurface(display_, existing->second.surface);
            ANativeWindow_release(existing->second.window);
            encoders_.erase(existing);
        }
        if (window) {
            EGLSurface surface = eglCreateWindowSurface(display_, config_, window, nullptr);
            if (surface == EGL_NO_SURFACE) {
                LOGE("encoder surface failed: 0x%x", eglGetError());
                ANativeWindow_release(window);
            } else {
                encoders_[id] = {window, surface};
            }
        }
    }
    EGLSurface current = surface_ != EGL_NO_SURFACE ? surface_ : pbuffer_;
    eglMakeCurrent(display_, current, current, context_);
}

// Draws the clean converted frame into every encoder surface (recording, Save Recent).
void Renderer::encode(int64_t timestampNs) {
    if (encoders_.empty()) return;
    ATrace_beginSection("encode");
    glBindFramebuffer(GL_FRAMEBUFFER, 0);
    glUseProgram(scaleProgram_);
    glUniform4f(glGetUniformLocation(scaleProgram_, "uvRect"), 0, 0, 1, 1);
    glUniform4f(glGetUniformLocation(scaleProgram_, "adjust"), 0, 1, 1, 1);
    glUniform4f(glGetUniformLocation(scaleProgram_, "sharpen"), 0, 0, 0, 0);
    glUniform4f(glGetUniformLocation(scaleProgram_, "lut"), 0, 0, 0, 0);
    glActiveTexture(GL_TEXTURE0);
    glBindTexture(GL_TEXTURE_2D, rgbTexture_);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_NEAREST);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
    glBindVertexArray(vao_);
    for (auto &[id, encoder] : encoders_) {
        eglMakeCurrent(display_, encoder.surface, encoder.surface, context_);
        glViewport(0, 0, frameWidth_, frameHeight_);
        glDrawArrays(GL_TRIANGLE_STRIP, 0, 4);
        if (presentationTime_) presentationTime_(display_, encoder.surface, timestampNs);
        eglSwapBuffers(display_, encoder.surface);
    }
    EGLSurface current = surface_ != EGL_NO_SURFACE ? surface_ : pbuffer_;
    eglMakeCurrent(display_, current, current, context_);
    ATrace_endSection();
}

void Renderer::setLook(Look look) {
    std::lock_guard<std::mutex> lock(mutex_);
    look_ = std::move(look);
    lookChanged_ = true;
    cv_.notify_all();
}

SignalSample Renderer::signal() {
    std::lock_guard<std::mutex> lock(mutex_);
    return signal_;
}

RenderStats Renderer::stats() {
    std::lock_guard<std::mutex> lock(mutex_);
    RenderStats s = stats_;
    s.framesIn = showingPattern_ ? patternRing_.published() : ring_.published();
    return s;
}

Screenshot Renderer::screenshot() {
    std::unique_lock<std::mutex> lock(mutex_);
    screenshotRequested_ = true;
    screenshotReady_ = false;
    cv_.notify_all();
    cv_.wait_for(lock, std::chrono::seconds(1), [this] { return screenshotReady_ || !running_; });
    return screenshotReady_ ? screenshot_ : Screenshot{};
}

bool Renderer::ensureContext() {
    display_ = eglGetDisplay(EGL_DEFAULT_DISPLAY);
    if (!eglInitialize(display_, nullptr, nullptr)) return false;
    const EGLint attrs[] = {EGL_RENDERABLE_TYPE, EGL_OPENGL_ES3_BIT, EGL_SURFACE_TYPE, EGL_WINDOW_BIT | EGL_PBUFFER_BIT,
                            EGL_RED_SIZE, 8, EGL_GREEN_SIZE, 8, EGL_BLUE_SIZE, 8, EGL_ALPHA_SIZE, 8, EGL_NONE};
    EGLint count = 0;
    if (!eglChooseConfig(display_, attrs, &config_, 1, &count) || count == 0) return false;
    const EGLint contextAttrs[] = {EGL_CONTEXT_CLIENT_VERSION, 3, EGL_NONE};
    context_ = eglCreateContext(display_, config_, EGL_NO_CONTEXT, contextAttrs);
    const EGLint pbufferAttrs[] = {EGL_WIDTH, 1, EGL_HEIGHT, 1, EGL_NONE};
    pbuffer_ = eglCreatePbufferSurface(display_, config_, pbufferAttrs);
    if (context_ == EGL_NO_CONTEXT || !eglMakeCurrent(display_, pbuffer_, pbuffer_, context_)) return false;

    for (int i = 0; i < 4; i++) {
        std::string source = std::string(kConvertHeader) + kConvertBody[i];
        convertPrograms_[i] = link(source.c_str());
        glUseProgram(convertPrograms_[i]);
        glUniform1i(glGetUniformLocation(convertPrograms_[i], "plane0"), 0);
        glUniform1i(glGetUniformLocation(convertPrograms_[i], "plane1"), 1);
        glUniform1i(glGetUniformLocation(convertPrograms_[i], "plane2"), 2);
    }
    scaleProgram_ = link(kScale);
    glUseProgram(scaleProgram_);
    glUniform1i(glGetUniformLocation(scaleProgram_, "picture"), 0);
    glUniform1i(glGetUniformLocation(scaleProgram_, "table"), 1);
    glGenTextures(3, planes_);
    glGenTextures(1, &rgbTexture_);
    glGenFramebuffers(1, &rgbFramebuffer_);
    glGenVertexArrays(1, &vao_);
    glGenTextures(1, &lutTexture_);
    uploadLook(Look{});  // a valid (unused) 3D texture from the start
    glPixelStorei(GL_UNPACK_ALIGNMENT, 1);
    glPixelStorei(GL_PACK_ALIGNMENT, 1);
    presentationTime_ = reinterpret_cast<PFNEGLPRESENTATIONTIMEANDROIDPROC>(eglGetProcAddress("eglPresentationTimeANDROID"));
    LOGI("GL ready: %s", glGetString(GL_RENDERER));
    return true;
}

void Renderer::attachWindow() {
    surface_ = eglCreateWindowSurface(display_, config_, window_, nullptr);
    if (surface_ == EGL_NO_SURFACE || !eglMakeCurrent(display_, surface_, surface_, context_)) {
        LOGE("window surface failed: 0x%x", eglGetError());
        surface_ = EGL_NO_SURFACE;
        eglMakeCurrent(display_, pbuffer_, pbuffer_, context_);
        return;
    }
    eglSwapInterval(display_, 1);
    frameRateHint_ = 0;
}

void Renderer::detachWindow() {
    if (surface_ != EGL_NO_SURFACE) {
        eglMakeCurrent(display_, pbuffer_, pbuffer_, context_);
        eglDestroySurface(display_, surface_);
        surface_ = EGL_NO_SURFACE;
    }
    if (window_) ANativeWindow_release(window_);
    window_ = nullptr;
}

void Renderer::upload(const Frame &f) {
    int w = f.width, h = f.height;
    const uint8_t *d = f.data.data();
    if (w != frameWidth_ || h != frameHeight_ || f.format != frameFormat_ || !hasFrame_) {
        switch (f.format) {
            case PixelFormat::NV12:
                setTexture(planes_[0], GL_R8, w, h, GL_RED);
                setTexture(planes_[1], GL_RG8, w / 2, h / 2, GL_RG);
                break;
            case PixelFormat::YUY2:
                setTexture(planes_[0], GL_RGBA8, w / 2, h, GL_RGBA);
                break;
            case PixelFormat::I420:
            case PixelFormat::I422: {
                int ch = f.format == PixelFormat::I420 ? h / 2 : h;
                setTexture(planes_[0], GL_R8, w, h, GL_RED);
                setTexture(planes_[1], GL_R8, w / 2, ch, GL_RED);
                setTexture(planes_[2], GL_R8, w / 2, ch, GL_RED);
                break;
            }
        }
        glBindTexture(GL_TEXTURE_2D, rgbTexture_);
        glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, w, h, 0, GL_RGBA, GL_UNSIGNED_BYTE, nullptr);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
        glBindFramebuffer(GL_FRAMEBUFFER, rgbFramebuffer_);
        glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, rgbTexture_, 0);
        frameWidth_ = w;
        frameHeight_ = h;
        frameFormat_ = f.format;
        LOGI("frame %dx%d format %d", w, h, int(f.format));
    }
    switch (f.format) {
        case PixelFormat::NV12:
            updateTexture(planes_[0], w, h, GL_RED, d);
            updateTexture(planes_[1], w / 2, h / 2, GL_RG, d + size_t(w) * h);
            break;
        case PixelFormat::YUY2:
            updateTexture(planes_[0], w / 2, h, GL_RGBA, d);
            break;
        case PixelFormat::I420:
        case PixelFormat::I422: {
            int ch = f.format == PixelFormat::I420 ? h / 2 : h;
            updateTexture(planes_[0], w, h, GL_RED, d);
            updateTexture(planes_[1], w / 2, ch, GL_RED, d + size_t(w) * h);
            updateTexture(planes_[2], w / 2, ch, GL_RED, d + size_t(w) * h + size_t(w / 2) * ch);
            break;
        }
    }
}

void Renderer::convert(const Frame &f) {
    ATrace_beginSection("convert");
    upload(f);
    bool full = active_.range == ColorRange::Full || (active_.range == ColorRange::Auto && f.fullRange);
    bool bt709 = f.height >= 720;  // SD sources are BT.601, as in CaptureCore
    // Columns are the Y, Cb, Cr contributions to (R, G, B).
    const float m709[9] = {1, 1, 1, 0, -0.1873f, 1.8556f, 1.5748f, -0.4681f, 0};
    const float m601[9] = {1, 1, 1, 0, -0.344136f, 1.772f, 1.402f, -0.714136f, 0};
    GLuint program = convertPrograms_[int(f.format)];
    glUseProgram(program);
    glUniformMatrix3fv(glGetUniformLocation(program, "matrix"), 1, GL_FALSE, bt709 ? m709 : m601);
    float chroma = 128.f / 255.f;
    if (full) {
        glUniform3f(glGetUniformLocation(program, "offset"), 0, chroma, chroma);
        glUniform3f(glGetUniformLocation(program, "scale"), 1, 1, 1);
    } else {
        glUniform3f(glGetUniformLocation(program, "offset"), 16.f / 255.f, chroma, chroma);
        glUniform3f(glGetUniformLocation(program, "scale"), 255.f / 219.f, 255.f / 224.f, 255.f / 224.f);
    }
    glUniform4f(glGetUniformLocation(program, "uvRect"), 0, 0, 1, 1);
    for (int i = 0; i < 3; i++) {
        glActiveTexture(GL_TEXTURE0 + i);
        glBindTexture(GL_TEXTURE_2D, planes_[i]);
    }
    glBindFramebuffer(GL_FRAMEBUFFER, rgbFramebuffer_);
    glViewport(0, 0, f.width, f.height);
    glBindVertexArray(vao_);
    glDrawArrays(GL_TRIANGLE_STRIP, 0, 4);
    hasFrame_ = true;
    if (convertCount_ % 10 == 0) sampleSignal(f);
    if (scopesEnabled_ && convertCount_ % 5 == 0) sampleScopes(f);
    convertCount_++;
    ATrace_endSection();
}

// Y, Cb, Cr at pixel (x, y) of any supported frame layout.
static inline void readYCC(const Frame &f, int x, int y, int v[3]) {
    const int w = f.width, h = f.height;
    const uint8_t *d = f.data.data();
    const size_t plane = size_t(w) * h;
    switch (f.format) {
        case PixelFormat::NV12: {
            const uint8_t *c = d + plane + size_t(y / 2) * w + (x / 2) * 2;
            v[0] = d[size_t(y) * w + x]; v[1] = c[0]; v[2] = c[1];
            break;
        }
        case PixelFormat::YUY2: {
            const uint8_t *p = d + size_t(y) * w * 2 + (x / 2) * 4;
            v[0] = p[(x & 1) * 2]; v[1] = p[1]; v[2] = p[3];
            break;
        }
        case PixelFormat::I420: {
            size_t ci = size_t(y / 2) * (w / 2) + x / 2;
            v[0] = d[size_t(y) * w + x]; v[1] = d[plane + ci]; v[2] = d[plane + plane / 4 + ci];
            break;
        }
        case PixelFormat::I422: {
            size_t ci = size_t(y) * (w / 2) + x / 2;
            v[0] = d[size_t(y) * w + x]; v[1] = d[plane + ci]; v[2] = d[plane + plane / 2 + ci];
            break;
        }
    }
    (void) h;
}

void Renderer::sampleScopes(const Frame &f) {
    if (f.width < 2 || f.height < 2) return;
    std::vector<uint8_t> grid(size_t(kScopeW) * kScopeH * 3);
    size_t i = 0;
    for (int sy = 0; sy < kScopeH; sy++) {
        int y = (sy * 2 + 1) * f.height / (kScopeH * 2);
        for (int sx = 0; sx < kScopeW; sx++) {
            int v[3];
            readYCC(f, (sx * 2 + 1) * f.width / (kScopeW * 2), y, v);
            grid[i++] = uint8_t(v[0]);
            grid[i++] = uint8_t(v[1]);
            grid[i++] = uint8_t(v[2]);
        }
    }
    std::lock_guard<std::mutex> lock(mutex_);
    scopeGrid_.swap(grid);
    scopeSequence_++;
}

int64_t Renderer::scopeSamples(uint8_t *out, size_t size) {
    std::lock_guard<std::mutex> lock(mutex_);
    if (scopeGrid_.empty() || size < scopeGrid_.size()) return 0;
    std::memcpy(out, scopeGrid_.data(), scopeGrid_.size());
    return scopeSequence_;
}

// Mean and range of Y/Cb/Cr over SignalAnalyzer's grid (every 10th frame; ~14 k byte reads).
void Renderer::sampleSignal(const Frame &f) {
    const int gw = 160, gh = 90, w = f.width, h = f.height;
    if (w < 2 || h < 2) return;
    double sum[3] = {0, 0, 0};
    int lo[3] = {255, 255, 255}, hi[3] = {0, 0, 0};
    for (int gy = 0; gy < gh; gy++) {
        int y = (gy * 2 + 1) * h / (gh * 2);
        for (int gx = 0; gx < gw; gx++) {
            int x = (gx * 2 + 1) * w / (gw * 2);
            int v[3];
            readYCC(f, x, y, v);
            for (int i = 0; i < 3; i++) {
                sum[i] += v[i];
                lo[i] = std::min(lo[i], v[i]);
                hi[i] = std::max(hi[i], v[i]);
            }
        }
    }
    const double n = gw * gh;
    std::lock_guard<std::mutex> lock(mutex_);
    signal_.meanY = sum[0] / n;
    signal_.meanCb = sum[1] / n;
    signal_.meanCr = sum[2] / n;
    signal_.rangeY = hi[0] - lo[0];
    signal_.rangeChroma = std::max(hi[1] - lo[1], hi[2] - lo[2]);
    signal_.fullRange = f.fullRange;
    signal_.count++;
}

void Renderer::uploadLook(const Look &look) {
    glBindTexture(GL_TEXTURE_3D, lutTexture_);
    if (look.lutSize > 1 && look.lut.size() == size_t(look.lutSize) * look.lutSize * look.lutSize * 3) {
        glTexImage3D(GL_TEXTURE_3D, 0, GL_RGB16F, look.lutSize, look.lutSize, look.lutSize, 0, GL_RGB, GL_FLOAT, look.lut.data());
    } else {
        const float black[3] = {0, 0, 0};
        glTexImage3D(GL_TEXTURE_3D, 0, GL_RGB16F, 1, 1, 1, 0, GL_RGB, GL_FLOAT, black);
    }
    glTexParameteri(GL_TEXTURE_3D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_3D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_3D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
    glTexParameteri(GL_TEXTURE_3D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
    glTexParameteri(GL_TEXTURE_3D, GL_TEXTURE_WRAP_R, GL_CLAMP_TO_EDGE);
}

// Fits the picture (with the look) into a window surface and presents it.
void Renderer::drawScaled(EGLSurface surface, bool vsync) {
    EGLint w = 0, h = 0;
    eglMakeCurrent(display_, surface, surface, context_);
    eglSwapInterval(display_, vsync ? 1 : 0);
    eglQuerySurface(display_, surface, EGL_WIDTH, &w);
    eglQuerySurface(display_, surface, EGL_HEIGHT, &h);
    RenderGeometry g = RenderGeometry::compute(float(frameWidth_), float(frameHeight_), float(w), float(h), active_.scale);
    glBindFramebuffer(GL_FRAMEBUFFER, 0);
    glViewport(0, 0, w, h);
    glClearColor(0, 0, 0, 1);
    glClear(GL_COLOR_BUFFER_BIT);
    // GL viewports start bottom-left; the geometry is top-left.
    glViewport(GLint(g.viewport.x), GLint(float(h) - g.viewport.y - g.viewport.h), GLsizei(g.viewport.w), GLsizei(g.viewport.h));
    glUseProgram(scaleProgram_);
    glUniform4f(glGetUniformLocation(scaleProgram_, "uvRect"), g.source.x, g.source.y, g.source.w, g.source.h);
    const Look &l = activeLook_;
    glUniform4f(glGetUniformLocation(scaleProgram_, "adjust"), l.brightness, l.contrast, l.saturation, 1.f / std::max(0.1f, l.gamma));
    glUniform4f(glGetUniformLocation(scaleProgram_, "sharpen"), l.sharpness, 1.f / frameWidth_, 1.f / frameHeight_, 0);
    glUniform4f(glGetUniformLocation(scaleProgram_, "lut"), l.lutSize > 1 ? l.lutIntensity : 0.f, float(l.lutSize), 0, 0);
    glUniform3f(glGetUniformLocation(scaleProgram_, "lutMin"), l.lutMin[0], l.lutMin[1], l.lutMin[2]);
    glUniform3f(glGetUniformLocation(scaleProgram_, "lutMax"), l.lutMax[0], l.lutMax[1], l.lutMax[2]);
    glActiveTexture(GL_TEXTURE1);
    glBindTexture(GL_TEXTURE_3D, lutTexture_);
    glActiveTexture(GL_TEXTURE0);
    glBindTexture(GL_TEXTURE_2D, rgbTexture_);
    GLint filter = active_.filter == FilterMode::Nearest ? GL_NEAREST : GL_LINEAR;
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, filter);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, filter);
    glBindVertexArray(vao_);
    glDrawArrays(GL_TRIANGLE_STRIP, 0, 4);
    eglSwapBuffers(display_, surface);
}

void Renderer::present(int64_t arrivalNs) {
    ATrace_beginSection("present");
    // External display first, without vsync, so the phone's own picture never waits on it.
    if (externalSurface_ != EGL_NO_SURFACE) drawScaled(externalSurface_, false);
    if (surface_ != EGL_NO_SURFACE) drawScaled(surface_, true);
    ATrace_endSection();

    std::lock_guard<std::mutex> lock(mutex_);
    stats_.framesDrawn++;
    stats_.width = frameWidth_;
    stats_.height = frameHeight_;
    if (arrivalNs > 0) stats_.lastLatencyNs = nowNs() - arrivalNs;
}

// ADPF: tell the CPU governor what a frame costs against the frame budget, so it clocks up for
// 1080p60 instead of reacting a few frames late (Android 13+).
void Renderer::updateHint(float fps, int64_t workNs) {
    if (__builtin_available(android 33, *)) {
        int64_t target = fps > 1 ? int64_t(1e9 / fps) : 16666667;
        if (!hintSession_) {
            APerformanceHintManager *manager = APerformanceHint_getManager();
            int32_t tid = gettid();
            if (manager) hintSession_ = APerformanceHint_createSession(manager, &tid, 1, target);
            hintTargetNs_ = target;
        } else if (std::llabs(target - hintTargetNs_) > 1000000) {
            APerformanceHint_updateTargetWorkDuration(static_cast<APerformanceHintSession *>(hintSession_), target);
            hintTargetNs_ = target;
        }
        if (hintSession_ && workNs > 0) APerformanceHint_reportActualWorkDuration(static_cast<APerformanceHintSession *>(hintSession_), workNs);
    }
}

void Renderer::run() {
    // THREAD_PRIORITY_URGENT_DISPLAY: the picture must not wait behind app work.
    if (setpriority(PRIO_PROCESS, 0, -8) != 0) LOGI("render priority: %d", errno);
    if (!ensureContext()) {
        LOGE("EGL setup failed: 0x%x", eglGetError());
        return;
    }
    float interval = 0;  // smoothed frame interval (s), for the display frame-rate hint
    for (;;) {
        bool windowChanged, optionsChanged, wantScreenshot;
        ANativeWindow *newWindow = nullptr;
        {
            std::unique_lock<std::mutex> lock(mutex_);
            cv_.wait(lock, [this] {
                return !running_ || windowChanged_ || externalChanged_ || encodersChanged_ || optionsChanged_ || lookChanged_ || screenshotRequested_ ||
                       ring_.hasFresh() || patternRing_.hasFresh();
            });
            if (!running_) break;
            windowChanged = windowChanged_;
            if (windowChanged) {
                newWindow = pendingWindow_;
                pendingWindow_ = nullptr;
            }
            optionsChanged = optionsChanged_ || lookChanged_;
            optionsChanged_ = false;
            active_ = options_;
            if (lookChanged_) {
                bool lutChanged = look_.lutSize != activeLook_.lutSize || look_.lut != activeLook_.lut;
                activeLook_ = look_;
                lookChanged_ = false;
                if (lutChanged) uploadLook(activeLook_);
            }
            wantScreenshot = screenshotRequested_;
            if (externalChanged_) {
                if (externalSurface_ != EGL_NO_SURFACE) {
                    eglMakeCurrent(display_, pbuffer_, pbuffer_, context_);
                    eglDestroySurface(display_, externalSurface_);
                    externalSurface_ = EGL_NO_SURFACE;
                }
                if (externalWindow_) ANativeWindow_release(externalWindow_);
                externalWindow_ = pendingExternal_;
                pendingExternal_ = nullptr;
                if (externalWindow_) {
                    externalSurface_ = eglCreateWindowSurface(display_, config_, externalWindow_, nullptr);
                    if (externalSurface_ == EGL_NO_SURFACE) LOGE("external surface failed: 0x%x", eglGetError());
                }
                EGLSurface current = surface_ != EGL_NO_SURFACE ? surface_ : pbuffer_;
                eglMakeCurrent(display_, current, current, context_);
                externalChanged_ = false;
                optionsChanged = true;  // draw the current frame on it right away
                cv_.notify_all();
            }
            if (encodersChanged_) {
                applyEncoderChanges(std::move(pendingEncoders_));
                pendingEncoders_.clear();
                encodersChanged_ = false;
                cv_.notify_all();
            }
        }

        if (windowChanged) {
            detachWindow();
            window_ = newWindow;
            if (window_) attachWindow();
            {
                std::lock_guard<std::mutex> lock(mutex_);
                windowChanged_ = pendingWindow_ != nullptr;  // another change may have arrived
            }
            cv_.notify_all();
        }

        int64_t arrival = 0;
        bool fresh = false;
        // Draw from the pattern ring while a test pattern shows; drop the other ring's frames.
        FrameRing &active = showingPattern_ ? patternRing_ : ring_;
        FrameRing &idle = showingPattern_ ? ring_ : patternRing_;
        if (idle.take()) idle.release();
        int64_t workStart = nowNs();
        if (Frame *frame = active.take()) {
            if (lastArrivalNs_ > 0) {
                float dt = float(frame->timestampNs - lastArrivalNs_) / 1e9f;
                interval = interval == 0 ? dt : interval * 0.9f + dt * 0.1f;
            }
            lastArrivalNs_ = frame->timestampNs;
            arrival = frame->timestampNs;
            convert(*frame);
            active.release();
            encode(arrival);
            fresh = true;
        }

        if (wantScreenshot) {
            Screenshot shot;
            if (hasFrame_) {
                shot.width = frameWidth_;
                shot.height = frameHeight_;
                shot.rgba.resize(size_t(frameWidth_) * frameHeight_ * 4);
                glBindFramebuffer(GL_FRAMEBUFFER, rgbFramebuffer_);
                glReadPixels(0, 0, frameWidth_, frameHeight_, GL_RGBA, GL_UNSIGNED_BYTE, shot.rgba.data());
            }
            std::lock_guard<std::mutex> lock(mutex_);
            screenshot_ = std::move(shot);
            screenshotRequested_ = false;
            screenshotReady_ = true;
            cv_.notify_all();
        }

        if (hasFrame_ && (surface_ != EGL_NO_SURFACE || externalSurface_ != EGL_NO_SURFACE) && (fresh || windowChanged || optionsChanged)) {
            present(arrival);
            updateHint(interval > 0 ? 1.f / interval : 60.f, nowNs() - workStart);
            // Ask the display to match the source rate (60 fps on a 90/120 Hz panel), Android 11+.
            if (interval > 0) {
                float fps = 1.f / interval;
                if (std::abs(fps - frameRateHint_) > 1.f) {
                    frameRateHint_ = fps;
                    if (__builtin_available(android 30, *)) {
                        if (window_) ANativeWindow_setFrameRate(window_, fps, ANATIVEWINDOW_FRAME_RATE_COMPATIBILITY_FIXED_SOURCE);
                    }
                }
            }
        }
    }
    if (__builtin_available(android 33, *)) {
        if (hintSession_) APerformanceHint_closeSession(static_cast<APerformanceHintSession *>(hintSession_));
    }
    detachWindow();
    if (externalSurface_ != EGL_NO_SURFACE) eglDestroySurface(display_, externalSurface_);
    if (externalWindow_) ANativeWindow_release(externalWindow_);
    std::vector<std::pair<int, ANativeWindow *>> removeAll;
    for (auto &[id, encoder] : encoders_) removeAll.emplace_back(id, nullptr);
    applyEncoderChanges(std::move(removeAll));
    if (display_ != EGL_NO_DISPLAY) {
        eglMakeCurrent(display_, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT);
        if (pbuffer_ != EGL_NO_SURFACE) eglDestroySurface(display_, pbuffer_);
        if (context_ != EGL_NO_CONTEXT) eglDestroyContext(display_, context_);
        eglTerminate(display_);
    }
}

}  // namespace hdmi
