#pragma once

// The few things the GL pipeline needs from its platform: logging and trace sections.

#if defined(__ANDROID__)
#include <android/log.h>
#include <android/trace.h>
#define HC_LOGI(...) __android_log_print(ANDROID_LOG_INFO, "hdmirender", __VA_ARGS__)
#define HC_LOGE(...) __android_log_print(ANDROID_LOG_ERROR, "hdmirender", __VA_ARGS__)
#define HC_TRACE_BEGIN(name) ATrace_beginSection(name)
#define HC_TRACE_END() ATrace_endSection()
#else
#include <cstdio>
#define HC_LOGI(...) (std::fprintf(stderr, "hdmirender: " __VA_ARGS__), std::fputc('\n', stderr))
#define HC_LOGE(...) (std::fprintf(stderr, "hdmirender error: " __VA_ARGS__), std::fputc('\n', stderr))
#define HC_TRACE_BEGIN(name) ((void) 0)
#define HC_TRACE_END() ((void) 0)
#endif

#include <cstdint>
#include <ctime>

namespace hdmi {

inline int64_t nowNs() {
    timespec ts{};
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return int64_t(ts.tv_sec) * 1000000000 + ts.tv_nsec;
}

}  // namespace hdmi
