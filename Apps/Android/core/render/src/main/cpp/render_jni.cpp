#include <android/native_window_jni.h>
#include <jni.h>

#include "renderer.h"

// JNI entry points for com.example.hdmicaptain.render.VideoRenderer.
// Frames never cross JNI: only setup, control and the occasional screenshot do.

using hdmi::Renderer;

namespace {
Renderer *renderer(jlong handle) { return reinterpret_cast<Renderer *>(handle); }
}  // namespace

extern "C" {

JNIEXPORT jstring JNICALL
Java_com_example_hdmicaptain_render_NativeRender_nativeVersion(JNIEnv *env, jclass) {
    return env->NewStringUTF("hdmirender 0.2 (GLES 3)");
}

JNIEXPORT jlong JNICALL
Java_com_example_hdmicaptain_render_VideoRenderer_nativeCreate(JNIEnv *, jclass) {
    return reinterpret_cast<jlong>(new Renderer());
}

JNIEXPORT void JNICALL
Java_com_example_hdmicaptain_render_VideoRenderer_nativeDestroy(JNIEnv *, jclass, jlong handle) {
    delete renderer(handle);
}

JNIEXPORT void JNICALL
Java_com_example_hdmicaptain_render_VideoRenderer_nativeSetSurface(JNIEnv *env, jclass, jlong handle, jobject surface) {
    renderer(handle)->setWindow(surface ? ANativeWindow_fromSurface(env, surface) : nullptr);
}

JNIEXPORT void JNICALL
Java_com_example_hdmicaptain_render_VideoRenderer_nativeSetOptions(JNIEnv *, jclass, jlong handle, jint scale, jint filter, jint range) {
    renderer(handle)->setOptions({hdmi::ScaleMode(scale), hdmi::FilterMode(filter), hdmi::ColorRange(range)});
}

JNIEXPORT void JNICALL
Java_com_example_hdmicaptain_render_VideoRenderer_nativeStartTestPattern(JNIEnv *, jclass, jlong handle, jint pattern, jint width, jint height, jint fps) {
    renderer(handle)->startTestPattern(hdmi::Pattern(pattern), width, height, fps);
}

JNIEXPORT void JNICALL
Java_com_example_hdmicaptain_render_VideoRenderer_nativeStopSource(JNIEnv *, jclass, jlong handle) {
    renderer(handle)->stopSource();
}

// The frame ring a capture source publishes into (core:capture's UvcSource). Valid until destroy.
JNIEXPORT jlong JNICALL
Java_com_example_hdmicaptain_render_VideoRenderer_nativeRing(JNIEnv *, jclass, jlong handle) {
    return reinterpret_cast<jlong>(&renderer(handle)->ring());
}

JNIEXPORT void JNICALL
Java_com_example_hdmicaptain_render_VideoRenderer_nativeSetLook(JNIEnv *env, jclass, jlong handle, jfloatArray adjust,
                                                                jfloatArray lut, jint lutSize, jfloat intensity, jfloatArray domain) {
    hdmi::Look look;
    float a[5];
    env->GetFloatArrayRegion(adjust, 0, 5, a);
    look.brightness = a[0]; look.contrast = a[1]; look.saturation = a[2]; look.gamma = a[3]; look.sharpness = a[4];
    if (lut) {
        look.lutSize = lutSize;
        look.lutIntensity = intensity;
        look.lut.resize(size_t(env->GetArrayLength(lut)));
        env->GetFloatArrayRegion(lut, 0, jsize(look.lut.size()), look.lut.data());
        float dom[6];
        env->GetFloatArrayRegion(domain, 0, 6, dom);
        for (int i = 0; i < 3; i++) { look.lutMin[i] = dom[i]; look.lutMax[i] = dom[3 + i]; }
    }
    renderer(handle)->setLook(std::move(look));
}

// out = [meanY, meanCb, meanCr, rangeY, rangeChroma, fullRange, count]
JNIEXPORT void JNICALL
Java_com_example_hdmicaptain_render_VideoRenderer_nativeSignal(JNIEnv *env, jclass, jlong handle, jdoubleArray out) {
    hdmi::SignalSample s = renderer(handle)->signal();
    jdouble v[7] = {s.meanY, s.meanCb, s.meanCr, s.rangeY, s.rangeChroma, s.fullRange ? 1.0 : 0.0, double(s.count)};
    env->SetDoubleArrayRegion(out, 0, 7, v);
}

// out = [framesIn, framesDrawn, lastLatencyNs, width, height]
JNIEXPORT void JNICALL
Java_com_example_hdmicaptain_render_VideoRenderer_nativeStats(JNIEnv *env, jclass, jlong handle, jlongArray out) {
    hdmi::RenderStats s = renderer(handle)->stats();
    jlong values[5] = {s.framesIn, s.framesDrawn, s.lastLatencyNs, s.width, s.height};
    env->SetLongArrayRegion(out, 0, 5, values);
}

// Returns RGBA bytes (top row first) and writes [width, height] into size; null when there is no frame yet.
JNIEXPORT jbyteArray JNICALL
Java_com_example_hdmicaptain_render_VideoRenderer_nativeScreenshot(JNIEnv *env, jclass, jlong handle, jintArray size) {
    hdmi::Screenshot shot = renderer(handle)->screenshot();
    if (shot.width == 0) return nullptr;
    jint dims[2] = {shot.width, shot.height};
    env->SetIntArrayRegion(size, 0, 2, dims);
    jbyteArray bytes = env->NewByteArray(jsize(shot.rgba.size()));
    env->SetByteArrayRegion(bytes, 0, jsize(shot.rgba.size()), reinterpret_cast<const jbyte *>(shot.rgba.data()));
    return bytes;
}

}  // extern "C"
