#include <jni.h>

#include <cstring>
#include <ctime>

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

// Camera2 path: one YUV_420_888 image (any row/pixel strides) → I420 in a ring slot.
extern "C" JNIEXPORT void JNICALL
Java_com_example_hdmicaptain_capture_Camera2Source_nativePushYuv420(JNIEnv *env, jclass, jlong ringHandle, jint width, jint height,
                                                                    jobject yBuffer, jint yStride, jobject uBuffer, jobject vBuffer,
                                                                    jint uvStride, jint uvPixelStride) {
    auto *ring = reinterpret_cast<hdmi::FrameRing *>(ringHandle);
    auto *y = static_cast<const uint8_t *>(env->GetDirectBufferAddress(yBuffer));
    auto *u = static_cast<const uint8_t *>(env->GetDirectBufferAddress(uBuffer));
    auto *v = static_cast<const uint8_t *>(env->GetDirectBufferAddress(vBuffer));
    if (!ring || !y || !u || !v) return;
    hdmi::Frame &slot = ring->acquire();
    if (slot.width != width || slot.height != height || slot.format != hdmi::PixelFormat::I420) {
        slot.resize(hdmi::PixelFormat::I420, width, height);
    }
    uint8_t *dy = slot.data.data();
    uint8_t *du = dy + size_t(width) * height;
    uint8_t *dv = du + size_t(width / 2) * (height / 2);
    for (int row = 0; row < height; row++) std::memcpy(dy + size_t(row) * width, y + size_t(row) * yStride, width);
    for (int row = 0; row < height / 2; row++) {
        for (int col = 0; col < width / 2; col++) {
            du[size_t(row) * (width / 2) + col] = u[size_t(row) * uvStride + size_t(col) * uvPixelStride];
            dv[size_t(row) * (width / 2) + col] = v[size_t(row) * uvStride + size_t(col) * uvPixelStride];
        }
    }
    timespec ts{};
    clock_gettime(CLOCK_MONOTONIC, &ts);
    slot.fullRange = true;  // Camera2 YUV_420_888 is JFIF (full range)
    slot.timestampNs = int64_t(ts.tv_sec) * 1000000000 + ts.tv_nsec;
    ring->publish();
}
