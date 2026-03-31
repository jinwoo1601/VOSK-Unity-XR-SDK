cmake_minimum_required(VERSION 3.18)
project(vosk-bridge LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# --- Source files ---

set(BRIDGE_SOURCES
    src/vosk_bridge.cpp
    src/audio_capture_aaudio.cpp
)

set(BRIDGE_HEADERS
    src/vosk_bridge.h
    src/ring_buffer.h
    src/downsampler.h
    src/result_queue.h
    src/audio_capture_aaudio.h
    include/vosk_api.h
)

# --- Shared library ---

add_library(vosk-bridge SHARED ${BRIDGE_SOURCES} ${BRIDGE_HEADERS})

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
    aaudio
    log
)

# Catch missing symbols at link time
set_target_properties(vosk-bridge PROPERTIES
    LINK_FLAGS "-Wl,--no-undefined"
)
