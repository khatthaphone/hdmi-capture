#pragma once

#include <GLES3/gl3.h>

#include <cstdint>
#include <vector>

#include "frame.h"
#include "geometry.h"

namespace hdmi {

enum class FilterMode : int { Linear = 0, Nearest = 1 };
// Values match ColorRange in Kotlin, C# and CaptureCore: Auto trusts the source's flag.
enum class ColorRange : int { Auto = 0, Limited = 1, Full = 2 };

struct RenderOptions {
    ScaleMode scale = ScaleMode::Fit;
    FilterMode filter = FilterMode::Linear;
    ColorRange range = ColorRange::Auto;
    Matrix matrix = Matrix::Auto;  // the user's override; Auto keeps each frame's own
};

// Picture look, applied when scaling into the window (CaptureCore's ImageAdjustments + LUT).
struct Look {
    float brightness = 0, contrast = 1, saturation = 1, gamma = 1, sharpness = 0;
    float lutIntensity = 0;
    int lutSize = 0;
    std::vector<float> lut;  // size³ RGB, red fastest
    float lutMin[3] = {0, 0, 0}, lutMax[3] = {1, 1, 1};
};

// Y/Cb/Cr statistics over SignalAnalyzer's 160×90 grid, for no-signal detection.
struct SignalSample {
    double meanY = 0, meanCb = 128, meanCr = 128, rangeY = 0, rangeChroma = 0;
    bool fullRange = false;
    int64_t count = 0;  // increases with every new sample
};

struct Screenshot {
    int width = 0, height = 0;
    std::vector<uint8_t> rgba;  // top row first
};

constexpr int kScopeW = 320, kScopeH = 180;

// Mean and range of Y/Cb/Cr over SignalAnalyzer's grid (~14 k byte reads). Bumps out.count.
void sampleSignal(const Frame &frame, SignalSample &out);
// A kScopeW×kScopeH grid of interleaved Y,Cb,Cr bytes for the scopes.
void sampleScopes(const Frame &frame, std::vector<uint8_t> &grid);

// The GLES 3 drawing shared by Android and Linux: pass 1 converts a frame to RGB at source size
// (that texture is also what screenshots and recordings read, so UI overlays never reach them),
// pass 2 scales it with the look into a target framebuffer. Owns no thread, context or surface:
// every call needs the same GL context current.
class GlPipeline {
public:
    bool init();
    void release();

    // Upload + pass 1. Leaves rgbFramebuffer bound.
    void convert(const Frame &frame, ColorRange range, Matrix matrix = Matrix::Auto);
    // Pass 2 into framebuffer `fbo` of w×h pixels (clears it black around the picture).
    void drawScaled(GLuint fbo, int w, int h, const RenderOptions &options, const Look &look);
    // The clean pass-1 picture at source size, no look (recording surfaces).
    void drawClean(GLuint fbo);
    void uploadLook(const Look &look);
    bool readPass1(Screenshot &out);

    bool hasFrame() const { return hasFrame_; }
    int width() const { return frameWidth_; }
    int height() const { return frameHeight_; }

private:
    void upload(const Frame &frame);

    GLuint convertPrograms_[4] = {};
    GLuint scaleProgram_ = 0;
    GLuint planes_[3] = {};
    GLuint rgbTexture_ = 0;
    GLuint rgbFramebuffer_ = 0;
    GLuint vao_ = 0;
    GLuint lutTexture_ = 0;
    int frameWidth_ = 0, frameHeight_ = 0;
    PixelFormat frameFormat_ = PixelFormat::NV12;
    bool hasFrame_ = false;
};

}  // namespace hdmi
