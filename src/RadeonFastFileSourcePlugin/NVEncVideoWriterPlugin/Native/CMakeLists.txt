cmake_minimum_required(VERSION 3.20)

project(RadeonFastNative C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

add_library(RadeonFastNativeAudio SHARED
    RadeonFastNativeAudio.c
)

add_library(RadeonFastNativeImage SHARED
    RadeonFastNativeImage.c
)

add_library(RadeonFastNativeVideo SHARED
    RadeonFastNativeVideo.c
)

target_include_directories(RadeonFastNativeAudio PRIVATE
    "${CMAKE_CURRENT_LIST_DIR}/miniaudio"
)

if(MSVC)
    target_compile_options(RadeonFastNativeAudio PRIVATE /O2 /utf-8)
    target_compile_definitions(RadeonFastNativeAudio PRIVATE _CRT_SECURE_NO_WARNINGS)
    target_compile_options(RadeonFastNativeImage PRIVATE /O2 /utf-8)
    target_compile_definitions(RadeonFastNativeImage PRIVATE _CRT_SECURE_NO_WARNINGS)
    target_compile_options(RadeonFastNativeVideo PRIVATE /O2 /utf-8)
    target_compile_definitions(RadeonFastNativeVideo PRIVATE _CRT_SECURE_NO_WARNINGS)
endif()

target_include_directories(RadeonFastNativeImage PRIVATE
    "${CMAKE_CURRENT_LIST_DIR}/../../vendor/vips-dev-8.18/include"
    "${CMAKE_CURRENT_LIST_DIR}/../../vendor/vips-dev-8.18/include/glib-2.0"
    "${CMAKE_CURRENT_LIST_DIR}/../../vendor/vips-dev-8.18/lib/glib-2.0/include"
)

target_link_directories(RadeonFastNativeImage PRIVATE
    "${CMAKE_CURRENT_LIST_DIR}/../../vendor/vips-dev-8.18/lib"
)

target_link_libraries(RadeonFastNativeImage PRIVATE
    libvips
    libglib-2.0
    libgobject-2.0
)
