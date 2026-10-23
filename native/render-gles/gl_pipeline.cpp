#include "gl_pipeline.h"

#include <algorithm>
#include <cstring>
#include <string>

#include "platform.h"

// Extracted from the Android renderer (Apps/Android/core/render/src/main/cpp/renderer.cpp, task 107):
// the same shaders and maths, without the thread, EGL or window.

namespace hdmi {
namespace {

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
        HC_LOGE("shader: %s", log);
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
        HC_LOGE("link: %s", log);
        glDeleteProgram(program);
        return 0;
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

// Y, Cb, Cr at pixel (x, y) of any supported frame layout.
inline void readYCC(const Frame &f, int x, int y, int v[3]) {
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

}  // namespace

void sampleSignal(const Frame &f, SignalSample &out) {
    const int gw = 160, gh = 90, w = f.width, h = f.height;
    if (w < 2 || h < 2) return;
    double sum[3] = {0, 0, 0};
    int lo[3] = {255, 255, 255}, hi[3] = {0, 0, 0};
    for (int gy = 0; gy < gh; gy++) {
        int y = (gy * 2 + 1) * h / (gh * 2);
        for (int gx = 0; gx < gw; gx++) {
            int x = (gx * 2 + 1) * w / (gw * 2);
            int v[3] = {0, 128, 128};
            readYCC(f, x, y, v);
            for (int i = 0; i < 3; i++) {
                sum[i] += v[i];
                lo[i] = std::min(lo[i], v[i]);
                hi[i] = std::max(hi[i], v[i]);
            }
        }
    }
    const double n = gw * gh;
    out.meanY = sum[0] / n;
    out.meanCb = sum[1] / n;
    out.meanCr = sum[2] / n;
    out.rangeY = hi[0] - lo[0];
    out.rangeChroma = std::max(hi[1] - lo[1], hi[2] - lo[2]);
    out.fullRange = f.fullRange;
    out.count++;
}

void sampleScopes(const Frame &f, std::vector<uint8_t> &grid) {
    if (f.width < 2 || f.height < 2) return;
    grid.resize(size_t(kScopeW) * kScopeH * 3);
    size_t i = 0;
    for (int sy = 0; sy < kScopeH; sy++) {
        int y = (sy * 2 + 1) * f.height / (kScopeH * 2);
        for (int sx = 0; sx < kScopeW; sx++) {
            int v[3] = {0, 128, 128};
            readYCC(f, (sx * 2 + 1) * f.width / (kScopeW * 2), y, v);
            grid[i++] = uint8_t(v[0]);
            grid[i++] = uint8_t(v[1]);
            grid[i++] = uint8_t(v[2]);
        }
    }
}

bool GlPipeline::init() {
    for (int i = 0; i < 4; i++) {
        std::string source = std::string(kConvertHeader) + kConvertBody[i];
        convertPrograms_[i] = link(source.c_str());
        if (!convertPrograms_[i]) return false;
        glUseProgram(convertPrograms_[i]);
        glUniform1i(glGetUniformLocation(convertPrograms_[i], "plane0"), 0);
        glUniform1i(glGetUniformLocation(convertPrograms_[i], "plane1"), 1);
        glUniform1i(glGetUniformLocation(convertPrograms_[i], "plane2"), 2);
    }
    scaleProgram_ = link(kScale);
    if (!scaleProgram_) return false;
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
    glUseProgram(0);
    HC_LOGI("GL ready: %s / %s", glGetString(GL_RENDERER), glGetString(GL_VERSION));
    return true;
}

void GlPipeline::release() {
    for (GLuint &p : convertPrograms_) {
        if (p) glDeleteProgram(p);
        p = 0;
    }
    if (scaleProgram_) glDeleteProgram(scaleProgram_);
    scaleProgram_ = 0;
    glDeleteTextures(3, planes_);
    glDeleteTextures(1, &rgbTexture_);
    glDeleteTextures(1, &lutTexture_);
    glDeleteFramebuffers(1, &rgbFramebuffer_);
    glDeleteVertexArrays(1, &vao_);
    std::memset(planes_, 0, sizeof planes_);
    rgbTexture_ = lutTexture_ = rgbFramebuffer_ = vao_ = 0;
    hasFrame_ = false;
}

void GlPipeline::upload(const Frame &f) {
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
        HC_LOGI("frame %dx%d format %d", w, h, int(f.format));
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

void GlPipeline::convert(const Frame &f, ColorRange range) {
    HC_TRACE_BEGIN("convert");
    upload(f);
    bool full = range == ColorRange::Full || (range == ColorRange::Auto && f.fullRange);
    bool bt709 = f.matrix == Matrix::Bt709 || (f.matrix == Matrix::Auto && f.height >= 720);  // SD sources are BT.601, as in CaptureCore
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
    HC_TRACE_END();
}

void GlPipeline::uploadLook(const Look &look) {
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

void GlPipeline::drawScaled(GLuint fbo, int w, int h, const RenderOptions &options, const Look &l) {
    glBindFramebuffer(GL_FRAMEBUFFER, fbo);
    glViewport(0, 0, w, h);
    glClearColor(0, 0, 0, 1);
    glClear(GL_COLOR_BUFFER_BIT);
    if (!hasFrame_ || w <= 0 || h <= 0) return;
    RenderGeometry g = RenderGeometry::compute(float(frameWidth_), float(frameHeight_), float(w), float(h), options.scale);
    // GL viewports start bottom-left; the geometry is top-left.
    glViewport(GLint(g.viewport.x), GLint(float(h) - g.viewport.y - g.viewport.h), GLsizei(g.viewport.w), GLsizei(g.viewport.h));
    glUseProgram(scaleProgram_);
    glUniform4f(glGetUniformLocation(scaleProgram_, "uvRect"), g.source.x, g.source.y, g.source.w, g.source.h);
    glUniform4f(glGetUniformLocation(scaleProgram_, "adjust"), l.brightness, l.contrast, l.saturation, 1.f / std::max(0.1f, l.gamma));
    glUniform4f(glGetUniformLocation(scaleProgram_, "sharpen"), l.sharpness, 1.f / frameWidth_, 1.f / frameHeight_, 0);
    glUniform4f(glGetUniformLocation(scaleProgram_, "lut"), l.lutSize > 1 ? l.lutIntensity : 0.f, float(l.lutSize), 0, 0);
    glUniform3f(glGetUniformLocation(scaleProgram_, "lutMin"), l.lutMin[0], l.lutMin[1], l.lutMin[2]);
    glUniform3f(glGetUniformLocation(scaleProgram_, "lutMax"), l.lutMax[0], l.lutMax[1], l.lutMax[2]);
    glActiveTexture(GL_TEXTURE1);
    glBindTexture(GL_TEXTURE_3D, lutTexture_);
    glActiveTexture(GL_TEXTURE0);
    glBindTexture(GL_TEXTURE_2D, rgbTexture_);
    GLint filter = options.filter == FilterMode::Nearest ? GL_NEAREST : GL_LINEAR;
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, filter);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, filter);
    glBindVertexArray(vao_);
    glDrawArrays(GL_TRIANGLE_STRIP, 0, 4);
    glBindVertexArray(0);
    glUseProgram(0);
}

void GlPipeline::drawClean(GLuint fbo) {
    if (!hasFrame_) return;
    glBindFramebuffer(GL_FRAMEBUFFER, fbo);
    glViewport(0, 0, frameWidth_, frameHeight_);
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
    glDrawArrays(GL_TRIANGLE_STRIP, 0, 4);
    glBindVertexArray(0);
    glUseProgram(0);
}

bool GlPipeline::readPass1(Screenshot &out) {
    if (!hasFrame_) return false;
    out.width = frameWidth_;
    out.height = frameHeight_;
    out.rgba.resize(size_t(frameWidth_) * frameHeight_ * 4);
    GLint previous = 0;
    glGetIntegerv(GL_FRAMEBUFFER_BINDING, &previous);
    glBindFramebuffer(GL_FRAMEBUFFER, rgbFramebuffer_);
    glReadPixels(0, 0, frameWidth_, frameHeight_, GL_RGBA, GL_UNSIGNED_BYTE, out.rgba.data());
    glBindFramebuffer(GL_FRAMEBUFFER, GLuint(previous));
    return true;
}

}  // namespace hdmi
