cmake_minimum_required(VERSION 3.22.1)
project(hdmiaudio LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

find_package(oboe REQUIRED CONFIG)

add_library(hdmiaudio SHARED audio_jni.cpp card_audio.cpp)
target_compile_options(hdmiaudio PRIVATE -Wall -O2)
# 16 KB pages (Play requirement since 2025-11): NDK r28+ default, kept explicit.
target_link_options(hdmiaudio PRIVATE "-Wl,-z,max-page-size=16384")
target_link_libraries(hdmiaudio oboe::oboe android log)
