cmake_minimum_required(VERSION 3.18)
project(vosk-bridge LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# --- Capture backend selection ---

# Picks which capture backend is compiled and which header audio_capture.h
# forwards to. The default keeps the shipped Android build unchanged.
# NOTE: aaudio is unmaintained legacy (broken on Quest — see KNOWN_LIMITATIONS).
set(VOSK_BRIDGE_CAPTURE "audiorecord" CACHE STRING "Capture backend: audiorecord|aaudio|stub")
set_property(CACHE VOSK_BRIDGE_CAPTURE PROPERTY STRINGS audiorecord aaudio stub)
if(NOT VOSK_BRIDGE_CAPTURE MATCHES "^(audiorecord|aaudio|stub)$")
    message(FATAL_ERROR "VOSK_BRIDGE_CAPTURE must be audiorecord, aaudio, or stub (got '${VOSK_BRIDGE_CAPTURE}')")
endif()
string(TOUPPER "${VOSK_BRIDGE_CAPTURE}" VOSK_BRIDGE_CAPTURE_UPPER)

# --- Source files ---

set(BRIDGE_SOURCES
    src/vosk_bridge.cpp
    src/audio_capture_${VOSK_BRIDGE_CAPTURE}.cpp
)

set(BRIDGE_HEADERS
    src/vosk_bridge.h
    src/ring_buffer.h
    src/downsampler.h
    src/agc.h
    src/result_queue.h
    src/audio_capture.h
    src/audio_capture_${VOSK_BRIDGE_CAPTURE}.h
    include/vosk_api.h
)

# --- Shared library ---

add_library(vosk-bridge SHARED ${BRIDGE_SOURCES} ${BRIDGE_HEADERS})

target_compile_definitions(vosk-bridge PRIVATE VOSK_BRIDGE_CAPTURE_${VOSK_BRIDGE_CAPTURE_UPPER})

target_include_directories(vosk-bridge PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/src
    ${CMAKE_CURRENT_SOURCE_DIR}/include
)

# --- Link dependencies ---

# libvosk.so — prebuilt VOSK library.
# Set VOSK_LIB_DIR to the directory containing libvosk.so for the target ABI.
# Default: look next to the output in the Plugins directory.
if(NOT DEFINED VOSK_LIB_DIR)
    set(VOSK_LIB_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../Runtime/Plugins/Android/${ANDROID_ABI}")
endif()

add_library(vosk SHARED IMPORTED)
set_target_properties(vosk PROPERTIES
    IMPORTED_LOCATION "${VOSK_LIB_DIR}/libvosk.so"
)

target_link_libraries(vosk-bridge PRIVATE
    vosk
)

if(ANDROID)
    target_link_libraries(vosk-bridge PRIVATE log)
endif()

# Hide internal symbols; only extern "C" exports remain visible.
set_target_properties(vosk-bridge PROPERTIES
    C_VISIBILITY_PRESET hidden
    CXX_VISIBILITY_PRESET hidden
    LINK_FLAGS "-Wl,--no-undefined -Wl,--strip-all"
)

# LTO via -flto=thin (NDK uses lld, not gold, so CMake's IPO property won't
# work). clang/lld-specific — Android only; GCC on desktop would reject it.
if(ANDROID)
    target_compile_options(vosk-bridge PRIVATE $<$<CONFIG:Release>:-flto=thin>)
    target_link_options(vosk-bridge PRIVATE $<$<CONFIG:Release>:-flto=thin>)
endif()

# --- WSL harness (Tier C) ---

option(VOSK_BRIDGE_BUILD_HARNESS "Build the WAV->transcript harness CLI (desktop preset)" OFF)
if(VOSK_BRIDGE_BUILD_HARNESS)
    add_subdirectory(harness)
endif()
