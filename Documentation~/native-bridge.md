# Native Bridge

The SDK includes a prebuilt `libvosk-bridge.so` for Android arm64. This guide covers building the native bridge from source, which is only necessary if you need to modify the C++ layer.

---

## Prerequisites

- **Android NDK** -- bundled with Unity 6 or standalone r26+
- **CMake 3.18+** -- bundled with Unity's Android toolchain or standalone
- **Ninja build system** -- bundled with Unity's Android toolchain or standalone
- **`libvosk.so` for Android arm64** -- download from [VOSK releases](https://github.com/alphacep/vosk-api/releases)

---

## Build Steps

1. Place `libvosk.so` in `Plugins/Android/libs/arm64-v8a/`.

2. Configure and build with CMake:

```bash
CMAKE="/path/to/cmake"
NDK_WIN="C:/path/to/NDK"

"$CMAKE" -B NativeBridge~/build \
         -S NativeBridge~ \
         -DCMAKE_TOOLCHAIN_FILE="$NDK_WIN/build/cmake/android.toolchain.cmake" \
         -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-27 -DANDROID_STL=c++_shared \
         -DCMAKE_BUILD_TYPE=Release \
         -DCMAKE_MAKE_PROGRAM="/path/to/ninja" \
         -G Ninja

"$CMAKE" --build NativeBridge~/build --config Release -j 4
```

Replace the paths with your actual NDK, CMake, and Ninja locations. If you are using Unity's bundled Android toolchain, the tools are located under your Unity Editor installation at `Editor/Data/PlaybackEngines/AndroidPlayer/`.

---

## Source Files

The native bridge source is in `NativeBridge~/` (excluded from Unity import by the `~` suffix):

| File | Description |
|------|-------------|
| `vosk_bridge.cpp` | Main bridge: AudioRecord JNI capture, adaptive AGC with soft saturation, FIR downsampler (48 kHz -> 16 kHz), float-to-int16 conversion, and VOSK recognition thread. Feeds int16 samples to VOSK on a dedicated native thread. |
| `audio_capture_audiorecord.cpp` | Java `AudioRecord` JNI backend. Active capture implementation. Routes to the headset microphone on Quest devices via the `VOICE_RECOGNITION` audio source. |
| `audio_capture_aaudio.cpp` | AAudio backend. Retained for reference but **not compiled** in the current build. |

---

## AAudio on Quest 3

The AAudio backend (`audio_capture_aaudio.cpp`) is retained in the source tree but is not used. AAudio input delivers silence on Quest 3 -- the `AAudioStream` opens and starts without error and callbacks fire on schedule, but the audio buffer contains near-zero samples regardless of input preset (`GENERIC`, `VOICE_RECOGNITION`, `UNPROCESSED`).

The shipped build uses Java `AudioRecord` via JNI instead, which routes correctly to the headset microphone. If porting to another Android device, test AAudio first -- it may work outside Quest 3. Do not remove the AAudio files; they serve as a reference and fallback seed for future device ports.

---

## See Also

- [Getting Started](getting-started.md) -- Installation and model setup
- [Troubleshooting](troubleshooting.md) -- Common issues on Quest and in the Editor
- [Known Limitations](../KNOWN_LIMITATIONS.md) -- Hardware audio constraints including AAudio silence and the `vosk_recognizer_accept_waveform_f` bug
