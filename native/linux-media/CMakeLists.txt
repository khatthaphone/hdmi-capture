cmake_minimum_required(VERSION 3.22)
project(hcmedia LANGUAGES C)

# Linux capture helpers (task 108): a thin C layer over V4L2 so the kernel struct layouts stay in C.
set(CMAKE_C_STANDARD 11)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

add_library(hcmedia SHARED hc_v4l2.c)
target_compile_options(hcmedia PRIVATE -Wall -Wextra -Werror)
