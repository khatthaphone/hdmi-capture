cmake_minimum_required(VERSION 3.22.1)
project(hdmirender LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_library(hdmirender SHARED render_jni.cpp)
# 16 KB pages: NDK r28+ does this by default; keep it explicit so a toolchain change can't regress it.
target_link_options(hdmirender PRIVATE "-Wl,-z,max-page-size=16384")
target_link_libraries(hdmirender android log EGL GLESv3)
