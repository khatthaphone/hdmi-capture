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
