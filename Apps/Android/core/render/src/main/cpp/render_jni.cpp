#include <jni.h>

// JNI entry points for com.example.hdmicaptain.render.NativeRender.
// Frames never cross JNI: only setup and control calls do.

extern "C" JNIEXPORT jstring JNICALL
Java_com_example_hdmicaptain_render_NativeRender_nativeVersion(JNIEnv *env, jclass) {
    return env->NewStringUTF("hdmirender 0.1 (GLES 3)");
}
