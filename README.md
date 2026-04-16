# VOSK XR Speech Recognition

Offline speech recognition and voice command parsing for Unity XR applications. Wraps the [VOSK](https://alphacephei.com/vosk/) toolkit behind a Unity-native C# API with native audio capture on Android arm64 and live microphone capture in the Unity Editor on Windows.

## Features

**Speech Recognition**
- Fully offline -- no internet, no cloud dependency
- Native audio capture via AudioRecord JNI on Android (no Meta/OVR SDK dependency)
- Live microphone capture in the Unity Editor on Windows for rapid iteration
- Event-driven API: partial results, final results, per-word confidence, timing, and n-best alternatives
- Two-tier native lifecycle: heavy model load once, then start / stop recognition instantly
- Adaptive automatic gain control (AGC) with soft saturation
- Structured error codes for all failure modes

**Command Recognition**
- Grammar-constrained VOSK parsing for high-accuracy command match
- Intent and slot extraction with scored matching (0.0--1.0 confidence)
- Optional slots, multi-word slot values, slot value aliases
- `NumberSequence` slot type for spoken digit commands (e.g. "heading two seven zero" -> 270)
- Named command sets with runtime switching for mode-specific grammars
- Utterance buffer merges split speech across VOSK VAD boundaries
- Sequential command extraction (multiple commands per utterance)
- Per-intent debounce to suppress rapid duplicate firings
- Pending command system: partial match with follow-up slot-fill, and explicit confirmation before firing
- Configurable `minConfidence` and `minScore` thresholds
- Free-speech mode toggle for unconstrained vocabulary with best-effort matching

**Authoring**
- Code-based `Configure()` API for full programmatic control
- ScriptableObject assets (`VoskSlotAsset`, `VoskCommandAsset`, `VoskCommandSetAsset`) for zero-code Inspector setup
- Mix and match: Inspector authoring and code-based configuration on the same recogniser

**Testing & Iteration**
- Editor command debug window with live audio meters, match breakdowns, and match history
- Batch test runner for regression-testing command definitions -- visual results table, CSV export, CI-safe Edit Mode API
- Text injection API for Editor testing, CI, and replay without audio hardware
- Live microphone in the Windows Editor -- speak into your PC mic, see commands fire in the Console
- 20 automated test suites (Edit Mode + Play Mode) covering parser, injection, lifecycle, DSP, diagnostics, batch testing, asset conversion, and pending commands
- Extensively tested on Quest 3 with published test matrices for every release

## Requirements

- Unity 6 (6000.0+)
- Android arm64 build target (for device deployment)
- Windows x86_64 (for Editor live microphone -- optional)

## Installation

**Via Git URL (recommended):**

1. Open Unity Package Manager (Window > Package Manager).
2. Click **+** > "Add package from git URL..."
3. Enter: `https://github.com/jinwoo1601/VOSK-Unity-XR-SDK.git`

**Pinned version:**

```
https://github.com/jinwoo1601/VOSK-Unity-XR-SDK.git#v0.15.0
```

**Via manifest.json:**

```json
{
  "dependencies": {
    "com.jinwoo1601.vosk-xr": "https://github.com/jinwoo1601/VOSK-Unity-XR-SDK.git#v0.15.0"
  }
}
```

## Model Setup

The SDK does not bundle a VOSK model. You must download one separately:

1. Download [vosk-model-small-en-us-0.15](https://alphacephei.com/vosk/models) (~50 MB).
2. Place the `.zip` archive in your Unity project at `Assets/StreamingAssets/vosk-model-small-en-us-0.15.zip`.
3. The SDK extracts it to persistent storage on first launch.

Any VOSK-compatible model works. Larger models improve accuracy at the cost of memory and download size.

## Windows Editor Setup (Optional)

Required only if you want the live microphone backend in the Unity Editor on Windows. Skip this if you only deploy to Android -- the Android native libraries are bundled in the package.

1. Download `vosk-win64-*.zip` from [alphacep/vosk-api releases](https://github.com/alphacep/vosk-api/releases).
2. Extract and drop these four DLLs into the package's `Runtime/Plugins/x86_64/` folder:
   - `libvosk.dll`
   - `libgcc_s_seh-1.dll`
   - `libstdc++-6.dll`
   - `libwinpthread-1.dll`
3. Plugin importer meta files are pre-configured for Editor-only loading on Windows x86_64 -- no build settings changes needed. These DLLs are excluded from Android and standalone builds.

If the DLLs are missing, `VoskSpeechRecogniser.OnError` fires with `ModelLoadFailed` on the first `StartRecognition()` call in the Editor. See [Editor Testing](Documentation~/editor-testing.md) for the full live-mic workflow.

## Quick Start -- Basic Transcription

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
        recogniser.OnResult += result =>
        {
            foreach (var word in result.Words)
                Debug.Log($"  {word.Text} conf={word.Confidence:F2} [{word.StartTime:F2}-{word.EndTime:F2}]");
        };
        recogniser.OnError += (code, msg) => Debug.LogError($"VOSK [{code}]: {msg}");
        recogniser.StartRecognition();
    }

    private void OnDisable()
    {
        recogniser.StopRecognition();
    }
}
```

## Quick Start -- Command Recognition

```csharp
using System.Collections.Generic;
using UnityEngine;
using VoskXR;
using VoskXR.Commands;

public class CommandDemo : MonoBehaviour
{
    [SerializeField] private VoskSpeechRecogniser recogniser;
    [SerializeField] private VoskCommandRecogniser commandRecogniser;

    private void Start()
    {
        // Define slots
        var targets = VoskSlotDefinition.OneOf("target", "alpha one", "bravo two", "hotel one");
        var weapons = VoskSlotDefinition.OneOf("weapon", "missiles", "torpedoes");
        var quantity = new VoskSlotDefinition("quantity",
            new[] { "one", "two", "three", "all" },
            new Dictionary<string, string> { { "a", "one" } });

        // Define commands
        var commands = new[]
        {
            new VoskCommandDefinition("launch_weapon",
                new[] { new[] { "launch", "{?quantity}", "{weapon}", "target", "{target}" } }),
            new VoskCommandDefinition("cease_fire",
                new[] { new[] { "cease", "fire" } }),
        };

        // Configure and start
        commandRecogniser.Configure(new[] { targets, weapons, quantity }, commands);
        commandRecogniser.OnCommandRecognised += cmd =>
        {
            Debug.Log($"Command: {cmd.Intent} (score={cmd.Score:F2})");
            Debug.Log($"  target={cmd.GetSlot("target")} weapon={cmd.GetSlot("weapon")}");
        };
        commandRecogniser.OnUnrecognisedSpeech += text => Debug.Log($"Unrecognised: {text}");

        recogniser.StartRecognition();
    }
}
```

## Documentation

For full documentation, see the [documentation index](Documentation~/index.md).

- [Getting Started](Documentation~/getting-started.md) -- installation, model setup, quick start, lifecycle
- [Command Recognition](Documentation~/command-recognition.md) -- pipeline concepts, patterns, slots, scoring, pending commands
- [Command Sets](Documentation~/command-sets.md) -- named sets, runtime mode switching
- [Inspector Authoring](Documentation~/inspector-authoring.md) -- zero-code ScriptableObject setup
- [Editor Testing](Documentation~/editor-testing.md) -- debug window, live mic, text injection, batch runner
- [Push-to-Talk](Documentation~/push-to-talk.md) -- PTT pattern and error handling
- [API Reference](Documentation~/api/speech-recogniser.md) -- full API documentation
- [Troubleshooting](Documentation~/troubleshooting.md) -- common issues and platform support

## Samples

Import samples via **Package Manager > VOSK XR Speech Recognition > Samples**.

| Sample | Description |
|---|---|
| **Basic Transcription** | Live speech-to-text with on-screen display. Demonstrates `VoskSpeechRecogniser` events, partial/final results, and per-word confidence. |
| **Command Recognition** | Full command parsing with slots, command sets, mode switching, utterance buffering, and sequential extraction. Includes an Inspector authoring toggle and 20 ScriptableObject assets covering every slot type and pattern form. |

## Running Tests

The package includes 20 test suites (Edit Mode and Play Mode) that run without audio hardware or a VOSK model. Add `"testables": ["com.jinwoo1601.vosk-xr"]` to your project's `Packages/manifest.json`, then open **Window > General > Test Runner**. See [Getting Started](Documentation~/getting-started.md) for details.

## Architecture

```
VoskSpeechRecogniser          -- MonoBehaviour, owns the native lifecycle
  |
  |-- [Android] BridgeNative  -- JNI -> C++ bridge -> AudioRecord + libvosk.so
  |-- [Editor]  EditorMicBackend -- UnityEngine.Microphone -> C# DSP -> libvosk.dll P/Invoke
  |
  +-- Events: OnPartialResult, OnFinalResult, OnResult, OnError
       |
VoskCommandRecogniser         -- MonoBehaviour, subscribes to speech events
  |
  |-- VoskCommandParser       -- grammar-constrained pattern matching
  |-- Utterance buffer        -- merges split VOSK results
  |-- Debounce                -- suppresses duplicate intents
  |
  +-- Events: OnCommandRecognised, OnCommandsRecognised, OnUnrecognisedSpeech
```

On Android, a C++ native bridge captures audio via Java `AudioRecord` (JNI), runs AGC and FIR downsampling (48 kHz -> 16 kHz), and feeds int16 samples to VOSK on a dedicated thread. In the Windows Editor, `EditorMicBackend` captures via `UnityEngine.Microphone`, runs equivalent C# DSP, and calls VOSK via P/Invoke on the main thread.

## Platform Support

| Platform | Status |
|---|---|
| Meta Quest 2/3/Pro (Android arm64) | Supported -- primary target, extensively tested |
| Other Android arm64 XR (Pico, Lynx) | Should work -- same native bridge, not yet device-tested |
| Windows Editor (x86_64) | Supported -- live mic + text injection for iteration |
| Standalone Windows (PCVR) | Not yet supported -- architecturally ready, deferred to a future release |
| macOS / Linux Editor | Text injection only -- no live mic backend |

## Known Limitations

See [KNOWN_LIMITATIONS.md](KNOWN_LIMITATIONS.md) for the full list with repro steps, root causes, and workarounds. Key items:

- **Short homophones**: VOSK's small model may misrecognise "to" as "two", "all" as "fall", etc. Prefer longer, phonetically distinct command words.
- **Single-character words**: "a" is unreliable in grammar mode. Use aliases (`"a" -> "one"`) instead.
- **Free-speech mode**: Significantly less accurate than grammar-constrained mode for commands. Use grammar mode for production.
- **Set switching audio gap**: `SetActiveSets()` causes a brief (~50 ms) audio gap during grammar rebuild. Pause ~500 ms before the next command.
- **Mid-command pauses**: Pauses exceeding `bufferWindow` split the command. Tune `bufferWindow` (2.0s recommended on Quest 3).

## Versioning

This project follows [Semantic Versioning](https://semver.org/). See [CHANGELOG.md](CHANGELOG.md) for the full release history.

| Version | Milestone |
|---|---|
| 0.16.0 | Internal refactoring and per-utterance allocation reduction |
| 0.15.0 | Pending commands: partial match, confirmation, follow-up slot-fill |
| 0.14.0 | Dynamic slot value providers for runtime parser filtering |
| 0.13.0 | Batch test runner for regression-testing command definitions |
| 0.12.0 | Editor command debug window with live diagnostics |
| 0.11.0 | Windows Editor live microphone backend |
| 0.10.0 | Text injection API for Editor/CI testing |
| 0.9.0 | Inspector authoring (ScriptableObject assets) |
| 0.8.0 | Command sets with runtime switching |
| 0.7.0 | Utterance buffer, sequential extraction, debounce |
| 0.6.0 | NumberSequence slot type |
| 0.5.0 | Scored matching, aliases, optional literals |
| 0.4.0 | Command recognition system |
| 0.3.0 | Per-word confidence and n-best alternatives |
| 0.2.0 | Adaptive AGC, AudioRecord JNI for Quest 3 |
| 0.1.0 | Initial release -- offline speech-to-text on Quest |

## License

Apache 2.0. See [LICENSE.md](LICENSE.md).

VOSK is licensed under Apache 2.0 by [Alpha Cephei](https://alphacephei.com/).
