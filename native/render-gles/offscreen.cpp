// A headless GLES 3 context for renderers that draw for an encoder instead of a window (recordings, task 120):
// Mesa's EGL surfaceless platform, current on the creating thread. Same setup as the pixel tests' context.
#include <EGL/egl.h>
#include <EGL/eglext.h>
#include <GLES3/gl3.h>

#include "hc_render.h"
#include "platform.h"

struct hc_offscreen {
    EGLDisplay display = EGL_NO_DISPLAY;
    EGLContext context = EGL_NO_CONTEXT;
};

extern "C" {

hc_offscreen *hc_offscreen_create(void) {
    auto getPlatformDisplay = reinterpret_cast<PFNEGLGETPLATFORMDISPLAYEXTPROC>(eglGetProcAddress("eglGetPlatformDisplayEXT"));
    if (!getPlatformDisplay) return nullptr;
    auto *o = new hc_offscreen();
    o->display = getPlatformDisplay(EGL_PLATFORM_SURFACELESS_MESA, EGL_DEFAULT_DISPLAY, nullptr);
    if (o->display == EGL_NO_DISPLAY || !eglInitialize(o->display, nullptr, nullptr)) {
        HC_LOGE("offscreen: no EGL surfaceless display (0x%x)", eglGetError());
        delete o;
        return nullptr;
    }
    eglBindAPI(EGL_OPENGL_ES_API);
    const EGLint attrs[] = {EGL_RENDERABLE_TYPE, EGL_OPENGL_ES3_BIT, EGL_NONE};
    EGLConfig config = nullptr;
    EGLint count = 0;
    eglChooseConfig(o->display, attrs, &config, 1, &count);
    const EGLint contextAttrs[] = {EGL_CONTEXT_MAJOR_VERSION, 3, EGL_NONE};
    o->context = eglCreateContext(o->display, count ? config : EGL_NO_CONFIG_KHR, EGL_NO_CONTEXT, contextAttrs);
    if (o->context == EGL_NO_CONTEXT || !eglMakeCurrent(o->display, EGL_NO_SURFACE, EGL_NO_SURFACE, o->context)) {
        HC_LOGE("offscreen: no GLES 3 context (0x%x)", eglGetError());
        hc_offscreen_destroy(o);
        return nullptr;
    }
    return o;
}

const char *hc_offscreen_renderer_name(hc_offscreen *) { return reinterpret_cast<const char *>(glGetString(GL_RENDERER)); }

void hc_offscreen_destroy(hc_offscreen *o) {
    if (!o) return;
    if (o->display != EGL_NO_DISPLAY) {
        eglMakeCurrent(o->display, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT);
        if (o->context != EGL_NO_CONTEXT) eglDestroyContext(o->display, o->context);
        // No eglTerminate: the display is shared per process with other surfaceless users (the pixel tests).
    }
    delete o;
}

}  // extern "C"
