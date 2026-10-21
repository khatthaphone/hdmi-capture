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

# libjpeg-turbo: BSD/IJG, static. Its build refuses add_subdirectory(), so it is an external
# project built with the same NDK toolchain. SIMD needs NASM on x86_64 (emulator only): off there.
include(ExternalProject)
set(JPEG_BUILD ${CMAKE_CURRENT_BINARY_DIR}/libjpeg-turbo)
if(ANDROID_ABI STREQUAL "x86_64")
    set(JPEG_SIMD OFF)
else()
    set(JPEG_SIMD ON)
endif()
ExternalProject_Add(libjpeg-turbo-build
    SOURCE_DIR ${CMAKE_CURRENT_SOURCE_DIR}/third_party/libjpeg-turbo
    BINARY_DIR ${JPEG_BUILD}
    CMAKE_ARGS
        -DCMAKE_TOOLCHAIN_FILE=${CMAKE_TOOLCHAIN_FILE}
        -DANDROID_ABI=${ANDROID_ABI}
        -DANDROID_PLATFORM=${ANDROID_PLATFORM}
        -DCMAKE_BUILD_TYPE=Release
        -DCMAKE_POSITION_INDEPENDENT_CODE=ON
        -DENABLE_SHARED=OFF -DENABLE_STATIC=ON -DWITH_TURBOJPEG=ON
        -DWITH_TOOLS=OFF -DWITH_TESTS=OFF -DWITH_FUZZ=OFF
        -DWITH_SIMD=${JPEG_SIMD} -DREQUIRE_SIMD=${JPEG_SIMD}
    BUILD_COMMAND ${CMAKE_COMMAND} --build ${JPEG_BUILD} --target turbojpeg-static
    INSTALL_COMMAND ""
    BUILD_BYPRODUCTS ${JPEG_BUILD}/libturbojpeg.a)
add_library(turbojpeg STATIC IMPORTED)
set_target_properties(turbojpeg PROPERTIES IMPORTED_LOCATION ${JPEG_BUILD}/libturbojpeg.a)
add_dependencies(turbojpeg libjpeg-turbo-build)

add_library(hdmicapture SHARED capture_jni.cpp uvc_source.cpp mjpeg_decoder.cpp)
add_dependencies(hdmicapture libjpeg-turbo-build)
target_include_directories(hdmicapture PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/third_party/libjpeg-turbo/src)
# Frames go straight into the renderer's FrameRing (header-only, shared with core:render).
target_include_directories(hdmicapture PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../../../../render/src/main/cpp)
target_compile_options(hdmicapture PRIVATE -Wall -D__ANDROID_UNAVAILABLE_SYMBOLS_ARE_WEAK__ -Werror=unguarded-availability)
target_link_libraries(hdmicapture uvc turbojpeg android log)
