#!/usr/bin/env bash
set -euo pipefail

# Build libvosk-bridge.so for Android arm64-v8a.
#
# Prerequisites:
#   - Android NDK r26+ installed
#   - libvosk.so placed in ../Runtime/Plugins/Android/arm64-v8a/
#   - CMake 3.18+
#
# Usage:
#   export ANDROID_NDK_HOME=/path/to/ndk
#   ./build.sh
#
# Output:
#   ../Runtime/Plugins/Android/arm64-v8a/libvosk-bridge.so

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BUILD_DIR="${SCRIPT_DIR}/build"
OUTPUT_DIR="${SCRIPT_DIR}/../Runtime/Plugins/Android/arm64-v8a"

# Locate NDK
if [ -z "${ANDROID_NDK_HOME:-}" ]; then
    echo "Error: ANDROID_NDK_HOME is not set."
    echo "Set it to your Android NDK installation path, e.g.:"
    echo "  export ANDROID_NDK_HOME=\$HOME/Android/Sdk/ndk/26.1.10909125"
    exit 1
fi

TOOLCHAIN="${ANDROID_NDK_HOME}/build/cmake/android.toolchain.cmake"
if [ ! -f "$TOOLCHAIN" ]; then
    echo "Error: NDK toolchain not found at ${TOOLCHAIN}"
    exit 1
fi

# Check that libvosk.so is present
if [ ! -f "${OUTPUT_DIR}/libvosk.so" ]; then
    echo "Error: libvosk.so not found in ${OUTPUT_DIR}"
    echo "Download the VOSK Android arm64 release and place libvosk.so there."
    exit 1
fi

echo "=== Building libvosk-bridge for Android arm64-v8a ==="
echo "NDK:    ${ANDROID_NDK_HOME}"
echo "Source: ${SCRIPT_DIR}"
echo "Build:  ${BUILD_DIR}"
echo "Output: ${OUTPUT_DIR}"
echo ""

cmake -B "${BUILD_DIR}" -S "${SCRIPT_DIR}" \
    -DCMAKE_TOOLCHAIN_FILE="${TOOLCHAIN}" \
    -DANDROID_ABI=arm64-v8a \
    -DANDROID_PLATFORM=android-29 \
    -DANDROID_STL=c++_shared \
    -DCMAKE_BUILD_TYPE=Release

cmake --build "${BUILD_DIR}" --config Release -j "$(nproc)"

# Copy output
cp "${BUILD_DIR}/libvosk-bridge.so" "${OUTPUT_DIR}/libvosk-bridge.so"

echo ""
echo "=== Build complete ==="
ls -lh "${OUTPUT_DIR}/libvosk-bridge.so"
ls -lh "${OUTPUT_DIR}/libvosk.so"
