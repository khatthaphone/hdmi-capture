#include "card_audio.h"

#include <android/log.h>

#include <cmath>

#define LOG_TAG "hdmiaudio"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, LOG_TAG, __VA_ARGS__)

namespace hdmi {

CardAudio::CardAudio() {
    inScratch_.resize(size_t(kSampleRate) * 2);   // generous: callbacks are a few ms
    outScratch_.resize(size_t(kSampleRate) * 2);
}

CardAudio::~CardAudio() { stop(); }

std::string CardAudio::start(int inputDeviceId) {
    std::lock_guard<std::mutex> lock(controlMutex_);
    inputDeviceId_ = inputDeviceId;
    // Ask for 48 kHz stereo float and let Oboe convert whatever the card really delivers.
    oboe::AudioStreamBuilder in;
    in.setDirection(oboe::Direction::Input)
        ->setDeviceId(inputDeviceId)
        ->setPerformanceMode(oboe::PerformanceMode::LowLatency)
        ->setSharingMode(oboe::SharingMode::Shared)
        ->setInputPreset(oboe::InputPreset::Unprocessed)  // no voice processing on HDMI audio
        ->setFormat(oboe::AudioFormat::Float)
        ->setChannelCount(oboe::ChannelCount::Stereo)
        ->setSampleRate(kSampleRate)
        ->setFormatConversionAllowed(true)
        ->setChannelConversionAllowed(true)
        ->setSampleRateConversionQuality(oboe::SampleRateConversionQuality::Medium)
        ->setDataCallback(this)
        ->setErrorCallback(this);
    oboe::Result result = in.openStream(input_);
    if (result != oboe::Result::OK) return std::string("input: ") + oboe::convertToText(result);

    oboe::AudioStreamBuilder out;
    out.setDirection(oboe::Direction::Output)
        ->setPerformanceMode(oboe::PerformanceMode::LowLatency)
        ->setSharingMode(oboe::SharingMode::Shared)
        ->setUsage(oboe::Usage::Media)
        ->setContentType(oboe::ContentType::Movie)
        ->setFormat(oboe::AudioFormat::Float)
        ->setChannelCount(oboe::ChannelCount::Stereo)
        ->setSampleRate(kSampleRate)
        ->setFormatConversionAllowed(true)
        ->setChannelConversionAllowed(true)
        ->setSampleRateConversionQuality(oboe::SampleRateConversionQuality::Medium)
        ->setDataCallback(this)
        ->setErrorCallback(this);
    result = out.openStream(output_);
    if (result != oboe::Result::OK) {
        input_->close();
        input_.reset();
        return std::string("output: ") + oboe::convertToText(result);
    }
    LOGI("input device %d: %d Hz, %d ch, burst %d; output %d Hz, burst %d", inputDeviceId, input_->getSampleRate(),
         input_->getChannelCount(), input_->getFramesPerBurst(), output_->getSampleRate(), output_->getFramesPerBurst());
    chunkFrames_ = std::max(input_->getFramesPerBurst(), 1);
    input_->requestStart();
    output_->requestStart();
    running_ = true;
    return "";
}

void CardAudio::stop() {
    std::lock_guard<std::mutex> lock(controlMutex_);
    running_ = false;
    for (auto *stream : {&input_, &output_}) {
        if (*stream) {
            (*stream)->stop();
            (*stream)->close();
            stream->reset();
        }
    }
}

AudioLevels CardAudio::levels() {
    AudioLevels l;
    l.peakL = peakL_;
    l.peakR = peakR_;
    l.rmsL = rmsL_;
    l.rmsR = rmsR_;
    l.bufferedMs = float(ring_.available()) * 1000.f / kSampleRate;
    l.underruns = underruns_;
    l.running = running_;
    return l;
}

oboe::DataCallbackResult CardAudio::onAudioReady(oboe::AudioStream *stream, void *audioData, int32_t frames) {
    if (stream->getDirection() == oboe::Direction::Input) {
        captured(static_cast<const float *>(audioData), frames);
    } else {
        render(static_cast<float *>(audioData), frames);
    }
    return oboe::DataCallbackResult::Continue;
}

void CardAudio::captured(const float *samples, int frames) {
    frames = std::min(frames, int(inScratch_.size() / 2));
    float *s = inScratch_.data();
    std::memcpy(s, samples, sizeof(float) * frames * 2);
    ChannelMode mode = channelMode_.load(std::memory_order_relaxed);
    applyChannelMode(mode == ChannelMode::Auto ? ChannelMode::Normal : mode, s, frames);
    float pl = 0, pr = 0, sl = 0, sr = 0;
    for (int i = 0; i < frames; i++) {
        pl = std::max(pl, std::fabs(s[2 * i]));
        pr = std::max(pr, std::fabs(s[2 * i + 1]));
        sl += s[2 * i] * s[2 * i];
        sr += s[2 * i + 1] * s[2 * i + 1];
    }
    // Fast attack, slow release, so meters are readable at UI refresh rates.
    peakL_ = std::max(pl, peakL_.load(std::memory_order_relaxed) * 0.85f);
    peakR_ = std::max(pr, peakR_.load(std::memory_order_relaxed) * 0.85f);
    if (frames > 0) {
        rmsL_ = std::max(std::sqrt(sl / frames), rmsL_.load(std::memory_order_relaxed) * 0.85f);
        rmsR_ = std::max(std::sqrt(sr / frames), rmsR_.load(std::memory_order_relaxed) * 0.85f);
    }
    chunkFrames_.store(frames, std::memory_order_relaxed);
    ring_.write(s, frames);
}

void CardAudio::render(float *out, int frames) {
    drift_.targetFrames = DriftController::target(delayFrames_.load(std::memory_order_relaxed),
                                                  chunkFrames_.load(std::memory_order_relaxed), frames);
    DriftController::Plan plan = drift_.plan(ring_.available(), frames);
    if (plan.silence) {
        std::fill(out, out + frames * 2, 0.f);
        if (running_.load(std::memory_order_relaxed)) underruns_.fetch_add(1, std::memory_order_relaxed);
        return;
    }
    if (plan.discard > 0) ring_.discard(plan.discard);
    int count = std::min(plan.consume, int(outScratch_.size() / 2));
    int got = ring_.read(outScratch_.data(), count);
    stretch(outScratch_.data(), got, out, frames);
    float gain = muted_.load(std::memory_order_relaxed) ? 0.f : volume_.load(std::memory_order_relaxed);
    if (gain != 1.f) {
        for (int i = 0; i < frames * 2; i++) out[i] *= gain;
    }
}

void CardAudio::onErrorAfterClose(oboe::AudioStream *, oboe::Result error) {
    // Usually the card (input) or headphones (output) went away. Reopen on the same input; if the
    // card is gone, the controller stops us when it sees the detach.
    LOGW("stream closed: %s; reopening", oboe::convertToText(error));
    int device = inputDeviceId_;
    stop();
    std::string failed = start(device);
    if (!failed.empty()) LOGW("reopen failed: %s", failed.c_str());
}

}  // namespace hdmi
