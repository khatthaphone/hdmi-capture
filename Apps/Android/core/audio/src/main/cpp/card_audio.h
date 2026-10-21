#pragma once

#include <oboe/Oboe.h>

#include <atomic>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#include "audio_ring.h"

namespace hdmi {

struct AudioLevels {
    float peakL = 0, peakR = 0, rmsL = 0, rmsR = 0;
    float bufferedMs = 0;
    int64_t underruns = 0;  // output callbacks that played silence after priming
    bool running = false;
};

// HDMI audio passthrough: an Oboe input stream on the card's USB device feeds the ring (channel
// mapping + meters on the way in); the output stream plays it with the user's delay, drift
// correction, volume and mute. Both callbacks are real-time: no locks, no allocation.
class CardAudio : public oboe::AudioStreamDataCallback, public oboe::AudioStreamErrorCallback {
public:
    static constexpr int kSampleRate = 48000;

    CardAudio();
    ~CardAudio() override;

    // inputDeviceId: AudioDeviceInfo.getId() of the card's USB input. Returns an error or "".
    std::string start(int inputDeviceId);
    void stop();

    void setDelayMs(int ms) { delayFrames_ = ms * kSampleRate / 1000; }
    void setChannelMode(ChannelMode mode) { channelMode_ = mode; }
    void setVolume(float volume) { volume_ = volume; }
    void setMuted(bool muted) { muted_ = muted; }
    AudioLevels levels();

    oboe::DataCallbackResult onAudioReady(oboe::AudioStream *stream, void *audioData, int32_t frames) override;
    void onErrorAfterClose(oboe::AudioStream *stream, oboe::Result error) override;

private:
    void captured(const float *samples, int frames);
    void render(float *out, int frames);

    std::shared_ptr<oboe::AudioStream> input_;
    std::shared_ptr<oboe::AudioStream> output_;
    std::mutex controlMutex_;
    int inputDeviceId_ = 0;

    AudioRing ring_{kSampleRate};  // 1 s
    DriftController drift_;
    std::vector<float> inScratch_;
    std::vector<float> outScratch_;
    std::atomic<int> delayFrames_{0};
    std::atomic<int> chunkFrames_{480};
    std::atomic<ChannelMode> channelMode_{ChannelMode::Auto};
    std::atomic<float> volume_{1.f};
    std::atomic<bool> muted_{false};

    // Written by the callbacks, read by levels(): relaxed atomics are enough for meters.
    std::atomic<float> peakL_{0}, peakR_{0}, rmsL_{0}, rmsR_{0};
    std::atomic<int64_t> underruns_{0};
    std::atomic<bool> running_{false};
};

}  // namespace hdmi
