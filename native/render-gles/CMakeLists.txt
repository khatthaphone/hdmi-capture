cmake_minimum_required(VERSION 3.22)
project(hcrender LANGUAGES CXX)

# Shared GLES 3 renderer (task 107). Linux builds libhcrender.so with a C ABI (hc_render.h) for hosts
# that own the GL context (GtkGLArea). Android still builds its own copy in Apps/Android/core/render
# until it is switched to these sources.
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

find_package(PkgConfig REQUIRED)
pkg_check_modules(GLES REQUIRED IMPORTED_TARGET glesv2)

add_library(hcrender SHARED gl_pipeline.cpp hc_render.cpp test_pattern.cpp)
target_compile_options(hcrender PRIVATE -Wall -Wextra -Werror)
target_link_libraries(hcrender PRIVATE PkgConfig::GLES)

# Pixel tests run on an EGL surfaceless context (Mesa), so they need EGL too.
option(HC_RENDER_TESTS "Build the EGL pixel-test helper" ON)
if(HC_RENDER_TESTS)
  pkg_check_modules(EGL REQUIRED IMPORTED_TARGET egl)
  add_library(hcrender_testctx SHARED test_context.cpp)
  target_compile_options(hcrender_testctx PRIVATE -Wall -Wextra -Werror)
  target_link_libraries(hcrender_testctx PRIVATE PkgConfig::EGL PkgConfig::GLES)
endif()
