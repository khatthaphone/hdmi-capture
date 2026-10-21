#ifndef LIBUVC_CONFIG_H
#define LIBUVC_CONFIG_H

/* Generated for HDMI Captain from libuvc_config.h.in (v0.0.8). MJPEG is decoded by us
   (libjpeg-turbo, task 074), so libuvc is built without its JPEG helpers. */
#define LIBUVC_VERSION_MAJOR 0
#define LIBUVC_VERSION_MINOR 0
#define LIBUVC_VERSION_PATCH 8
#define LIBUVC_VERSION_STR "0.0.8"
#define LIBUVC_VERSION_INT ((0 << 16) | (0 << 8) | (8))

#define LIBUVC_VERSION_GTE(major, minor, patch) \
  (LIBUVC_VERSION_INT >= (((major) << 16) | ((minor) << 8) | (patch)))

#endif // !def(LIBUVC_CONFIG_H)
