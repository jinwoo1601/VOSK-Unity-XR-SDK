# VOSK XR Speech Recognition

Offline speech recognition for Unity XR applications. Wraps the [VOSK](https://alphacephei.com/vosk/) toolkit behind a Unity-native C# API with native audio capture on Android arm64.

## Features

- Fully offline — no internet required
- Native audio capture via AAudio (no Meta/OVR SDK dependency)
- Event-driven API: partial and final transcription results
- Two-tier lifecycle: fast push-to-talk without model reload
- Structured error codes for all failure modes
- Works on any Android arm64 XR device (Quest, Pico, Lynx)

## Requirements

- Unity 6 (6000.0+)
- Android arm64 build target

## Installation

**Via Git URL:**

1. Open Unity Package Manager (Window > Package Manager).
2. Click **+** > "Add package from git URL..."
3. Enter: `https://github.com/jinwoo1601/vosk-xr.git`

**Pinned version (recommended):**

```
https://github.com/jinwoo1601/vosk-xr.git#v0.1.0
```

**Via manifest.json:**

```json
{
  "dependencies": {
    "com.jinwoo1601.vosk-xr": "https://github.com/jinwoo1601/vosk-xr.git#v0.1.0"
  }
}
```

## Model Setup

The SDK does not bundle a VOSK model. You must download one separately:

1. Download [vosk-model-small-en-us-0.15](https://alphacephei.com/vosk/models) (~50 MB).
2. Place the `.zip` archive in your Unity project at `Assets/StreamingAssets/vosk-model-small-en-us-0.15.zip`.
3. The SDK extracts it to persistent storage on first launch.

## Quick Start

```csharp
using UnityEngine;
using VoskXR;

public class VoiceDemo : MonoBehaviour
{
    [SerializeField] private VoskSpeechRecogniser recogniser;

    private void OnEnable()
    {
        recogniser.OnPartialResult += text => Debug.Log($"Partial: {text}");
        recogniser.OnFinalResult += text => Debug.Log($"Final: {text}");
        recogniser.OnError += (code, msg) => Debug.LogError($"VOSK [{code}]: {msg}");
        recogniser.StartRecognition();
    }

    private void OnDisable()
    {
        recogniser.StopRecognition();
    }
}
```

## License

Apache 2.0. See [LICENSE.md](LICENSE.md).

VOSK is licensed under Apache 2.0 by [Alpha Cephei](https://alphacephei.com/).
