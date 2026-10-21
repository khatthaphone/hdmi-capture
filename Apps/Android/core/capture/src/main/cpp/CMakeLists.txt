cmake_minimum_required(VERSION 3.22.1)
project(hdmicapture LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# 16 KB pages (Play requirement since 2025-11): NDK r28+ default, kept explicit.
add_link_options("-Wl,-z,max-page-size=16384")

set(LIBUSB ${CMAKE_CURRENT_SOURCE_DIR}/third_party/libusb)
set(LIBUVC ${CMAKE_CURRENT_SOURCE_DIR}/third_party/libuvc)

# libusb: LGPL-2.1, so it stays its own replaceable shared library.
add_library(usb1.0 SHARED
    ${LIBUSB}/libusb/core.c ${LIBUSB}/libusb/descriptor.c ${LIBUSB}/libusb/hotplug.c ${LIBUSB}/libusb/io.c
    ${LIBUSB}/libusb/sync.c ${LIBUSB}/libusb/strerror.c ${LIBUSB}/libusb/os/linux_usbfs.c
    ${LIBUSB}/libusb/os/events_posix.c ${LIBUSB}/libusb/os/threads_posix.c ${LIBUSB}/libusb/os/linux_netlink.c)
target_include_directories(usb1.0 PUBLIC ${LIBUSB}/libusb PRIVATE ${LIBUSB}/android ${LIBUSB}/libusb/os)
target_compile_options(usb1.0 PRIVATE -fvisibility=hidden -Wno-unused-parameter)
target_link_libraries(usb1.0 log)

# libuvc: BSD, static inside libhdmicapture. Built without JPEG (we decode MJPEG ourselves).
add_library(uvc STATIC
    ${LIBUVC}/src/ctrl.c ${LIBUVC}/src/ctrl-gen.c ${LIBUVC}/src/device.c ${LIBUVC}/src/diag.c
    ${LIBUVC}/src/frame.c ${LIBUVC}/src/init.c ${LIBUVC}/src/stream.c ${LIBUVC}/src/misc.c)
target_include_directories(uvc PUBLIC ${LIBUVC}/include)
target_compile_options(uvc PRIVATE -Wno-unused-parameter -Wno-unused-function -Wno-unused-variable)
target_link_libraries(uvc PUBLIC usb1.0 log)

add_library(hdmicapture SHARED capture_jni.cpp uvc_source.cpp)
# Frames go straight into the renderer's FrameRing (header-only, shared with core:render).
target_include_directories(hdmicapture PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../../../../render/src/main/cpp)
target_compile_options(hdmicapture PRIVATE -Wall -D__ANDROID_UNAVAILABLE_SYMBOLS_ARE_WEAK__ -Werror=unguarded-availability)
target_link_libraries(hdmicapture uvc android log)
