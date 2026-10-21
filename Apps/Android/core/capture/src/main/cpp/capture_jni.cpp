#include <jni.h>

#include "uvc_source.h"

// JNI entry points for com.example.hdmicaptain.capture.UvcSource. Control only; frames stay native.

using hdmi::UvcSource;

namespace {
UvcSource *source(jlong handle) { return reinterpret_cast<UvcSource *>(handle); }
jstring str(JNIEnv *env, const std::string &s) { return env->NewStringUTF(s.c_str()); }
}  // namespace

extern "C" {

JNIEXPORT jlong JNICALL
Java_com_example_hdmicaptain_capture_UvcSource_nativeCreate(JNIEnv *, jclass) {
    return reinterpret_cast<jlong>(new UvcSource());
}

JNIEXPORT void JNICALL
Java_com_example_hdmicaptain_capture_UvcSource_nativeDestroy(JNIEnv *, jclass, jlong handle) {
    delete source(handle);
}

JNIEXPORT jstring JNICALL
Java_com_example_hdmicaptain_capture_UvcSource_nativeOpen(JNIEnv *env, jclass, jlong handle, jint fd) {
    return str(env, source(handle)->open(fd));
}

JNIEXPORT jstring JNICALL
Java_com_example_hdmicaptain_capture_UvcSource_nativeDescribe(JNIEnv *env, jclass, jlong handle) {
    return str(env, source(handle)->describe());
}

JNIEXPORT jstring JNICALL
Java_com_example_hdmicaptain_capture_UvcSource_nativeStart(JNIEnv *env, jclass, jlong handle, jstring fourCC, jint width,
                                                           jint height, jint fps, jlong ring) {
    const char *cc = env->GetStringUTFChars(fourCC, nullptr);
    std::string result = source(handle)->start(cc, width, height, fps, reinterpret_cast<hdmi::FrameRing *>(ring));
    env->ReleaseStringUTFChars(fourCC, cc);
    return str(env, result);
}

JNIEXPORT void JNICALL
Java_com_example_hdmicaptain_capture_UvcSource_nativeStop(JNIEnv *, jclass, jlong handle) {
    source(handle)->stop();
}

JNIEXPORT void JNICALL
Java_com_example_hdmicaptain_capture_UvcSource_nativeClose(JNIEnv *, jclass, jlong handle) {
    source(handle)->close();
}

// out = [frames, shortFrames, compressed, lastFrameNs, decoded, decodeDropped, decodeFailed, avgDecodeNs]
JNIEXPORT void JNICALL
Java_com_example_hdmicaptain_capture_UvcSource_nativeStats(JNIEnv *env, jclass, jlong handle, jlongArray out) {
    hdmi::UvcStats s = source(handle)->stats();
    hdmi::MjpegStats d = source(handle)->decoderStats();
    jlong v[8] = {s.frames, s.shortFrames, s.compressed, s.lastFrameNs, d.decoded, d.dropped, d.failed, d.avgDecodeNs};
    env->SetLongArrayRegion(out, 0, 8, v);
}

}  // extern "C"
