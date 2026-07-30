# Native Bridge

The SDK includes a prebuilt `libvosk-bridge.so` for Android arm64. This guide covers building the native bridge from source, which is only necessary if you need to modify the C++ layer.

The bridge has two operating modes: **capture mode** (`vosk_bridge_start`), where a platform capture backend feeds the microphone into the recognition pipeline, and **push mode** (`vosk_bridge_start_push`), where the caller supplies pre-DSP 48 kHz mono float audio via `vosk_bridge_push_audio` — used by the automated-verification tiers to replay recorded fixtures through the real pipeline (ring buffer → 48→16 kHz downsampler → AGC → int16 → VOSK) without a microphone. The two modes are mutually exclusive per session. A read-only input-level getter (`vosk_bridge_get_input_level`, ~300 ms rolling RMS of pre-DSP audio) supports mic-liveness checks. None of this is game-facing API: the C# layer binds the symbols (`BridgeNative`) but nothing in the runtime calls them.

---

## Prerequisites

- **Android NDK** -- bundled with Unity 6 or standalone r26+
- **CMake 3.18+** -- bundled with Unity's Android toolchain or standalone
- **Ninja build system** -- bundled with Unity's Android toolchain or standalone
- **`libvosk.so` for Android arm64** -- download from [VOSK releases](https://github.com/alphacep/vosk-api/releases)

---

## Build Steps

1. Place `libvosk.so` in `Runtime/Plugins/Android/arm64-v8a/` (the location the package's CMake build expects by default — `NativeBridge~/CMakeLists.txt` sets `VOSK_LIB_DIR` here when nothing else is provided).

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

## Capture Backend Selection

The capture backend is selected at configure time by the CMake option `VOSK_BRIDGE_CAPTURE` (`audiorecord` | `aaudio` | `stub`, default `audiorecord`). It picks both the compiled `audio_capture_<backend>.cpp` and the header the backend-neutral `audio_capture.h` forwards to — `vosk_bridge.cpp` includes only the neutral header. The default Android build is source-identical to the pre-option build. The `stub` backend (no capture hardware; a "silent microphone") exists for desktop builds where audio enters via push mode only; `aaudio` is accepted by the option but is unmaintained legacy (see below) and is not verified to compile.

---

## Desktop Build & WSL Harness

`NativeBridge~/CMakePresets.json` defines a `desktop-linux` preset (Linux x86_64, stub backend, Release) that builds the bridge against a locally provisioned Linux `libvosk.so` — no NDK, no JNI — plus a CLI harness (`NativeBridge~/harness/`) that replays the committed fixture corpus (`Tests~/Fixtures/audio/`) through the real bridge in push mode and compares final transcripts against a committed baseline (`harness/expectations.json`, which pins the grammar, AGC gain, and libvosk version it was decoded under). This makes bridge-logic changes verifiable from WSL in seconds; it deliberately cannot validate the arm64 binary, JNI capture, or Quest audio routing (the on-device tier owns those). Provisioning, build, run, and re-baselining are documented in `NativeBridge~/harness/README.md`. Desktop-only: the Windows editor keeps its own managed pipeline and never loads `libvosk-bridge`.

---

## Source Files

The native bridge source is in `NativeBridge~/` (excluded from Unity import by the `~` suffix):

| File | Description |
|------|-------------|
| `vosk_bridge.cpp` | Main bridge: capture/push session lifecycle, adaptive AGC with soft saturation, FIR downsampler (48 kHz -> 16 kHz), float-to-int16 conversion, VOSK recognition thread, and the input-level RMS. Feeds int16 samples to VOSK on a dedicated native thread. |
| `audio_capture.h` | Backend-neutral include — forwards to the backend selected by `VOSK_BRIDGE_CAPTURE`. |
| `audio_capture_audiorecord.cpp` | Java `AudioRecord` JNI backend. Active capture implementation on Android. Routes to the headset microphone on Quest devices via the `VOICE_RECOGNITION` audio source. |
| `audio_capture_stub.cpp` | No-op backend for desktop builds: `Start` succeeds without producing samples, so capture-mode calls are safe on platforms without a capture path. |
| `audio_capture_aaudio.cpp` | AAudio backend. Retained for reference but **not compiled** in the current build. |
| `harness/` | Desktop WAV→transcript regression harness (see above). Never ships — `NativeBridge~/` is stripped from the published package. |

---

## AAudio on Quest 3

The AAudio backend (`audio_capture_aaudio.cpp`) is retained in the source tree but is not used. AAudio input delivers silence on Quest 3 -- the `AAudioStream` opens and starts without error and callbacks fire on schedule, but the audio buffer contains near-zero samples regardless of input preset (`GENERIC`, `VOICE_RECOGNITION`, `UNPROCESSED`).

The shipped build uses Java `AudioRecord` via JNI instead, which routes correctly to the headset microphone. If porting to another Android device, test AAudio first -- it may work outside Quest 3. Do not remove the AAudio files; they serve as a reference and fallback seed for future device ports.

---

## See Also

- [Getting Started](getting-started.md) -- Installation and model setup
- [Troubleshooting](troubleshooting.md) -- Common issues on Quest and in the Editor
- [Known Limitations](../KNOWN_LIMITATIONS.md) -- Hardware audio constraints including AAudio silence and the `vosk_recognizer_accept_waveform_f` bug
