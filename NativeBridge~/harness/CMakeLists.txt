# ============================================================================
# Purpose:  Build the WSL WAV->transcript harness CLI (Tier C)
# Layer:    NativeBridge.Harness
# Owns:     vosk-bridge-harness (executable target)
# Depends:  vosk-bridge (shared library target), nlohmann/json (vendored)
# ============================================================================

add_executable(vosk-bridge-harness
    main.cpp
    wav_reader.cpp
)

target_include_directories(vosk-bridge-harness PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/../src
    ${CMAKE_CURRENT_SOURCE_DIR}/third_party
)

target_link_libraries(vosk-bridge-harness PRIVATE vosk-bridge)
