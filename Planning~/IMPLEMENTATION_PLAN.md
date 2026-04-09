# VOSK XR Unity SDK — Implementation Plan

**Date:** 2026-03-30
**Branch:** v1.0
**PRD:** PRD_v1.0.md

---

## Overview

Implement the full VOSK XR UPM package from scratch: C++ native bridge, C# runtime, tests, samples, and documentation. ~31 files across 7 phases, organized bottom-up by dependency.

The repo uses a **package-at-root** layout — the repo root is the UPM package root. No Unity project lives in this repo. Development/testing is done via a separate Unity project referencing this package locally.

---

## Phase Summary

| Phase | What | Files | Depends On |
|-------|------|-------|------------|
| 0 | Package scaffolding | 6 | Nothing |
| 1 | Runtime asmdef + leaf types | 4 | Phase 0 |
| 2 | P/Invoke interop | 1 | Phase 1 |
| 3 | Model extraction | 1 | Phase 2 |
| 4 | Main MonoBehaviour | 1 | Phase 3 |
| 5 | C++ native bridge | 9 | Nothing (parallel with C#) |
| 6 | Tests | 6 | Phase 4 |
| 7 | Samples + docs | 3 | Phase 4 |

---

## Phase 0: Package Scaffolding

No dependencies — all files created in parallel.

| File | Description |
|------|-------------|
| `package.json` | UPM manifest: `com.jinwoo1601.vosk-xr`, v0.1.0, Unity 6 minimum |
| `LICENSE.md` | Apache 2.0, copyright 2026 jinwoo1601 |
| `CHANGELOG.md` | Initial 0.1.0 entry |
| `README.md` | Overwrite placeholder with install instructions + quick-start |
| `.gitignore` | Unity ignores + build artifacts + .so binaries |
| `Plugins/Android/libs/arm64-v8a/.gitkeep` | Preserve directory structure for native binaries |

---

## Phase 1: Runtime Assembly + Leaf Types

Depends on Phase 0. All files created in parallel.

| File | Description |
|------|-------------|
| `Runtime/Jinwoo1601.VoskXR.Runtime.asmdef` | Assembly def: rootNamespace `VoskXR`, all platforms, no unsafe code |
| `Runtime/AssemblyInfo.cs` | `InternalsVisibleTo` for both test assemblies |
| `Runtime/VoskBridgeErrorCode.cs` | Enum mirroring native error codes (Ok=0 through AlreadyInitialised=7) + `ToDescription()` extension method |
| `Runtime/RecognitionResult.cs` | `internal readonly struct` with `string Text` and `bool IsFinal` |

---

## Phase 2: Native Interop Layer

Depends on Phase 1.

| File | Description |
|------|-------------|
| `Runtime/Native/BridgeNative.cs` | `internal static class` with `[Preserve]` attributes. All P/Invoke declarations using `IntPtr` returns (never `string`). Includes `MarshalResult()` and `GetLastError()` helpers. DllImport library name: `"vosk-bridge"`. |

**Key P/Invoke rules (IL2CPP-safe):**
- All string returns use `IntPtr` + `Marshal.PtrToStringUTF8`
- Guard `IntPtr.Zero` before marshalling
- `[Preserve]` on all methods and the class itself
- Polling architecture only — no native-to-managed callbacks

---

## Phase 3: Model Extraction

Depends on Phase 2.

| File | Description |
|------|-------------|
| `Runtime/ModelExtractor.cs` | `internal static class` implementing async model extraction with atomic rename pattern |

**Extraction flow (from PRD Section 7):**
1. Check cache at `{persistentDataPath}/VoskModels/{modelName}/`
2. Validate structure: `am/final.mdl`, `conf/mfcc.conf`, `graph/` directory
3. If invalid or missing, delete stale `.tmp_{modelName}/` directory
4. Read archive from StreamingAssets (Android: `UnityWebRequest` with `jar:file://`; Editor: direct file read)
5. Decompress to `.tmp_{modelName}/` via `System.IO.Compression.ZipArchive`
6. Validate temp directory
7. Atomic rename `.tmp_{modelName}/` → `{modelName}/` via `Directory.Move()`
8. Return filesystem path

**Failure guarantees:** Interrupted extraction leaves only `.tmp_` dir, cleaned up on next launch. Corrupt archives caught by validation. No partial state in final location.

---

## Phase 4: Main MonoBehaviour

Depends on Phase 3.

| File | Description |
|------|-------------|
| `Runtime/VoskSpeechRecogniser.cs` | Public entry point — the only class consumers interact with directly |

**Inspector fields:**
- `string modelRelativePath` (default: `"vosk-model-small-en-us-0.15"`)
- `float sampleRate` (default: `16000f`)

**Events:**
- `Action<string> OnPartialResult` — partial transcript text
- `Action<string> OnFinalResult` — final transcript text
- `Action<VoskBridgeErrorCode, string> OnError` — error code + description
- `Action OnModelReady` — model extraction complete

**Properties:**
- `bool IsInitialised` — calls through to bridge
- `bool IsRecognising` — calls through to bridge
- `bool IsModelReady` — C#-side flag

**Lifecycle methods:**
- `Initialise()` — async void, extracts model + calls bridge init
- `ReleaseNativeResources()` — calls bridge destroy, safe to call multiple times
- `StartRecognition()` — calls Initialise() if needed, then bridge start
- `StopRecognition()` — stops listening, model stays loaded
- `ResetRecogniser()` — clears recogniser state without stopping

**Update loop:** Polls `vosk_bridge_has_result()` in a `while` loop each frame, marshals results, fires events. Parses VOSK JSON (`{"text":"..."}` / `{"partial":"..."}`) via `JsonUtility`.

**Cleanup:** `OnDestroy()` always calls `ReleaseNativeResources()`.

**Editor guard:** Wraps first P/Invoke call in try/catch for `DllNotFoundException` — sets `_bridgeAvailable = false` and fires `OnError` instead of crashing.

---

## Phase 5: C++ Native Bridge

**Independent of C# phases — can be written in parallel with Phases 1–4.**

### 5A: Headers (no internal dependencies, all parallel)

| File | Description |
|------|-------------|
| `NativeBridge~/include/vosk_api.h` | VOSK C API declarations (copied from upstream vosk-api repo) |
| `NativeBridge~/src/vosk_bridge.h` | Bridge C API header: `VoskBridgeError` enum + all function declarations |
| `NativeBridge~/src/ring_buffer.h` | Header-only lock-free SPSC ring buffer, 65536 float samples, atomic indices, overflow flag |
| `NativeBridge~/src/downsampler.h` | Header-only FIR low-pass filter + 3:1 decimation (48kHz → 16kHz) |
| `NativeBridge~/src/result_queue.h` | Mutex-based thread-safe queue: `{string json, bool is_final}` |

### 5B: Implementations (depends on 5A)

| File | Description |
|------|-------------|
| `NativeBridge~/src/vosk_bridge.cpp` | Core bridge: static global state, init/destroy/start/stop/reset, recognition thread loop, result polling |
| `NativeBridge~/src/audio_capture_aaudio.cpp` | AAudio stream setup (48kHz float32 mono), data callback writes to ring buffer |

**Recognition thread loop:**
1. Read 48kHz samples from ring buffer
2. Downsample to 16kHz via FIR filter
3. Feed `vosk_recognizer_accept_waveform_f()`
4. Queue partial/final results
5. Check overflow flag
6. Sleep 10-20ms if no data
7. On stop: flush via `vosk_recognizer_final_result()`

### 5C: Build System (depends on 5B)

| File | Description |
|------|-------------|
| `NativeBridge~/CMakeLists.txt` | CMake config: C++17, targets arm64-v8a, links vosk + aaudio + log, NDK toolchain |
| `NativeBridge~/build.sh` | Convenience script: sets NDK path, invokes CMake, copies .so to Plugins/ |

**Build command (for reference):**
```bash
cmake -B build \
  -DCMAKE_TOOLCHAIN_FILE=$NDK_PATH/build/cmake/android.toolchain.cmake \
  -DANDROID_ABI=arm64-v8a \
  -DANDROID_PLATFORM=android-27 \
  -DANDROID_STL=c++_shared \
  -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release
```

---

## Phase 6: Tests

### 6A: Test Assembly Definitions (depends on Phase 1)

| File | Description |
|------|-------------|
| `Tests/Runtime/Jinwoo1601.VoskXR.Tests.Runtime.asmdef` | Play-mode tests, all platforms, refs Runtime + TestRunner |
| `Tests/Editor/Jinwoo1601.VoskXR.Tests.Editor.asmdef` | Edit-mode tests, Editor only, refs Runtime + TestRunner |

Both use `overrideReferences: true`, `precompiledReferences: ["nunit.framework.dll"]`, `defineConstraints: ["UNITY_INCLUDE_TESTS"]`.

### 6B: Test Files (depends on Phase 4 + 6A)

| File | Tests |
|------|-------|
| `Tests/Editor/VoskBridgeErrorCodeTests.cs` | Enum values match expected integers, ToDescription() returns non-empty |
| `Tests/Editor/ModelExtractorValidationTests.cs` | Validation passes/fails with correct/missing model files |
| `Tests/Editor/RecognitionResultTests.cs` | Struct construction, JSON parsing |
| `Tests/Runtime/VoskSpeechRecogniserLifecycleTests.cs` | Component add/destroy, StopRecognition when not running is no-op |

---

## Phase 7: Samples + Documentation

Depends on Phase 4.

| File | Description |
|------|-------------|
| `Samples~/BasicTranscription/VoiceDemo.cs` | Consumer example from PRD: subscribe to events, start/stop recognition, display with TMPro |
| `Samples~/BasicTranscription/README.md` | Step-by-step sample setup instructions |
| `Documentation~/vosk-xr.md` | Full package docs: installation, model setup, API reference, lifecycle guide, error handling, troubleshooting |

---

## Key Design Decisions

| Decision | Rationale |
|----------|-----------|
| `internal` for BridgeNative + ModelExtractor | Minimal public API — consumers only see VoskSpeechRecogniser + error/result types |
| Namespace: `VoskXR` | Short, memorable |
| `allowUnsafeCode: false` | P/Invoke uses IntPtr + Marshal, not unsafe pointers |
| Static globals in C++ bridge | Singleton per PRD (NG4: no multi-instance) |
| Header-only ring buffer/downsampler | Small, perf-critical, enables inlining |
| `while` loop in Update() | Drain all queued results per frame to prevent unbounded growth |
| JSON via JsonUtility | VOSK output is trivial `{"text":"..."}` — no Newtonsoft dependency needed |
| DllNotFoundException guard | Graceful fallback when .so is absent (Editor on Windows/Mac) |
| `async void Initialise()` | Matches MonoBehaviour pattern; errors routed via OnError event, not exceptions |

---

## Parallel Execution Timeline

```
Phase 0 ████                          (scaffolding)
         │
         ├── Phase 1 ████             (asmdef + leaf types)
         │    │
         │    ├── Phase 2 ███         (BridgeNative)
         │    │    │
         │    │    ├── Phase 3 ████   (ModelExtractor)
         │    │    │    │
         │    │    │    └── Phase 4 █████ (VoskSpeechRecogniser)
         │    │    │                  │
         │    │    │                  ├── Phase 6B ███ (test files)
         │    │    │                  │
         │    │    │                  └── Phase 7  ███ (samples + docs)
         │    │
         │    └── Phase 6A ██         (test asmdefs)
         │
         └── Phase 5A ████████        (C++ headers)
              │
              └── Phase 5B ████████   (C++ implementations)
                   │
                   └── Phase 5C ███   (CMake + build.sh)
```

---

## What We Can't Do From WSL

- Compile the C++ bridge (needs Android NDK — done separately)
- Create `.unity` scene files (binary format)
- Verify C# compilation (needs Unity Editor)
- Run tests (needs Unity Test Runner)
- Build prebuilt `.so` files for Plugins/

These are done after the source is written, in a Unity 6 project that references this package locally.
