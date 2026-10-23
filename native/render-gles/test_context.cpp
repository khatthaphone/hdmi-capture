// Test-only helper (task 107): a headless EGL context (Mesa surfaceless platform) with a w×h RGBA
// framebuffer, so the renderer's pixel tests run without a window, in WSL and CI.
#include <EGL/egl.h>
#include <EGL/eglext.h>
#include <GLES3/gl3.h>

#include <cstdint>
#include <cstdio>

namespace {
EGLDisplay display = EGL_NO_DISPLAY;
EGLContext context = EGL_NO_CONTEXT;
GLuint framebuffer = 0, colour = 0;
}

extern "C" {

// Makes a GLES 3 context current on this thread with a w×h framebuffer bound. Returns the framebuffer (0 = failed).
uint32_t hc_test_context_create(int32_t width, int32_t height) {
    auto getPlatformDisplay = reinterpret_cast<PFNEGLGETPLATFORMDISPLAYEXTPROC>(eglGetProcAddress("eglGetPlatformDisplayEXT"));
    if (!getPlatformDisplay) return 0;
    display = getPlatformDisplay(EGL_PLATFORM_SURFACELESS_MESA, EGL_DEFAULT_DISPLAY, nullptr);
    if (display == EGL_NO_DISPLAY || !eglInitialize(display, nullptr, nullptr)) return 0;
    eglBindAPI(EGL_OPENGL_ES_API);
    const EGLint attrs[] = {EGL_RENDERABLE_TYPE, EGL_OPENGL_ES3_BIT, EGL_NONE};
    EGLConfig config = nullptr;
    EGLint count = 0;
    eglChooseConfig(display, attrs, &config, 1, &count);
    const EGLint contextAttrs[] = {EGL_CONTEXT_MAJOR_VERSION, 3, EGL_NONE};
    context = eglCreateContext(display, count ? config : EGL_NO_CONFIG_KHR, EGL_NO_CONTEXT, contextAttrs);
    if (context == EGL_NO_CONTEXT || !eglMakeCurrent(display, EGL_NO_SURFACE, EGL_NO_SURFACE, context)) {
        std::fprintf(stderr, "test context: 0x%x\n", eglGetError());
        return 0;
    }
    glGenRenderbuffers(1, &colour);
    glBindRenderbuffer(GL_RENDERBUFFER, colour);
    glRenderbufferStorage(GL_RENDERBUFFER, GL_RGBA8, width, height);
    glGenFramebuffers(1, &framebuffer);
    glBindFramebuffer(GL_FRAMEBUFFER, framebuffer);
    glFramebufferRenderbuffer(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_RENDERBUFFER, colour);
    return glCheckFramebufferStatus(GL_FRAMEBUFFER) == GL_FRAMEBUFFER_COMPLETE ? framebuffer : 0;
}

// Binds the test framebuffer (what a host does before hc_renderer_render).
void hc_test_bind(void) { glBindFramebuffer(GL_FRAMEBUFFER, framebuffer); }

// Reads the test framebuffer, bottom row first (GL order).
void hc_test_read(int32_t width, int32_t height, uint8_t *rgba) {
    glBindFramebuffer(GL_FRAMEBUFFER, framebuffer);
    glPixelStorei(GL_PACK_ALIGNMENT, 1);
    glReadPixels(0, 0, width, height, GL_RGBA, GL_UNSIGNED_BYTE, rgba);
}

const char *hc_test_renderer_name(void) { return reinterpret_cast<const char *>(glGetString(GL_RENDERER)); }

void hc_test_context_destroy(void) {
    if (framebuffer) glDeleteFramebuffers(1, &framebuffer);
    if (colour) glDeleteRenderbuffers(1, &colour);
    framebuffer = colour = 0;
    if (display != EGL_NO_DISPLAY) {
        eglMakeCurrent(display, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT);
        if (context != EGL_NO_CONTEXT) eglDestroyContext(display, context);
        eglTerminate(display);
    }
    display = EGL_NO_DISPLAY;
    context = EGL_NO_CONTEXT;
}

}  // extern "C"
