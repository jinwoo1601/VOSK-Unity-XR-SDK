# Getting Started

Everything you need to install the SDK, set up a VOSK model, and get your first speech recognition or voice command working in Unity.

---

## Installation

### Via Git URL

1. Open Unity Package Manager (Window > Package Manager).
2. Click **+** > "Add package from git URL..."
3. Enter: `https://github.com/jinwoo1601/VOSK-Unity-XR-SDK.git`

To pin a specific version (recommended):

```
https://github.com/jinwoo1601/VOSK-Unity-XR-SDK.git#v0.17.0
```

### Via manifest.json

Add to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.jinwoo1601.voxr": "https://github.com/jinwoo1601/VOSK-Unity-XR-SDK.git#v0.17.0"
  }
}
```

---

## Model Setup

The SDK does not include VOSK models. You must download one separately.

1. Visit [VOSK Models](https://alphacephei.com/vosk/models).
2. Download `vosk-model-small-en-us-0.15` (~50 MB) or another compatible model.
3. Place the `.zip` archive at `Assets/StreamingAssets/vosk-model-small-en-us-0.15.zip`.

The SDK extracts the model to `Application.persistentDataPath` on first launch. Subsequent launches use the cached extraction. The extraction uses an atomic rename pattern to prevent corruption from interrupted extractions.

Any VOSK-compatible model works. Larger models improve accuracy at the cost of memory and download size.

### Model Validation

The SDK validates extracted models by checking for:
- `am/final.mdl`
- `conf/mfcc.conf`
- `graph/` directory

If validation fails, the SDK deletes the corrupt cache and re-extracts on next launch.

---

## Quick Start -- Transcription

Attach a `VoxrSpeechRecogniser` component to a GameObject, then subscribe to its events:

```csharp
using UnityEngine;
using VoXR;

public class VoiceDemo : MonoBehaviour
{
    [SerializeField] VoxrSpeechRecogniser recogniser;

    void OnEnable()
    {
        recogniser.OnPartialResult += text => Debug.Log($"Partial: {text}");
        recogniser.OnFinalResult += text => Debug.Log($"Final: {text}");
        recogniser.OnResult += result =>
        {
            foreach (var word in result.Words)
                Debug.Log($"  {word.Text} conf={word.Confidence:F2} [{word.StartTime:F2}-{word.EndTime:F2}]");
        };
        recogniser.OnError += (code, msg) => Debug.LogError($"VOSK [{code}]: {msg}");
        recogniser.StartRecognition();
    }

    void OnDisable()
    {
        recogniser.StopRecognition();
    }
}
```

`OnPartialResult` fires continuously as you speak. `OnFinalResult` fires at utterance boundaries with the complete transcript. `OnResult` provides the same final text plus per-word confidence scores and timing.

---

## Quick Start -- Commands

Add a `VoxrCommandRecogniser` component alongside your `VoxrSpeechRecogniser`. Define slots (allowed values) and commands (patterns that reference those slots):

```csharp
using UnityEngine;
using VoXR;
using VoXR.Commands;

public class CommandExample : MonoBehaviour
{
    [SerializeField] VoxrSpeechRecogniser recogniser;
    [SerializeField] VoxrCommandRecogniser commandRecogniser;

    void Start()
    {
        var targets = VoxrSlotDefinition.OneOf("target", "alpha one", "bravo two", "hotel one");
        var weapons = VoxrSlotDefinition.OneOf("weapon", "missiles", "torpedoes");

        var commands = new[]
        {
            new VoxrCommandDefinition("launch_weapon",
                new[] { new[] { "launch", "{weapon}", "target", "{target}" } }),
            new VoxrCommandDefinition("cease_fire",
                new[] { new[] { "cease", "fire" } }),
        };

        commandRecogniser.Configure(new[] { targets, weapons }, commands);
        commandRecogniser.OnCommandRecognised += cmd =>
        {
            Debug.Log($"Intent: {cmd.Intent} score={cmd.Score:F2}");
            Debug.Log($"  target={cmd.GetSlot("target")} weapon={cmd.GetSlot("weapon")}");
        };

        recogniser.StartRecognition();
    }
}
```

When the user says "launch missiles target alpha one", the `OnCommandRecognised` event fires with `Intent="launch_weapon"`, `weapon="missiles"`, and `target="alpha one"`.

---

## Understanding the Lifecycle

The SDK uses a two-tier lifecycle that separates the expensive model load from the cheap audio start/stop.

### Heavyweight (model load / teardown)

- `Initialise()` / `InitialiseAsync()` -- loads the VOSK model and creates the recogniser. Takes seconds on first launch (model extraction from StreamingAssets).
- `ReleaseNativeResources()` -- frees all native resources. Called automatically by `OnDestroy()`.

### Lightweight (audio stream start / stop)

- `StartRecognition()` / `StartRecognitionAsync()` -- opens audio stream, starts recognition. Milliseconds. Calls `Initialise()` if needed.
- `StopRecognition()` -- stops audio, joins recognition thread. Model stays loaded.

This separation enables push-to-talk without model reload:

```
Initialise() --> StartRecognition() --> StopRecognition() --> StartRecognition() --> ...
    slow              fast                   fast                   fast
```

On Android, audio is captured on a native thread via JNI `AudioRecord`. Results are queued and delivered on Unity's main thread during `Update()`. In the Windows Editor, `EditorMicBackend` captures via `UnityEngine.Microphone` and processes synchronously on the main thread.

---

## Next Steps

- [Command Recognition](command-recognition.md) -- Learn how the full command parsing pipeline works, including patterns, slots, scoring, and grammar modes
- [Command Sets](command-sets.md) -- Organise commands into switchable groups for mode-specific grammars
- [Inspector Authoring](inspector-authoring.md) -- Set up commands without writing code using ScriptableObject assets
- [Editor Testing](editor-testing.md) -- Iterate without deploying to Quest using the debug window, live mic, and text injection
- [Push-to-Talk and Error Handling](push-to-talk.md) -- Implement push-to-talk and handle errors gracefully
