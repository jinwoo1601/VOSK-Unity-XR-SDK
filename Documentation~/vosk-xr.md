# VOSK XR Speech Recognition — Documentation

## Table of Contents

- [Installation](#installation)
- [Model Setup](#model-setup)
- [Quick Start](#quick-start)
- [API Reference](#api-reference)
- [Lifecycle](#lifecycle)
- [Error Handling](#error-handling)
- [Push-to-Talk Pattern](#push-to-talk-pattern)
- [Building the Native Bridge](#building-the-native-bridge)
- [Troubleshooting](#troubleshooting)
- [Platform Support](#platform-support)

---

## Installation

### Via Git URL

1. Open Unity Package Manager (Window > Package Manager).
2. Click **+** > "Add package from git URL..."
3. Enter: `https://github.com/jinwoo1601/vosk-xr.git#v0.1.0`

### Via manifest.json

Add to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.jinwoo1601.vosk-xr": "https://github.com/jinwoo1601/vosk-xr.git#v0.1.0"
  }
}
```

---

## Model Setup

The SDK does not include VOSK models. Download separately:

1. Visit [VOSK Models](https://alphacephei.com/vosk/models).
2. Download `vosk-model-small-en-us-0.15` (~50 MB) or another model.
3. Place the `.zip` archive at `Assets/StreamingAssets/vosk-model-small-en-us-0.15.zip`.

The SDK extracts the model to `Application.persistentDataPath` on first launch. Subsequent launches use the cached extraction. The extraction uses an atomic rename pattern to prevent corruption from interrupted extractions.

### Model Validation

The SDK validates extracted models by checking for:
- `am/final.mdl`
- `conf/mfcc.conf`
- `graph/` directory

If validation fails, the SDK deletes the corrupt cache and re-extracts on next launch.

---

## Quick Start

```csharp
using UnityEngine;
using VoskXR;

public class VoiceDemo : MonoBehaviour
{
    [SerializeField] VoskSpeechRecogniser recogniser;

    void OnEnable()
    {
        recogniser.OnPartialResult += text => Debug.Log($"Partial: {text}");
        recogniser.OnFinalResult += text => Debug.Log($"Final: {text}");
        recogniser.OnError += (code, msg) => Debug.LogError($"VOSK [{code}]: {msg}");
        recogniser.StartRecognition();
    }

    void OnDisable()
    {
        recogniser.StopRecognition();
    }
}
```

---

## API Reference

### VoskSpeechRecogniser (MonoBehaviour)

The main entry point. Attach to a GameObject, configure via Inspector, subscribe to events.

#### Inspector Fields

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `modelRelativePath` | `string` | `"vosk-model-small-en-us-0.15"` | Path within StreamingAssets (without `.zip` extension) |
| `sampleRate` | `float` | `16000` | VOSK recogniser sample rate in Hz |

#### Events

| Event | Signature | Description |
|-------|-----------|-------------|
| `OnPartialResult` | `Action<string>` | Fired on the main thread with partial transcript text |
| `OnFinalResult` | `Action<string>` | Fired on the main thread with final transcript text |
| `OnError` | `Action<VoskBridgeErrorCode, string>` | Fired on the main thread with error code and description |
| `OnModelReady` | `Action` | Fired when model extraction completes |

#### Properties

| Property | Type | Description |
|----------|------|-------------|
| `IsInitialised` | `bool` | True after `Initialise()` succeeds, false after `ReleaseNativeResources()` |
| `IsRecognising` | `bool` | True between `StartRecognition()` and `StopRecognition()` |
| `IsModelReady` | `bool` | True once model extraction completes |

#### Methods

| Method | Description |
|--------|-------------|
| `Initialise()` | Extracts model (if needed) and initialises the native bridge. No-op if already initialised. |
| `ReleaseNativeResources()` | Destroys the native bridge and frees all resources. Safe to call multiple times. |
| `StartRecognition()` | Starts audio capture and recognition. Calls `Initialise()` if needed. |
| `StopRecognition()` | Stops audio capture. Model stays loaded for fast restart. |
| `ResetRecogniser()` | Clears recogniser state without stopping audio. |

### VoskBridgeErrorCode (enum)

| Value | Int | Description |
|-------|-----|-------------|
| `Ok` | 0 | Success |
| `ModelLoadFailed` | 1 | VOSK model failed to load |
| `AudioDeviceUnavailable` | 2 | Audio input device could not be opened |
| `PermissionDenied` | 3 | RECORD_AUDIO permission not granted |
| `RingBufferOverflow` | 4 | Audio buffer overflowed; recognition may have gaps |
| `AlreadyRunning` | 5 | Recognition is already running |
| `NotInitialised` | 6 | Bridge not initialised |
| `AlreadyInitialised` | 7 | Bridge already initialised |

---

## Lifecycle

The SDK uses a two-tier lifecycle:

### Heavyweight (model load / teardown)
- `Initialise()` — loads the VOSK model and creates the recogniser. Takes seconds on first launch (model extraction).
- `ReleaseNativeResources()` — frees all native resources.

### Lightweight (audio stream start / stop)
- `StartRecognition()` — opens audio stream, starts recognition thread. Milliseconds.
- `StopRecognition()` — stops audio, joins thread. Model stays loaded.

This separation enables push-to-talk without model reload:

```
Initialise() ──► StartRecognition() ──► StopRecognition() ──► StartRecognition() ──► ...
    slow              fast                   fast                   fast
```

`OnDestroy()` automatically calls `ReleaseNativeResources()`.

---

## Error Handling

All errors are surfaced via the `OnError` event with a `VoskBridgeErrorCode` and a human-readable description.

```csharp
recogniser.OnError += (code, message) =>
{
    switch (code)
    {
        case VoskBridgeErrorCode.PermissionDenied:
            // Prompt user to grant microphone permission
            break;
        case VoskBridgeErrorCode.ModelLoadFailed:
            // Check model archive in StreamingAssets
            break;
        default:
            Debug.LogError($"VOSK [{code}]: {message}");
            break;
    }
};
```

---

## Push-to-Talk Pattern

```csharp
public class PushToTalk : MonoBehaviour
{
    [SerializeField] VoskSpeechRecogniser recogniser;

    void Start()
    {
        // Pre-warm the model at scene load
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

---

## Building the Native Bridge

The prebuilt `libvosk-bridge.so` is included in the package. To build from source:

### Prerequisites
- Android NDK r26+ (set `ANDROID_NDK_HOME`)
- CMake 3.18+
- `libvosk.so` for Android arm64 (from [VOSK releases](https://github.com/alphacep/vosk-api/releases))

### Steps

1. Place `libvosk.so` in `Plugins/Android/libs/arm64-v8a/`.
2. Run the build script:

```bash
cd NativeBridge~
export ANDROID_NDK_HOME=/path/to/ndk
./build.sh
```

The script builds `libvosk-bridge.so` and copies it to `Plugins/Android/libs/arm64-v8a/`.

---

## Troubleshooting

### "Model archive not found in StreamingAssets"
Ensure the model `.zip` is at `Assets/StreamingAssets/<modelName>.zip` where `<modelName>` matches the `modelRelativePath` field on the `VoskSpeechRecogniser` component.

### "Microphone permission (RECORD_AUDIO) was not granted"
Add `RECORD_AUDIO` to your Android manifest or request it at runtime before calling `StartRecognition()`.

### "Native bridge library (libvosk-bridge) not found"
The native libraries are Android arm64 only. This error is expected in the Unity Editor on desktop platforms. Recognition only works on device builds.

### No transcription output
- Verify the model extracted successfully (check `OnModelReady` event).
- Check logcat for `vosk-bridge` tagged messages.
- Ensure `RECORD_AUDIO` permission is granted.

---

## Platform Support

| Platform | Status |
|----------|--------|
| Meta Quest (Android arm64) | Supported (v1.0) |
| Other Android arm64 XR (Pico, Lynx) | Should work (same binary, untested) |
| Unity Editor (Windows/Mac/Linux) | Not supported (no native bridge) |
| PC VR (Windows x64) | Planned (future) |
| Apple Vision Pro | Planned (future) |
