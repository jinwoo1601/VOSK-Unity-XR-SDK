# VoXR Speech Recognition

Offline speech recognition and voice command parsing for Unity XR applications. Wraps the [VOSK](https://alphacephei.com/vosk/) toolkit behind a Unity-native C# API with native audio capture on Android arm64 and live microphone capture in the Unity Editor on Windows.

## Features

**Speech Recognition**
- Fully offline -- no internet, no cloud dependency
- Native audio capture via AudioRecord JNI on Android (no Meta/OVR SDK dependency)
- Live microphone capture in the Unity Editor on Windows for rapid iteration
- Event-driven API: partial results, final results, per-word confidence, and timing
- Two-tier native lifecycle: heavy model load once, then start / stop recognition instantly
- Adaptive automatic gain control (AGC) with soft saturation
- Structured error codes for all failure modes
- Built-in push-to-talk controller with runtime-switchable listening mode

**Command Recognition**
- Grammar-constrained VOSK parsing for high-accuracy command match
- Intent and slot extraction with scored matching (0.0--1.0 confidence)
- Optional slots, multi-word slot values, slot value aliases
- `NumberSequence` slot type for spoken digit commands ("heading two seven zero" fills the slot with `"two seven zero"`; `VoxrNumberParser` converts it to `270`)
- Named command sets with runtime switching for mode-specific grammars
- Utterance buffer merges split speech across VOSK VAD boundaries
- Sequential command extraction (multiple commands per utterance)
- Per-intent debounce to suppress rapid duplicate firings
- Pending command system: partial match with follow-up slot-fill, explicit confirmation before firing, and asking the speaker which command they meant when two are indistinguishable
- Configurable `minConfidence` and `minScore` thresholds
- Free-speech mode toggle for unconstrained vocabulary with best-effort matching

**Authoring**
- Code-based `Configure()` API for full programmatic control
- ScriptableObject assets (`VoxrSlotAsset`, `VoxrCommandAsset`, `VoxrCommandSetAsset`) for zero-code Inspector setup
- Mix and match: Inspector authoring and code-based configuration on the same recogniser

**Testing & Iteration**
- Editor command debug window with live audio meters, match breakdowns, and match history
- Automatic session debug log -- every match of a Play Mode session exported to self-describing JSON for post-session analysis
- Batch test runner for regression-testing command definitions -- visual results table, CSV export, CI-safe pure-C# API
- Text injection API for Editor testing, CI, and replay without audio hardware
- Live microphone in the Windows Editor -- speak into your PC mic, see commands fire in the Console
- Manually verified on Quest 3 hardware before every release -- on-device checks are hands-on, and no test-matrix artefact is published

## Requirements

- Unity 6 (6000.0+)
- Android arm64 build target (for device deployment)
- Windows x86_64 (for Editor live microphone -- optional)

## Installation

**Via Git URL (recommended):**

1. Open Unity Package Manager (Window > Package Manager).
2. Click **+** > "Add package from git URL..."
3. Enter: `https://github.com/jinwoo1601/VoXR-Speech-Recognition.git`

**Pinned version:**

```
https://github.com/jinwoo1601/VoXR-Speech-Recognition.git#v1.4.0
```

**Via manifest.json:**

```json
{
  "dependencies": {
    "com.jinwoo1601.voxr": "https://github.com/jinwoo1601/VoXR-Speech-Recognition.git#v1.4.0"
  }
}
```

## Model Setup

The SDK does not bundle a VOSK model. Download [vosk-model-small-en-us-0.15](https://alphacephei.com/vosk/models) (~50 MB) and place the `.zip` archive at `Assets/StreamingAssets/vosk-model-small-en-us-0.15.zip`; any VOSK-compatible model works, with larger ones trading memory and download size for accuracy.

The cache path, the atomic extraction, model validation, and what happens when an archive is corrupt: [Model Setup](Documentation~/getting-started.md#model-setup).

## Quick Start -- Basic Transcription

Attach a `VoxrSpeechRecogniser` component to a GameObject (**Add Component > VoXR > Speech Recogniser**), then drag it into the script's `recogniser` field in the Inspector -- the field is a serialised reference and nothing resolves it for you.

```csharp
using UnityEngine;
using VoXR;

public class VoiceDemo : MonoBehaviour
{
    [SerializeField] private VoxrSpeechRecogniser recogniser;

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

Add a `VoxrCommandRecogniser` alongside the speech recogniser (**Add Component > VoXR > Command Recogniser**) and assign **both** components to the script's serialised fields in the Inspector.

```csharp
using System.Collections.Generic;
using UnityEngine;
using VoXR;
using VoXR.Commands;

public class CommandDemo : MonoBehaviour
{
    [SerializeField] private VoxrSpeechRecogniser recogniser;
    [SerializeField] private VoxrCommandRecogniser commandRecogniser;

    private void Start()
    {
        // Define slots
        var targets = VoxrSlotDefinition.OneOf("target", "alpha one", "bravo two", "hotel one");
        var weapons = VoxrSlotDefinition.OneOf("weapon", "missiles", "torpedoes");
        var quantity = new VoxrSlotDefinition("quantity",
            new[] { "one", "two", "three", "all" },
            new Dictionary<string, string> { { "a", "one" } });

        // Define commands
        var commands = new[]
        {
            new VoxrCommandDefinition("launch_weapon",
                new[] { new[] { "launch", "{?quantity}", "{weapon}", "target", "{target}" } }),
            new VoxrCommandDefinition("cease_fire",
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
- [Command Recognition](Documentation~/command-recognition.md) -- pipeline concepts, patterns, slots, scoring, pending commands, and resolving two commands that are too alike to tell apart
- [Matching and Scoring](Documentation~/scoring.md) -- score formula and miss costs, coverage, selection order, the two gates, eager-flush verdicts
- [Command Sets](Documentation~/command-sets.md) -- named sets, runtime mode switching
- [Inspector Authoring](Documentation~/inspector-authoring.md) -- zero-code ScriptableObject setup
- [Editor Testing](Documentation~/editor-testing.md) -- debug window, session debug log, live mic, text injection, batch runner
- [Push-to-Talk](Documentation~/push-to-talk.md) -- PTT pattern and error handling
- [API Reference](Documentation~/index.md#api-reference) -- per-type reference for all eight public API pages
- [Troubleshooting](Documentation~/troubleshooting.md) -- common issues and platform support

## Samples

Import samples via **Package Manager > VoXR Speech Recognition > Samples**.

| Sample | Description |
|---|---|
| **Basic Transcription** | Live speech-to-text with on-screen display. Demonstrates `VoxrSpeechRecogniser` events, partial/final results, and per-word confidence. |
| **Command Recognition** | Full command parsing with slots, command sets, mode switching, utterance buffering, and sequential extraction. Includes an Inspector authoring toggle and 20 ScriptableObject assets covering every slot type and pattern form. |
| **Push-to-Talk** | Hold-to-talk gating with `VoxrPushToTalkController`, runtime switching between push-to-talk and continuous modes, `UnityEvent` wiring for a recording indicator, and optional command-recogniser flush on release. |

## Architecture

```
VoxrSpeechRecogniser          -- MonoBehaviour, owns the native lifecycle
  |
  |-- [Android] BridgeNative  -- P/Invoke -> C++ bridge -> JNI AudioRecord + libvosk.so
  |-- [Editor]  EditorMicBackend -- UnityEngine.Microphone -> C# DSP -> libvosk.dll P/Invoke
  |
  +-- Events: OnPartialResult, OnFinalResult, OnResult, OnError
       |
VoxrCommandRecogniser         -- MonoBehaviour, subscribes to speech events
  |
  |-- VoxrCommandParser       -- grammar-constrained pattern matching
  |-- Utterance buffer        -- merges split VOSK results
  |-- Debounce                -- suppresses duplicate intents
  |
  +-- Events: OnCommandRecognised, OnCommandsRecognised, OnUnrecognisedSpeech
```

On Android, a C++ native bridge captures audio via Java `AudioRecord` (JNI), runs AGC and FIR downsampling (48 kHz -> 16 kHz), and feeds int16 samples to VOSK on a dedicated thread. In the Windows Editor, `EditorMicBackend` captures via `UnityEngine.Microphone`, runs equivalent C# DSP, and calls VOSK via P/Invoke on the main thread (the one-time model load is offloaded to a background `Task` to avoid a startup hitch).

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

See [CHANGELOG.md](CHANGELOG.md) for the full release history.

| Version | Milestone |
|---|---|
| 1.4.0 | Latency and accuracy tuning for command recognition -- `prefixHoldSeconds`, `skippedWordPenalty`, and vocabulary-aware eager-flush eligibility |
| 1.3.0 | Automatic session debug log -- every Play Mode match exported to self-describing JSON for post-session analysis |
| 1.2.0 | Zero-alloc byte-span parsing on the recognition hot path (breaking change to `vosk_bridge_get_result` native ABI) |
| 1.1.0 | Removed n-best alternatives (breaking change to `VoxrResult` and native ABI) |
| 1.0.0 | First stable release -- public API committed for the v1.x series |
| 0.17.0 | `VoxrPushToTalkController` and `VoxrListeningMode` for runtime-switchable push-to-talk |
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
