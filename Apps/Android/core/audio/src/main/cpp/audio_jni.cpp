#include <jni.h>

#include "card_audio.h"

// JNI entry points for com.example.hdmicaptain.audio.CardAudio. Control and meters only.

using hdmi::CardAudio;

namespace {
CardAudio *audio(jlong handle) { return reinterpret_cast<CardAudio *>(handle); }
}  // namespace

extern "C" {

JNIEXPORT jlong JNICALL Java_com_example_hdmicaptain_audio_CardAudio_nativeCreate(JNIEnv *, jclass) {
    return reinterpret_cast<jlong>(new CardAudio());
}

JNIEXPORT void JNICALL Java_com_example_hdmicaptain_audio_CardAudio_nativeDestroy(JNIEnv *, jclass, jlong h) { delete audio(h); }

JNIEXPORT jstring JNICALL Java_com_example_hdmicaptain_audio_CardAudio_nativeStart(JNIEnv *env, jclass, jlong h, jint device) {
    return env->NewStringUTF(audio(h)->start(device).c_str());
}

JNIEXPORT void JNICALL Java_com_example_hdmicaptain_audio_CardAudio_nativeStop(JNIEnv *, jclass, jlong h) { audio(h)->stop(); }

JNIEXPORT void JNICALL Java_com_example_hdmicaptain_audio_CardAudio_nativeSettings(JNIEnv *, jclass, jlong h, jint delayMs,
                                                                                   jint channelMode, jfloat volume, jboolean muted) {
    audio(h)->setDelayMs(delayMs);
    audio(h)->setChannelMode(hdmi::ChannelMode(channelMode));
    audio(h)->setVolume(volume);
    audio(h)->setMuted(muted);
}

// out = [peakL, peakR, rmsL, rmsR, bufferedMs, underruns, running]
JNIEXPORT void JNICALL Java_com_example_hdmicaptain_audio_CardAudio_nativeLevels(JNIEnv *env, jclass, jlong h, jfloatArray out) {
    hdmi::AudioLevels l = audio(h)->levels();
    jfloat v[7] = {l.peakL, l.peakR, l.rmsL, l.rmsR, l.bufferedMs, float(l.underruns), l.running ? 1.f : 0.f};
    env->SetFloatArrayRegion(out, 0, 7, v);
}

}  // extern "C"
