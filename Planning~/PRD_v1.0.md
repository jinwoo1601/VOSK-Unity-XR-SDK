# PRD: VOSK XR Unity SDK

**Package:** `com.jinwoo1601.vosk-xr`
**Version:** 0.1.0 (pre-release)
**Author:** jinwoo1601
**Date:** 2026-03-30
**Status:** Draft

---

## 1. Overview

VOSK XR is a Unity SDK that provides offline speech recognition for XR applications. It wraps the open-source VOSK speech recognition toolkit (Apache 2.0) behind a clean, Unity-native C# API and distributes as a UPM package installable via Git URL.

The SDK's core job is simple: capture microphone audio on-device, feed it to VOSK's native recognition engine, and return transcribed text to the Unity application — all without an internet connection.

**Why this exists:** Current options for offline speech recognition in Unity XR are either commercial and closed-source (Recognissimo), abandoned community projects with limited platform support, or the raw VOSK C API which requires significant integration work. This SDK fills the gap: a maintained, open-source, XR-focused wrapper that handles the platform-specific complexity (native library loading, model extraction, audio capture, threading) so consumers can add voice input with a few lines of C#.

**Why VOSK:** Offline operation (critical for XR where connectivity is unreliable), small model footprint (~50 MB for portable models), permissive licensing, and a stable C API suitable for native integration. VOSK is the same engine behind Recognissimo's commercial product, proving it is viable for production Unity use.

**Architecture approach:** A thin C++ bridge library (`libvosk-bridge`) sits between C# and VOSK. The bridge handles audio capture via the platform's native audio API (AAudio on Android), runs the recognition loop natively, and exposes a simplified C API to Unity via P/Invoke. This eliminates any dependency on Meta's SDK or Unity's `Microphone` class — the same bridge binary works on any Android arm64 XR device.

---

## 2. Goals & Non-Goals

### Goals (v1.0)

- **G1:** Provide offline speech-to-text on Meta Quest (Android arm64) with no internet dependency.
- **G2:** Expose a minimal, event-driven C# API: start listening, receive partial/final transcription results, stop listening.
- **G3:** Handle all platform complexity internally via a thin C++ bridge library (`libvosk-bridge`): native audio capture, recognition loop, model loading — all on the native side, with only results crossing back to C#.
- **G4:** Capture audio using the platform's native audio API (AAudio on Android) inside the bridge, avoiding any dependency on Meta's SDK or Unity's `Microphone` class.
- **G5:** Build the bridge with NDK/CMake targeting Android arm64, shipped as a prebuilt `.so` alongside `libvosk.so` in the UPM package.
- **G6:** Distribute as a UPM package installable from a GitHub Git URL — no Asset Store, no manual file copying.
- **G7:** Document and support a clear model setup workflow: user downloads a VOSK model separately, places it in StreamingAssets, and the SDK handles runtime extraction.
- **G8:** Provide a two-tier native lifecycle (heavyweight init/destroy vs lightweight start/stop) so that push-to-talk and repeated start/stop cycles do not re-initialise the model.
- **G9:** Define structured error codes for all anticipated failure modes (model load failure, audio device unavailable, permission denied, ring buffer overflow) and surface them to C# consumers via both return values and the `OnError` event.

### Non-Goals (v1.0)

- **NG1:** Editor-time recognition tooling (record-and-test in Play Mode is sufficient).
- **NG2:** Speaker identification (`VoskSpkModel` support).
- **NG3:** Grammar/command-constrained recognition (planned for v1.1).
- **NG4:** Multiple simultaneous recognisers or multi-language switching.
- **NG5:** Model downloading from CDN or any runtime network-based model management.
- **NG6:** Platforms beyond Meta Quest (Android arm64). The architecture must not prevent future expansion, but v1.0 only ships and tests against Quest.
- **NG7:** Word-level timestamps, confidence scores, or n-best alternatives in the public API (VOSK supports these, but they are deferred to a later release).

---

## 3. Target Platform

### v1.0: Meta Quest (Android arm64)

Meta Quest headsets run Android on arm64-v8a. The SDK ships two native libraries for this architecture: the prebuilt `libvosk.so` (from vosk-api GitHub releases) and a custom `libvosk-bridge.so` (built via NDK/CMake), both placed under `Plugins/Android/libs/arm64-v8a/`.

Key platform constraints:
- **No direct file access in APK:** StreamingAssets on Android lives inside the APK (a ZIP archive). VOSK's `vosk_model_new()` expects a filesystem path, so models must be extracted to `Application.persistentDataPath` at first launch before they can be loaded.
- **Microphone permissions:** The app must request `android.permission.RECORD_AUDIO` at runtime. The bridge should verify permission status before attempting audio capture and surface a clear error code if denied.
- **Audio capture via AAudio:** The C++ bridge uses Android's AAudio API for microphone capture. AAudio is the recommended low-latency audio API on Android 8.1+ (API level 27+), which covers all Quest hardware. This avoids any dependency on Unity's `Microphone` class or Meta's platform SDK — the same bridge binary works on any Android arm64 XR device.

### Future platform strategy

The architecture is designed for multi-platform expansion without breaking changes:
- The C++ bridge abstracts both audio capture and recognition behind a simple C API. Platform-specific code (AAudio vs CoreAudio vs WASAPI) lives inside the bridge, compiled per platform.
- The C# layer is entirely platform-agnostic — it only P/Invokes into the bridge's C API.
- VOSK provides prebuilt native libraries for Windows x64, Linux x64/aarch64, macOS, iOS, and additional Android architectures. Adding a platform means: building the bridge for that platform's audio API, adding the correct native binaries under `Plugins/`, and testing.
- No Meta/OVR SDK dependency means Android-based XR devices (Pico, Lynx, etc.) are supported by the same arm64 bridge binary with zero additional work.

Candidate future platforms: other Android arm64 XR devices (Pico, Lynx — supported by the same binary), PC VR (Windows x64, bridge would use WASAPI), and Apple Vision Pro (visionOS, would require both a VOSK source build and a CoreAudio bridge).

---

## 4. Architecture

The SDK has three layers. The key insight is that the entire audio-capture-and-recognition loop runs natively — C# only sends commands and receives results.

```
┌─────────────────────────────────────────────────────────────┐
│  Consumer Layer (user's C# code)                            │
│  - VoskSpeechRecogniser (MonoBehaviour)                     │
│  - Subscribes to events: OnPartialResult, OnFinalResult    │
├─────────────────────────────────────────────────────────────┤
│  C# Interop Layer                                           │
│  - BridgeNative (static class): P/Invoke to libvosk-bridge │
│  - Returns IntPtr, marshals via Marshal.PtrToStringUTF8     │
│  - VoskSpeechRecogniser.Update() polls for results          │
│  - [Preserve] attributes to prevent IL2CPP code stripping   │
├─────────────────────────────────────────────────────────────┤
│  C++ Bridge (libvosk-bridge.so)                             │
│  - Owns the recognition lifecycle                           │
│  - Audio capture via AAudio (Android)                       │
│  - Runs recognition loop on a native thread:                │
│      read mic → feed vosk → buffer results                  │
│  - Links against libvosk at load time                       │
│  - Exposes a flat C API to C# (see below)                  │
├─────────────────────────────────────────────────────────────┤
│  libvosk.so (prebuilt, untouched)                           │
│  - VOSK speech recognition engine                           │
│  - Kaldi + OpenBLAS + OpenFST                               │
└─────────────────────────────────────────────────────────────┘
```

### Bridge C API (libvosk-bridge)

The bridge exposes a minimal flat C API — this is the only interface C# calls via P/Invoke.

#### Error codes

All bridge functions that can fail return an integer error code from the `VoskBridgeError` enum. A return value of `0` always means success. C# checks the return value of every call and, on non-zero values, maps the code to a human-readable description and fires `OnError`.

```c
enum VoskBridgeError {
    VOSK_BRIDGE_OK                        = 0,
    VOSK_BRIDGE_ERR_MODEL_LOAD_FAILED     = 1,  // vosk_model_new() returned NULL
    VOSK_BRIDGE_ERR_AUDIO_DEVICE_UNAVAIL  = 2,  // AAudio stream could not be opened
    VOSK_BRIDGE_ERR_PERMISSION_DENIED     = 3,  // RECORD_AUDIO not granted
    VOSK_BRIDGE_ERR_RING_BUFFER_OVERFLOW  = 4,  // AAudio produced faster than recogniser consumed
    VOSK_BRIDGE_ERR_ALREADY_RUNNING       = 5,  // start() called while already running
    VOSK_BRIDGE_ERR_NOT_INITIALISED       = 6,  // start/stop/reset called before init
    VOSK_BRIDGE_ERR_ALREADY_INITIALISED   = 7,  // init() called without prior destroy()
};
```

#### Two-tier lifecycle

The bridge lifecycle is split into heavyweight (model load / teardown) and lightweight (audio stream start / stop) operations. This separation ensures that repeated start/stop cycles — the common push-to-talk pattern — do not re-load the model or re-create the VOSK recogniser.

```c
// === Heavyweight lifecycle (model load / teardown) ===
int  vosk_bridge_init(const char* model_path, float sample_rate);
    // Loads the VOSK model, creates the recogniser, allocates the ring buffer
    // and result queue. Does NOT open the audio stream or start recognition.
    // Returns: VOSK_BRIDGE_OK, ERR_MODEL_LOAD_FAILED, ERR_ALREADY_INITIALISED.

void vosk_bridge_destroy();
    // Stops recognition if running, then frees all native resources: model,
    // recogniser, ring buffer, result queue, AAudio stream. After this call,
    // init() must be called again before any other operation.

// === Lightweight lifecycle (audio stream start / stop) ===
int  vosk_bridge_start();
    // Opens the AAudio stream, launches the recognition thread, begins
    // capturing and transcribing audio.
    // Returns: VOSK_BRIDGE_OK, ERR_NOT_INITIALISED, ERR_ALREADY_RUNNING,
    //          ERR_AUDIO_DEVICE_UNAVAIL, ERR_PERMISSION_DENIED.

void vosk_bridge_stop();
    // Stops the AAudio stream, signals the recognition thread to drain
    // remaining audio, flushes the final result, and joins the thread.
    // The model and recogniser remain loaded — start() can be called
    // again immediately without re-initialisation.

int  vosk_bridge_reset();
    // Resets the VOSK recogniser state (clears accumulated audio and
    // partial hypotheses) without reloading the model. Use between
    // utterances if the consumer wants a clean slate without a full
    // stop/start cycle. Can be called while running or while stopped.
    // Returns: VOSK_BRIDGE_OK, ERR_NOT_INITIALISED.

// === Results (polled from C# Update loop) ===
int         vosk_bridge_has_result();           // Returns 1 if a result is queued
const char* vosk_bridge_get_result();           // Returns JSON string (see lifetime note below)
int         vosk_bridge_get_result_is_final();  // 1 = final, 0 = partial

// === Status ===
int  vosk_bridge_is_running();
int  vosk_bridge_is_initialised();              // 1 if init succeeded and destroy has not been called
int  vosk_bridge_get_error(char* buf, int buf_size);  // Copy last error string into buffer
```

**Result pointer lifetime:** `vosk_bridge_get_result()` returns a `const char*` that points to a bridge-owned buffer. This buffer remains valid until the next call to `vosk_bridge_get_result()`. The C# interop layer must marshal the pointer to a managed string via `Marshal.PtrToStringUTF8` immediately and must not cache the `IntPtr`.

#### Ring buffer overflow behaviour

If the AAudio callback produces samples faster than the recognition thread consumes them and the ring buffer fills, the bridge drops the oldest unread samples to make room for incoming audio. It does not block the AAudio callback — blocking the high-priority audio thread would stall the OS audio pipeline. When overflow occurs, the bridge sets an internal flag. C# can detect this via the `VOSK_BRIDGE_ERR_RING_BUFFER_OVERFLOW` error code, which is surfaced through `OnError`. The dropped audio may cause a brief gap in recognition but will not crash or corrupt state. The overflow flag resets on the next successful `start()`.

### Key design decisions

**Entire recognition loop in native code:** The bridge's internal thread runs: AAudio callback fills a ring buffer → recognition thread reads from the ring buffer → feeds `vosk_recognizer_accept_waveform_f()` → checks return value → buffers result JSON. C# never touches audio data or VOSK pointers. This eliminates all managed↔native crossing overhead for the hot path (audio processing), and means VOSK's memory is managed entirely in C++ (no SafeHandle/P/Invoke pointer juggling).

**AAudio callback model:** AAudio provides audio data via a callback on a high-priority thread. The bridge copies samples from the AAudio callback into a lock-free ring buffer, which the recognition thread consumes. This is the standard low-latency pattern for Android audio and avoids the polling-based `ReadSamples()` approach that would be needed if audio capture were in C#.

**Result buffering in bridge:** The bridge maintains an internal result queue (partial and final results). C# polls via `vosk_bridge_has_result()` / `vosk_bridge_get_result()` each frame. The returned `const char*` points to a bridge-owned buffer that remains valid until the next `vosk_bridge_get_result()` call — C# must copy the string before calling again.

**Single `.so` for portability:** The bridge compiles to one `libvosk-bridge.so` per platform. On Android arm64, it uses AAudio. Future platforms swap the audio backend at compile time (e.g., WASAPI for Windows, CoreAudio for Apple) behind the same C API. C# code doesn't change at all.

**Standard dynamic linking to libvosk:** `libvosk-bridge.so` links against `libvosk.so` via standard dynamic linking (`-lvosk` at build time). Both `.so` files always ship together in the package, so `dlopen`'s "graceful missing library" benefit doesn't apply. Standard linking is simpler, catches symbol mismatches at compile time, and works reliably on all Quest-era Android versions (API 23+).

**IL2CPP-safe P/Invoke:** Quest builds use IL2CPP. The C# interop layer follows three rules to ensure compatibility: (1) all P/Invoke functions returning strings use `IntPtr` as the return type — never `string` — and manually call `Marshal.PtrToStringUTF8`, which prevents IL2CPP from trying to free the bridge-owned buffer; (2) all `IntPtr` returns are guarded against `IntPtr.Zero` before marshalling; (3) all P/Invoke methods and the `BridgeNative` class are annotated with `[Preserve]` to prevent IL2CPP code stripping. The polling architecture (C# calls into native, never the reverse) is a natural fit for IL2CPP since it avoids the `[MonoPInvokeCallback]` requirement that native-to-managed callbacks would introduce.

**No Meta/OVR SDK dependency:** By using AAudio directly, the bridge runs on any Android device with API level 27+ — Quest, Pico, Lynx, or a phone. No vendor-specific SDK is needed.

---

## 5. SDK Public API Surface

The public API is intentionally small. A consumer should be able to go from zero to transcribed text in under 10 lines of code.

### Core types

#### `VoskBridgeErrorCode` (enum)

A C# mirror of the native `VoskBridgeError` enum, used for programmatic error handling.

```csharp
public enum VoskBridgeErrorCode
{
    Ok                      = 0,
    ModelLoadFailed         = 1,
    AudioDeviceUnavailable  = 2,
    PermissionDenied        = 3,
    RingBufferOverflow      = 4,
    AlreadyRunning          = 5,
    NotInitialised          = 6,
    AlreadyInitialised      = 7,
}
```

#### `VoskSpeechRecogniser` (MonoBehaviour)

The main entry point. Attach to a GameObject, configure via the Inspector or code, and subscribe to events.

```csharp
// Inspector-configurable fields
string modelRelativePath;     // Path within StreamingAssets (e.g., "vosk-model-small-en-us")
float sampleRate = 16000f;

// Lifecycle: heavyweight (model load / teardown)
void Initialise();            // Extracts model (if needed), calls bridge init. No-ops if already initialised.
void ReleaseNativeResources();// Calls bridge destroy. Safe to call multiple times.

// Lifecycle: lightweight (audio stream start / stop)
void StartRecognition();      // Calls Initialise() if needed, then bridge start.
void StopRecognition();       // Stops listening, flushes final result. Model stays loaded.
void ResetRecogniser();       // Clears recogniser state without stopping audio.

// State
bool IsInitialised { get; }   // True after Initialise() succeeds, false after ReleaseNativeResources().
bool IsRecognising { get; }   // True between StartRecognition() and StopRecognition().
bool IsModelReady { get; }    // True once model extraction completes (subset of IsInitialised).

// Events
event Action<string> OnPartialResult;                // Fired on main thread with partial transcript text
event Action<string> OnFinalResult;                  // Fired on main thread with final transcript text
event Action<VoskBridgeErrorCode, string> OnError;   // Fired on main thread with error code + description
event Action OnModelReady;                           // Fired when model extraction completes
```

**Lifecycle flow:**

The two-tier lifecycle supports three consumer patterns:

1. **Simple (fire-and-forget):** Call `StartRecognition()` — it internally calls `Initialise()` on first use. Call `StopRecognition()` when done. Model stays loaded for the next `StartRecognition()`. Cleanup happens automatically in `OnDestroy()`.

2. **Push-to-talk:** Call `StartRecognition()` / `StopRecognition()` on button press / release. Because `stop` does not tear down the model, the next `start` resumes in milliseconds rather than seconds.

3. **Explicit control:** Call `Initialise()` at scene load to pre-warm the model. Call `StartRecognition()` / `StopRecognition()` during gameplay. Call `ReleaseNativeResources()` when voice input is no longer needed (e.g., on scene unload) to reclaim memory.

`OnDestroy()` always calls `ReleaseNativeResources()` to guarantee no leaked native resources or zombie threads, even if the consumer does not call it explicitly.

The public API is deliberately thin — audio capture, threading, and VOSK interaction are all handled inside the native bridge. The C# layer's only jobs are: model extraction (which requires Unity APIs for StreamingAssets access on Android), lifecycle management of the bridge, and polling for results in `Update()` to fire events on the main thread.

### Consumer usage example

```csharp
public class VoiceDemo : MonoBehaviour
{
    [SerializeField] private VoskSpeechRecogniser recogniser;
    [SerializeField] private TextMeshProUGUI displayText;

    private void OnEnable()
    {
        recogniser.OnPartialResult += text => displayText.text = text;
        recogniser.OnFinalResult += text => Debug.Log($"Final: {text}");
        recogniser.OnError += (code, msg) => Debug.LogError($"VOSK [{code}]: {msg}");
        recogniser.StartRecognition();
    }

    private void OnDisable()
    {
        recogniser.StopRecognition();
        // Model stays loaded — next OnEnable resumes instantly.
    }

    private void OnDestroy()
    {
        // Optional: explicitly release native resources.
        // If omitted, the recogniser's own OnDestroy handles it.
        recogniser.ReleaseNativeResources();
    }
}
```

### Push-to-talk example

```csharp
public class PushToTalk : MonoBehaviour
{
    [SerializeField] private VoskSpeechRecogniser recogniser;

    private void Start()
    {
        // Pre-warm: load the model at scene start so recognition
        // starts instantly when the user first presses the button.
        recogniser.Initialise();
    }

    public void OnTalkButtonPressed()
    {
        recogniser.StartRecognition();
    }

    public void OnTalkButtonReleased()
    {
        recogniser.StopRecognition();
    }
}
```

### What is NOT in the v1.0 public API

The following VOSK capabilities exist in `libvosk` but are not exposed through the bridge or C# API in v1.0: grammar/phrase list constraints, max alternatives, word-level timestamps and confidence, speaker identification, NLSML output, endpointer configuration, and batch recognition. The bridge's C API is designed to be extended with these features in future versions without breaking existing consumers.

---

## 6. Audio Pipeline

### Capture → Ring Buffer → Recognise → Result Queue

The entire audio pipeline runs inside the C++ bridge. C# has no involvement in audio data handling.

**AAudio callback thread (high priority, OS-managed):**
1. AAudio delivers audio samples via a callback at **48 kHz** (Quest's native rate) in float32 mono. The stream requests 48 kHz directly — do not request 16 kHz from AAudio, as it may reject the rate or resample poorly.
2. The callback writes raw 48 kHz samples into a lock-free ring buffer. The callback must be non-blocking — no allocations, no locks, no VOSK calls.
3. **On ring buffer overflow:** If the ring buffer is full, the callback overwrites the oldest unread samples and sets an atomic overflow flag. It never blocks. See *Ring buffer overflow behaviour* in Section 4 for details.

**Recognition thread (bridge-managed):**
1. A dedicated native thread runs a loop (controlled by an atomic flag):
   - Read available 48 kHz samples from the ring buffer into a local processing buffer.
   - **Downsample 48 kHz → 16 kHz** before feeding VOSK. The 3:1 integer ratio makes this straightforward with a simple FIR low-pass filter (anti-alias at ~7.5 kHz, then decimate by 3). This runs on the recognition thread, not the AAudio callback.
   - Call `vosk_recognizer_accept_waveform_f(recognizer, downsampled_buffer, sample_count)`.
   - If return value is `1` (utterance boundary): call `vosk_recognizer_result()`, copy the JSON string into the result queue as a final result.
   - If return value is `0` (decoding continues): call `vosk_recognizer_partial_result()`, copy into the result queue as a partial result.
   - If no samples are available, sleep briefly (10–20 ms) to avoid busy-spinning.
   - **Check overflow flag:** If the overflow flag is set, enqueue an overflow error into the result queue so that C# can surface it via `OnError`. Clear the flag.
2. On stop: call `vosk_recognizer_final_result()` to flush remaining audio, enqueue the last result, then exit the thread. The recognition thread exits but the model and recogniser remain allocated — they are only freed on `vosk_bridge_destroy()`.

**C# main thread (Unity Update):**
1. Each frame, `VoskSpeechRecogniser.Update()` calls `vosk_bridge_has_result()`.
2. If a result is available, calls `vosk_bridge_get_result()` and `vosk_bridge_get_result_is_final()`.
3. Marshals the `const char*` to a C# string via `Marshal.PtrToStringUTF8`.
4. Fires `OnPartialResult` or `OnFinalResult` accordingly.

### Audio format

AAudio captures at **48 kHz, float32, mono** — the native rate on Quest hardware. The bridge's recognition thread downsamples to **16 kHz** (VOSK's expected rate) using a simple FIR low-pass filter before each `accept_waveform_f` call. The 3:1 integer ratio (48000 ÷ 16000 = 3) means every third sample is kept after filtering — no fractional resampling needed. The VOSK recogniser is initialised with a sample rate of 16000.

### Partial result frequency

VOSK produces a partial result on every `accept_waveform` call that returns `0`. The recognition thread processes audio in chunks (e.g., 4096 samples ≈ 256 ms at 16 kHz), yielding roughly 4 partial results per second — a reasonable update rate for live transcription display. If this proves too frequent, throttling can be added inside the bridge without changing the C API.

### Ring buffer sizing

The ring buffer holds raw 48 kHz audio and should contain at least 1–2 seconds (48,000–96,000 float samples ≈ 192–384 KB) to absorb jitter between the AAudio callback frequency and the recognition thread's consumption rate. A power-of-two size (e.g., 65,536 samples ≈ 1.37 seconds) simplifies lock-free index arithmetic.

---

## 7. Model Management

### Distribution strategy (v1.0)

Models are **not bundled** inside the UPM package. Shipping 50 MB+ binary files in a Git repo is impractical for consumers — it bloats clone times and repository size. Instead, models are distributed as a separate download.

The recommended workflow for consumers:
1. Download the desired model archive (e.g., `vosk-model-small-en-us-0.15.zip`, ~50 MB) from the VOSK models page or a mirrored release.
2. Place the `.zip` archive in the Unity project's `StreamingAssets/VoskModels/` directory.
3. The SDK handles extraction to `Application.persistentDataPath` at runtime.

The package README will include direct download links and step-by-step instructions. A future version may automate this via an Editor tool that downloads models from within Unity.

### Runtime extraction

On Android, StreamingAssets content is packed inside the APK (a ZIP archive) and is not accessible via filesystem paths. VOSK's `vosk_model_new()` requires a real filesystem path, so the SDK must extract models to writable storage at runtime.

The extraction process uses an **atomic rename pattern** to prevent corrupted model caches from persisting across launches:

1. **Check cache:** On `Initialise()`, `ModelExtractor` checks whether the model directory already exists at `{Application.persistentDataPath}/VoskModels/{modelName}/`.

2. **Validate if present:** If the directory exists, `ModelExtractor` performs a structural validation: it checks for the presence of expected top-level model files (`am/final.mdl`, `conf/mfcc.conf`, `graph/` directory, etc.). If validation passes, return the path immediately. If validation fails, delete the corrupt directory and proceed to extraction.

3. **Extract to a temporary directory:** If no valid cache exists, read the model archive from StreamingAssets (on Android, this requires `UnityWebRequest.Get()` with the `jar:file://` URI scheme) and decompress it to a temporary directory at `{Application.persistentDataPath}/VoskModels/.tmp_{modelName}/` using `System.IO.Compression.ZipFile` (part of .NET Standard 2.1, available in Unity 6).

4. **Clean up stale temp directories:** Before extracting, delete any existing `.tmp_{modelName}/` directory — its presence means a previous extraction was interrupted.

5. **Validate extraction:** After decompression completes, perform the same structural validation on the temp directory. If validation fails, delete the temp directory and fire `OnError` with `ModelLoadFailed`. This catches truncated archives and corrupt downloads.

6. **Atomic rename:** Rename `.tmp_{modelName}/` to `{modelName}/` via `Directory.Move()`. On Android's ext4/F2FS filesystem, a directory rename within the same mount point is atomic — either the rename completes fully or it does not happen. This eliminates the window where a crash could leave a partially-extracted model directory in the final location.

7. **Return path:** Return the filesystem path to the validated model directory.

**Failure mode guarantees:**
- If the app is killed during extraction (steps 3–5): The `.tmp_` directory is left behind. On next launch, step 4 deletes it and extraction restarts cleanly.
- If the app is killed during rename (step 6): The atomic rename either completed or didn't. If it didn't, the `.tmp_` directory still exists and step 4 handles it. If it did, the model is valid.
- If the archive is corrupt or truncated: Step 5 catches this and fires `OnError` without leaving invalid state.

### Recommended model for testing

`vosk-model-small-en-us-0.15` (~50 MB compressed) is the recommended starting model. The package documentation and sample scene will reference this model.

**Expected model structure** (used for structural validation):

```
vosk-model-small-en-us-0.15/
├── am/
│   └── final.mdl
├── conf/
│   ├── mfcc.conf
│   └── model.conf
├── graph/
│   ├── Gr.fst
│   ├── HCLr.fst
│   └── disambig_tid.int
├── ivector/
│   └── ...
└── README
```

The `ModelExtractor` validates the presence of `am/final.mdl`, `conf/mfcc.conf`, and the `graph/` directory. These three checks catch the vast majority of extraction failures without being brittle to model version differences.

### Future considerations (not v1.0)

- Editor tooling to download and manage models from within Unity (browse available models, download, place in StreamingAssets automatically).
- Model download from CDN at runtime for additional languages.
- Model versioning and cache invalidation when the user updates their model.

---

## 8. Package Structure

The SDK ships as a UPM package-at-root repository, installable via Git URL.

### Repository layout

```
vosk-xr/                                    ← repo root = package root
├── package.json
├── README.md
├── LICENSE.md                              ← Apache 2.0
├── CHANGELOG.md
├── Runtime/
│   ├── Jinwoo1601.VoskXR.Runtime.asmdef
│   ├── AssemblyInfo.cs                     ← InternalsVisibleTo for tests
│   ├── VoskSpeechRecogniser.cs             ← MonoBehaviour entry point
│   ├── VoskBridgeErrorCode.cs              ← Error code enum (C# mirror)
│   ├── ModelExtractor.cs                   ← StreamingAssets → persistentDataPath (atomic)
│   ├── RecognitionResult.cs                ← Result data struct
│   └── Native/
│       └── BridgeNative.cs                 ← P/Invoke DllImport to libvosk-bridge
├── NativeBridge~/                          ← tilde = ignored by Unity import
│   ├── CMakeLists.txt                      ← NDK/CMake build configuration
│   ├── src/
│   │   ├── vosk_bridge.h                   ← Bridge C API header + VoskBridgeError enum
│   │   ├── vosk_bridge.cpp                 ← Core bridge implementation
│   │   ├── audio_capture_aaudio.cpp        ← AAudio mic capture (Android)
│   │   ├── downsampler.h                   ← FIR low-pass filter + 3:1 decimation
│   │   ├── ring_buffer.h                   ← Lock-free ring buffer (overflow-safe)
│   │   └── result_queue.h                  ← Thread-safe result queue
│   ├── include/
│   │   └── vosk_api.h                      ← VOSK C API header (for linking)
│   └── build.sh                            ← Convenience script for NDK build
├── Plugins/
│   └── Android/
│       └── libs/
│           └── arm64-v8a/
│               ├── libvosk.so              ← Prebuilt VOSK native library
│               └── libvosk-bridge.so       ← Prebuilt bridge (built from NativeBridge~/)
├── Tests/
│   ├── Runtime/
│   │   ├── Jinwoo1601.VoskXR.Tests.Runtime.asmdef
│   │   └── *.cs
│   └── Editor/
│       ├── Jinwoo1601.VoskXR.Tests.Editor.asmdef
│       └── *.cs
├── Samples~/
│   └── BasicTranscription/
│       ├── VoiceDemo.cs
│       ├── BasicTranscription.unity
│       └── README.md
├── Documentation~/
│   └── vosk-xr.md
└── .gitignore
```

Key structural notes:
- **`NativeBridge~/`** — the tilde suffix ensures Unity ignores this directory during import. It contains the C++ source and CMake configuration for the bridge. Consumers don't need to build it — they use the prebuilt `.so` in `Plugins/`. Contributors or developers targeting new platforms will build from source here.
- **Two `.so` files in `Plugins/`** — `libvosk.so` (upstream, untouched) and `libvosk-bridge.so` (custom, links against libvosk). Both must be present and configured for Android arm64.
- **No OVR/Meta SDK references anywhere** — the package has zero vendor SDK dependencies.

### Installation (for consumers)

**Via Git URL:**
1. Open Unity Package Manager (Window → Package Manager).
2. Click + → "Add package from git URL...".
3. Enter: `https://github.com/jinwoo1601/vosk-xr.git`

**Via manifest.json:**
```json
{
  "dependencies": {
    "com.jinwoo1601.vosk-xr": "https://github.com/jinwoo1601/vosk-xr.git"
  }
}
```

**Pinned version (recommended):**
```
https://github.com/jinwoo1601/vosk-xr.git#v0.1.0
```

### Assembly definitions

| Assembly | Platforms | References |
|---|---|---|
| `Jinwoo1601.VoskXR.Runtime` | Any | — |
| `Jinwoo1601.VoskXR.Tests.Runtime` | Any (test only) | `Jinwoo1601.VoskXR.Runtime`, `UnityEngine.TestRunner`, `UnityEditor.TestRunner` |
| `Jinwoo1601.VoskXR.Tests.Editor` | Editor only | `Jinwoo1601.VoskXR.Runtime`, `UnityEngine.TestRunner`, `UnityEditor.TestRunner` |

### Native plugin import settings

Both `libvosk.so` and `libvosk-bridge.so` under `Plugins/Android/libs/arm64-v8a/` must have their Unity import settings configured:
- Platform: Android only
- CPU: ARM64
- All other platforms: unchecked

---

## 9. Known Risks & Open Questions

### Risks

| Risk | Severity | Mitigation |
|---|---|---|
| **NDK/CMake build pipeline maintenance** — the bridge must be compiled per platform using the Android NDK. Build toolchain updates or NDK version mismatches can break the build. | Medium | Pin to a specific NDK version (e.g., r26). Include a `build.sh` script and document the exact build steps. Provide prebuilt `.so` in the repo so consumers never need to build from source. |
| **AAudio availability and behaviour across devices** — while AAudio is supported on Android 8.1+ (API 27+), some XR devices may have non-standard audio routing or driver quirks. | Medium | AAudio is the official Android audio API and is well-supported on Quest. Test on physical hardware early. If device-specific issues arise, OpenSL ES is a fallback (older API, more verbose, but wider compatibility). The `ERR_AUDIO_DEVICE_UNAVAIL` error code surfaces device-specific failures clearly to the consumer. |
| **Debugging native crashes** — bugs in the C++ bridge produce Android tombstones rather than C# stack traces, which are harder to diagnose. | Medium | Keep the bridge code minimal and well-tested. Add logging behind a debug flag. Use AddressSanitizer during development builds. |
| **Model extraction time on first launch** — decompressing a ~50 MB model to persistent storage may take several seconds, during which the app cannot perform recognition. | Low | Extract asynchronously on a background thread. Provide `IsModelReady` and `OnModelReady` so the consumer can show a loading indicator. The atomic rename pattern (Section 7) ensures interrupted extractions never leave corrupt state. |
| **Interrupted model extraction** — app crash or kill during extraction could leave corrupt model cache. | Low | **Mitigated by design.** Extraction writes to a `.tmp_` directory and atomically renames on completion. Structural validation on launch detects corruption. See Section 7 for the full atomic extraction workflow. |
| **libvosk.so compatibility across Android OS versions** — VOSK's prebuilt Android arm64 binary is built against a specific NDK version. Future OS updates could theoretically break ABI compatibility. | Low | Use the official release binary and test against current Quest OS. If issues arise, building from source with a newer NDK is possible. |
| **Noise artefacts / phantom words** — VOSK may produce false recognitions from ambient noise, especially in environments with background audio (common in XR). | Medium | Document this as a known VOSK behaviour. In v1.1, expose endpointer tuning and grammar constraints which mitigate this. Consider a simple confidence-based filter if the issue is severe. |
| **APK size increase** — including the ~50 MB model in StreamingAssets (placed by the consumer) plus two native libraries increases the APK size. | Medium | Use ZIP compression for models. Document the size impact. The model is already separate from the package itself, so consumers can choose smaller models if size is a concern. |
| **Push-to-talk lifecycle misuse** — consumers may call `StartRecognition()` / `StopRecognition()` rapidly without understanding the lifecycle. | Low | The two-tier lifecycle (Section 4, 5) makes start/stop lightweight by design. `ERR_ALREADY_RUNNING` prevents double-start. `stop()` on an already-stopped bridge is a safe no-op. |

### Open questions

All architectural open questions have been resolved. See the relevant sections for decisions on: AAudio sample rate and downsampling (Section 6), dynamic linking strategy (Section 4), decompression library (Section 7), model distribution (Section 7), IL2CPP compatibility (Section 4), lifecycle separation (Section 4), and error handling strategy (Section 4).

---

## 10. Success Criteria

v1.0 is complete when all of the following are demonstrably true:

### Acceptance tests

1. **End-to-end recognition:** A user wearing a Meta Quest headset can speak a sentence in English, and the transcribed text appears in a Unity scene within 2 seconds of the utterance completing.
2. **Partial results:** While the user is speaking, partial transcription text updates in real time (at least 2–3 updates per second of continuous speech).
3. **First-launch model extraction:** On first launch after a fresh install, the model extracts from StreamingAssets to persistent storage and recognition starts successfully. Subsequent launches skip extraction.
4. **Interrupted extraction recovery:** If the app is killed during model extraction, the next launch detects the incomplete extraction, cleans up, and re-extracts successfully without user intervention.
5. **Corrupt model detection:** If the cached model directory is manually corrupted (e.g., key files deleted), the SDK detects the corruption on next `Initialise()`, fires `OnError` with `ModelLoadFailed`, and does not crash.
6. **Clean lifecycle:** `StartRecognition()` and `StopRecognition()` can be called multiple times in a session without native memory leaks, crashes, or zombie threads. The VOSK model is loaded once and reused across start/stop cycles.
7. **Push-to-talk pattern:** Rapidly toggling `StartRecognition()` / `StopRecognition()` (simulating push-to-talk) works correctly without reinitialising the model. Latency from stop to next start is under 100 ms.
8. **Error handling — permission denied:** If microphone permission is denied, the SDK fires `OnError` with `VoskBridgeErrorCode.PermissionDenied` and a human-readable message instead of crashing.
9. **Error handling — structured codes:** All error paths surface a `VoskBridgeErrorCode` value alongside a human-readable description. Consumers can use `switch` on the error code for programmatic handling.
10. **Ring buffer overflow resilience:** Under artificially induced load (recognition thread stalled), the bridge drops old audio and surfaces `RingBufferOverflow` via `OnError` rather than crashing or blocking.

### Package distribution

11. **UPM installation works:** A fresh Unity 6 project can install the package via `Add package from git URL` and compile without errors.
12. **Sample scene runs:** The included `BasicTranscription` sample scene can be imported via Package Manager and deployed to Quest with no additional setup beyond providing the VOSK model in StreamingAssets.

### Code quality

13. **No main-thread native calls:** All calls to `libvosk` happen inside the bridge's native recognition thread. C# P/Invoke calls to the bridge (init, start, stop, poll results) are lightweight and non-blocking.
14. **Deterministic cleanup:** All native resources (bridge, VOSK model, VOSK recogniser, AAudio stream) are freed on `ReleaseNativeResources()` or when the `VoskSpeechRecogniser` MonoBehaviour is destroyed. No leaked threads or open audio streams.
15. **Idempotent lifecycle calls:** `StopRecognition()` on an already-stopped recogniser is a no-op. `ReleaseNativeResources()` can be called multiple times safely. `StartRecognition()` while already running returns `AlreadyRunning` via `OnError` without corrupting state.
16. **Zero vendor SDK dependency:** The package compiles and runs without any Meta/OVR SDK installed. Only the Android NDK is required to build the bridge from source.
