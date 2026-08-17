# Getting Started

Everything you need to install the SDK, set up a VOSK model, and get your first speech recognition or voice command working in Unity.

---

## Requirements

- **Unity 6 (6000.0+)** -- the package targets the Unity 6 runtime.
- **Android arm64** for device builds -- the native bridge ships for that ABI only, so Quest 2/3/Pro and other Android arm64 headsets are the supported deployment targets.
- **Windows Editor (x86_64)** for live-microphone testing in the Editor -- on macOS and Linux the Editor is limited to text injection.

Full per-platform detail, including what is deferred and what is untested, is in the [platform support table](troubleshooting.md#platform-support).

---

## Installation

### Via Git URL

1. Open Unity Package Manager (Window > Package Manager).
2. Click **+** > "Add package from git URL..."
3. Enter: `https://github.com/jinwoo1601/VoXR-Speech-Recognition.git`

To pin a specific version (recommended):

```
https://github.com/jinwoo1601/VoXR-Speech-Recognition.git#v1.4.0
```

### Via manifest.json

Add to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.jinwoo1601.voxr": "https://github.com/jinwoo1601/VoXR-Speech-Recognition.git#v1.4.0"
  }
}
```

---

## Model Setup

The SDK does not include VOSK models. You must download one separately.

1. Visit [VOSK Models](https://alphacephei.com/vosk/models).
2. Download `vosk-model-small-en-us-0.15` (~50 MB) or another compatible model.
3. Place the `.zip` archive at `Assets/StreamingAssets/vosk-model-small-en-us-0.15.zip`.

The SDK extracts the model to `Application.persistentDataPath/VoxrModels/<modelName>` on first launch, where `<modelName>` is the archive's file name without the `.zip`. Subsequent launches use the cached extraction. Extraction unpacks to a temporary sibling folder and finishes with an atomic rename, so an interrupted extraction cannot leave a half-written cache behind.

Any VOSK-compatible model works. Larger models improve accuracy at the cost of memory and download size.

### Model Validation

The SDK validates extracted models by checking for:
- `am/final.mdl`
- `conf/mfcc.conf`
- `graph/` directory

If an existing cache fails validation, the SDK deletes it and re-extracts immediately, within the same call -- no restart is needed to recover a bad cache.

If the freshly extracted copy fails validation, the archive itself is the problem: the SDK deletes the partial extraction, raises `ModelLoadFailed`, and returns no model. That repeats on **every** launch -- there is no next-launch self-heal for a corrupt archive. Replace the `.zip` in `StreamingAssets` to fix it.

---

## Quick Start -- Transcription

Attach a `VoxrSpeechRecogniser` component to a GameObject (**Add Component > VoXR > Speech Recogniser**), then drag that component into the script's `recogniser` field in the Inspector. The field is a serialised reference and nothing looks the component up for you -- an unassigned field throws a `NullReferenceException` on the first event subscription.

With the reference assigned, subscribe to its events:

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

Add a `VoxrCommandRecogniser` component alongside your `VoxrSpeechRecogniser` (**Add Component > VoXR > Command Recogniser**), and drag **both** components into the script's serialised fields in the Inspector -- as above, neither is resolved automatically. Then define slots (allowed values) and commands (patterns that reference those slots):

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

The native bridge is one per process, so **only one `VoxrSpeechRecogniser` can be initialised at a time**. A second one logs an error and stays inert rather than sharing the first's model; see [VoxrSpeechRecogniser](api/speech-recogniser.md#one-recogniser-per-process).

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
- [Editor Testing](editor-testing.md) -- Iterate without deploying to Quest using the debug window, session debug log, live mic, and text injection
- [Push-to-Talk and Error Handling](push-to-talk.md) -- Implement push-to-talk and handle errors gracefully
