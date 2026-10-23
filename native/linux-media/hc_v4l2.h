#pragma once

// Thin C layer over V4L2 (task 108): the kernel's struct layouts stay in C, C# sees flat records.
// All functions return 0 / a count on success and -errno on failure.

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define HC_V4L2_MAX_INTERVALS 16

typedef struct {
    char driver[16];
    char card[32];
    char bus_info[32];
    uint32_t capabilities;   // device_caps when V4L2_CAP_DEVICE_CAPS is set
    uint32_t version;
} hc_v4l2_info;

// One (pixel format, frame size) pair with its discrete frame intervals (num/den seconds).
typedef struct {
    uint32_t fourcc;
    uint32_t flags;          // V4L2_FMT_FLAG_*: 1 = compressed
    int32_t width, height;
    int32_t interval_count;
    uint32_t interval_num[HC_V4L2_MAX_INTERVALS];
    uint32_t interval_den[HC_V4L2_MAX_INTERVALS];
} hc_v4l2_format;

typedef struct {
    uint32_t fourcc;
    int32_t width, height;
    uint32_t bytes_per_line, size_image;
    uint32_t interval_num, interval_den;  // 0/0 when the driver has none
    int32_t buffer_count;
} hc_v4l2_actual;

typedef struct {
    int32_t index;
    int32_t skipped;         // older ready buffers requeued unseen (newest frame wins)
    const uint8_t *data;
    uint32_t bytes_used;
    uint32_t flags;          // V4L2_BUF_FLAG_*: 0x40 = error
    uint32_t sequence;
    uint32_t reserved;
    int64_t timestamp_ns;    // CLOCK_MONOTONIC (uvcvideo), 0 if the driver has none
} hc_v4l2_buffer;

typedef struct hc_v4l2 hc_v4l2;

int32_t hc_v4l2_query(const char *path, hc_v4l2_info *info);
// Fills up to max entries; returns how many exist (may exceed max).
int32_t hc_v4l2_enum_formats(const char *path, hc_v4l2_format *out, int32_t max);

hc_v4l2 *hc_v4l2_open(const char *path, int32_t *error);
// S_FMT + S_PARM + REQBUFS(MMAP) + mmap + QBUF all + STREAMON.
int32_t hc_v4l2_start(hc_v4l2 *v, uint32_t fourcc, int32_t width, int32_t height, uint32_t interval_num, uint32_t interval_den,
                      int32_t buffers, hc_v4l2_actual *actual);
// Waits up to timeout_ms; returns 1 with the newest ready buffer (hold it until hc_v4l2_requeue), 0 on timeout.
// -ENODEV/-EIO mean the device went away.
int32_t hc_v4l2_dequeue(hc_v4l2 *v, int32_t timeout_ms, hc_v4l2_buffer *out);
int32_t hc_v4l2_requeue(hc_v4l2 *v, int32_t index);
void hc_v4l2_stop(hc_v4l2 *v);
void hc_v4l2_close(hc_v4l2 *v);
// sizeof checks for the C# mirror structs (tests).
int32_t hc_v4l2_struct_size(int32_t which);

#ifdef __cplusplus
}
#endif
