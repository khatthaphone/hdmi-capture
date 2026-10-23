#include "hc_v4l2.h"

#include <errno.h>
#include <fcntl.h>
#include <linux/videodev2.h>
#include <poll.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/mman.h>
#include <unistd.h>

#define MAX_BUFFERS 16

struct hc_v4l2 {
    int fd;
    int streaming;
    int count;
    void *start[MAX_BUFFERS];
    size_t length[MAX_BUFFERS];
};

static int xioctl(int fd, unsigned long request, void *arg) {
    int r;
    do r = ioctl(fd, request, arg);
    while (r == -1 && errno == EINTR);
    return r == -1 ? -errno : 0;
}

// The kernel fields are at least `size` bytes and NUL-padded; copy them whole and terminate.
static void copy_str(char *dst, size_t size, const __u8 *src) {
    memcpy(dst, src, size - 1);
    dst[size - 1] = 0;
}

int32_t hc_v4l2_struct_size(int32_t which) {
    switch (which) {
        case 0: return (int32_t) sizeof(hc_v4l2_info);
        case 1: return (int32_t) sizeof(hc_v4l2_format);
        case 2: return (int32_t) sizeof(hc_v4l2_actual);
        case 3: return (int32_t) sizeof(hc_v4l2_buffer);
        default: return -1;
    }
}

int32_t hc_v4l2_query(const char *path, hc_v4l2_info *info) {
    int fd = open(path, O_RDWR | O_NONBLOCK | O_CLOEXEC);
    if (fd < 0) return -errno;
    struct v4l2_capability cap;
    memset(&cap, 0, sizeof cap);
    int r = xioctl(fd, VIDIOC_QUERYCAP, &cap);
    close(fd);
    if (r < 0) return r;
    memset(info, 0, sizeof *info);
    copy_str(info->driver, sizeof info->driver, cap.driver);
    copy_str(info->card, sizeof info->card, cap.card);
    copy_str(info->bus_info, sizeof info->bus_info, cap.bus_info);
    info->capabilities = (cap.capabilities & V4L2_CAP_DEVICE_CAPS) ? cap.device_caps : cap.capabilities;
    info->version = cap.version;
    return 0;
}

int32_t hc_v4l2_enum_formats(const char *path, hc_v4l2_format *out, int32_t max) {
    int fd = open(path, O_RDWR | O_NONBLOCK | O_CLOEXEC);
    if (fd < 0) return -errno;
    int n = 0;
    for (__u32 fi = 0;; fi++) {
        struct v4l2_fmtdesc desc = {.index = fi, .type = V4L2_BUF_TYPE_VIDEO_CAPTURE};
        if (xioctl(fd, VIDIOC_ENUM_FMT, &desc) < 0) break;
        for (__u32 si = 0;; si++) {
            struct v4l2_frmsizeenum size = {.index = si, .pixel_format = desc.pixelformat};
            if (xioctl(fd, VIDIOC_ENUM_FRAMESIZES, &size) < 0) break;
            if (size.type != V4L2_FRMSIZE_TYPE_DISCRETE) break;  // capture cards list discrete sizes
            if (n < max) {
                hc_v4l2_format *f = &out[n];
                memset(f, 0, sizeof *f);
                f->fourcc = desc.pixelformat;
                f->flags = desc.flags;
                f->width = (int32_t) size.discrete.width;
                f->height = (int32_t) size.discrete.height;
                for (__u32 ii = 0; ii < HC_V4L2_MAX_INTERVALS; ii++) {
                    struct v4l2_frmivalenum ival = {.index = ii, .pixel_format = desc.pixelformat,
                                                    .width = size.discrete.width, .height = size.discrete.height};
                    if (xioctl(fd, VIDIOC_ENUM_FRAMEINTERVALS, &ival) < 0) break;
                    if (ival.type == V4L2_FRMIVAL_TYPE_DISCRETE) {
                        f->interval_num[f->interval_count] = ival.discrete.numerator;
                        f->interval_den[f->interval_count] = ival.discrete.denominator;
                        f->interval_count++;
                    } else {  // stepwise/continuous: report the fastest
                        f->interval_num[f->interval_count] = ival.stepwise.min.numerator;
                        f->interval_den[f->interval_count] = ival.stepwise.min.denominator;
                        f->interval_count++;
                        break;
                    }
                }
            }
            n++;
        }
    }
    close(fd);
    return n;
}

hc_v4l2 *hc_v4l2_open(const char *path, int32_t *error) {
    int fd = open(path, O_RDWR | O_NONBLOCK | O_CLOEXEC);
    if (fd < 0) {
        if (error) *error = -errno;
        return NULL;
    }
    hc_v4l2 *v = calloc(1, sizeof *v);
    v->fd = fd;
    if (error) *error = 0;
    return v;
}

static void release_buffers(hc_v4l2 *v) {
    for (int i = 0; i < v->count; i++)
        if (v->start[i] && v->start[i] != MAP_FAILED) munmap(v->start[i], v->length[i]);
    memset(v->start, 0, sizeof v->start);
    v->count = 0;
    struct v4l2_requestbuffers none = {.count = 0, .type = V4L2_BUF_TYPE_VIDEO_CAPTURE, .memory = V4L2_MEMORY_MMAP};
    xioctl(v->fd, VIDIOC_REQBUFS, &none);
}

int32_t hc_v4l2_start(hc_v4l2 *v, uint32_t fourcc, int32_t width, int32_t height, uint32_t interval_num, uint32_t interval_den,
                      int32_t buffers, hc_v4l2_actual *actual) {
    if (v->streaming) return -EBUSY;
    struct v4l2_format fmt = {.type = V4L2_BUF_TYPE_VIDEO_CAPTURE};
    fmt.fmt.pix.width = (__u32) width;
    fmt.fmt.pix.height = (__u32) height;
    fmt.fmt.pix.pixelformat = fourcc;
    fmt.fmt.pix.field = V4L2_FIELD_ANY;
    int r = xioctl(v->fd, VIDIOC_S_FMT, &fmt);
    if (r < 0) return r;

    struct v4l2_streamparm parm = {.type = V4L2_BUF_TYPE_VIDEO_CAPTURE};
    if (interval_num && interval_den) {
        parm.parm.capture.timeperframe.numerator = interval_num;
        parm.parm.capture.timeperframe.denominator = interval_den;
        xioctl(v->fd, VIDIOC_S_PARM, &parm);  // best effort; G_PARM below reports what stuck
    }
    memset(&parm, 0, sizeof parm);
    parm.type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
    xioctl(v->fd, VIDIOC_G_PARM, &parm);

    if (buffers < 2) buffers = 2;
    if (buffers > MAX_BUFFERS) buffers = MAX_BUFFERS;
    struct v4l2_requestbuffers req = {.count = (__u32) buffers, .type = V4L2_BUF_TYPE_VIDEO_CAPTURE, .memory = V4L2_MEMORY_MMAP};
    r = xioctl(v->fd, VIDIOC_REQBUFS, &req);
    if (r < 0) return r;
    if (req.count < 2) {
        release_buffers(v);
        return -ENOMEM;
    }
    v->count = (int) (req.count > MAX_BUFFERS ? MAX_BUFFERS : req.count);
    for (int i = 0; i < v->count; i++) {
        struct v4l2_buffer buf = {.index = (__u32) i, .type = V4L2_BUF_TYPE_VIDEO_CAPTURE, .memory = V4L2_MEMORY_MMAP};
        r = xioctl(v->fd, VIDIOC_QUERYBUF, &buf);
        if (r < 0) goto fail;
        v->length[i] = buf.length;
        v->start[i] = mmap(NULL, buf.length, PROT_READ | PROT_WRITE, MAP_SHARED, v->fd, buf.m.offset);
        if (v->start[i] == MAP_FAILED) {
            r = -errno;
            goto fail;
        }
        r = xioctl(v->fd, VIDIOC_QBUF, &buf);
        if (r < 0) goto fail;
    }
    enum v4l2_buf_type type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
    r = xioctl(v->fd, VIDIOC_STREAMON, &type);
    if (r < 0) goto fail;
    v->streaming = 1;
    if (actual) {
        memset(actual, 0, sizeof *actual);
        actual->fourcc = fmt.fmt.pix.pixelformat;
        actual->width = (int32_t) fmt.fmt.pix.width;
        actual->height = (int32_t) fmt.fmt.pix.height;
        actual->bytes_per_line = fmt.fmt.pix.bytesperline;
        actual->size_image = fmt.fmt.pix.sizeimage;
        actual->interval_num = parm.parm.capture.timeperframe.numerator;
        actual->interval_den = parm.parm.capture.timeperframe.denominator;
        actual->buffer_count = v->count;
    }
    return 0;
fail:
    release_buffers(v);
    return r;
}

int32_t hc_v4l2_dequeue(hc_v4l2 *v, int32_t timeout_ms, hc_v4l2_buffer *out) {
    if (!v->streaming) return -EINVAL;
    struct pollfd p = {.fd = v->fd, .events = POLLIN};
    int pr;
    do pr = poll(&p, 1, timeout_ms);
    while (pr < 0 && errno == EINTR);
    if (pr < 0) return -errno;
    if (pr == 0) return 0;
    // Drain: keep only the newest ready buffer, requeue the rest unseen.
    struct v4l2_buffer newest;
    int have = 0, skipped = 0;
    for (;;) {
        struct v4l2_buffer buf = {.type = V4L2_BUF_TYPE_VIDEO_CAPTURE, .memory = V4L2_MEMORY_MMAP};
        int r = xioctl(v->fd, VIDIOC_DQBUF, &buf);
        if (r == -EAGAIN) break;
        if (r < 0) {
            if (have) xioctl(v->fd, VIDIOC_QBUF, &newest);
            return r;
        }
        if (have) {
            xioctl(v->fd, VIDIOC_QBUF, &newest);
            skipped++;
        }
        newest = buf;
        have = 1;
    }
    if (!have) {
        // uvcvideo signals an unplug with POLLERR and nothing to dequeue.
        if (p.revents & (POLLERR | POLLHUP | POLLNVAL)) return -ENODEV;
        return 0;
    }
    out->index = (int32_t) newest.index;
    out->skipped = skipped;
    out->data = (const uint8_t *) v->start[newest.index];
    out->bytes_used = newest.bytesused;
    out->flags = newest.flags;
    out->sequence = newest.sequence;
    out->reserved = 0;
    out->timestamp_ns = (int64_t) newest.timestamp.tv_sec * 1000000000LL + (int64_t) newest.timestamp.tv_usec * 1000LL;
    return 1;
}

int32_t hc_v4l2_requeue(hc_v4l2 *v, int32_t index) {
    if (!v->streaming || index < 0 || index >= v->count) return -EINVAL;
    struct v4l2_buffer buf = {.index = (__u32) index, .type = V4L2_BUF_TYPE_VIDEO_CAPTURE, .memory = V4L2_MEMORY_MMAP};
    return xioctl(v->fd, VIDIOC_QBUF, &buf);
}

void hc_v4l2_stop(hc_v4l2 *v) {
    if (!v->streaming) return;
    enum v4l2_buf_type type = V4L2_BUF_TYPE_VIDEO_CAPTURE;
    xioctl(v->fd, VIDIOC_STREAMOFF, &type);
    release_buffers(v);
    v->streaming = 0;
}

void hc_v4l2_close(hc_v4l2 *v) {
    if (!v) return;
    hc_v4l2_stop(v);
    close(v->fd);
    free(v);
}
