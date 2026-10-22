# HDMI Captain virtual camera (task 104): a Windows 11 MFCreateVirtualCamera media source
# that generates SMPTE bars with a frame counter, plus the `vcam` console tool.
#
#   cmake -S . -B build -G "Visual Studio 17 2022" -A x64
#   cmake --build build --config Release
#
# Or `make win-vcam` from the repo root. Registering the DLL needs an elevated
# `regsvr32 HDMICaptainVCam.dll` (Frame Server only reads HKLM).
cmake_minimum_required(VERSION 3.24)
project(HDMICaptainVCam LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# Static CRT: the DLL is loaded into Frame Server and every camera app; don't depend on the VC++ redist.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
# DLL and exe side by side (vcam finds HDMICaptainVCam.dll next to itself).
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/out)
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/out)

add_compile_options(/W4 /WX /permissive- /EHsc /utf-8 /Zc:__cplusplus)
add_compile_definitions(UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
add_link_options(/WX)

add_library(vcam_pattern STATIC src/Pattern.cpp)

add_library(HDMICaptainVCam SHARED
    src/Dll.cpp
    src/MediaSource.cpp
    src/MediaStream.cpp
    src/Guids.cpp
    src/HDMICaptainVCam.def)
target_link_libraries(HDMICaptainVCam PRIVATE vcam_pattern mfplat mfuuid mfsensorgroup ole32 advapi32 propsys)

add_executable(vcam tools/vcam.cpp src/Guids.cpp)
target_link_libraries(vcam PRIVATE vcam_pattern mfplat mf mfreadwrite mfuuid mfsensorgroup ole32 advapi32 propsys)
add_dependencies(vcam HDMICaptainVCam)
