# VOSK XR Speech Recognition -- Documentation

## Table of Contents

- [Installation](#installation)
- [Model Setup](#model-setup)
- [Quick Start -- Transcription](#quick-start----transcription)
- [Quick Start -- Commands](#quick-start----commands)
- [API Reference: VoskSpeechRecogniser](#api-reference-voskspeechrecogniser)
- [API Reference: VoskCommandRecogniser](#api-reference-voskcommandrecogniser)
- [API Reference: Data Types](#api-reference-data-types)
- [API Reference: Command Definitions](#api-reference-command-definitions)
- [API Reference: ScriptableObject Assets](#api-reference-scriptableobject-assets)
- [API Reference: VoskNumberParser](#api-reference-vosknumberparser)
- [API Reference: VoskBridgeErrorCode](#api-reference-voskbridgeerrorcode)
- [Lifecycle](#lifecycle)
- [Command Recognition](#command-recognition)
  - [Patterns and Slots](#patterns-and-slots)
  - [Optional Slots](#optional-slots)
  - [Scored Matching](#scored-matching)
  - [Slot Value Aliases](#slot-value-aliases)
  - [NumberSequence Slots](#numbersequence-slots)
  - [Command Sets](#command-sets)
  - [Utterance Buffer](#utterance-buffer)
  - [Sequential Extraction](#sequential-extraction)
  - [Debounce](#debounce)
  - [Grammar Mode vs Free Speech](#grammar-mode-vs-free-speech)
- [Inspector Authoring](#inspector-authoring)
- [Editor Iteration](#editor-iteration)
  - [Command Debug Window](#command-debug-window)
  - [Live Microphone (Windows Editor)](#live-microphone-windows-editor)
  - [Text Injection API](#text-injection-api)
  - [Batch Test Runner](#batch-test-runner)
- [Push-to-Talk Pattern](#push-to-talk-pattern)
- [Error Handling](#error-handling)
- [Running Tests](#running-tests)
- [Building the Native Bridge](#building-the-native-bridge)
- [Troubleshooting](#troubleshooting)
- [Platform Support](#platform-support)
- [Known Limitations](#known-limitations)

---

## Installation

### Via Git URL

1. Open Unity Package Manager (Window > Package Manager).
2. Click **+** > "Add package from git URL..."
3. Enter: `https://github.com/jinwoo1601/VOSK-Unity-XR-SDK.git`

To pin a specific version (recommended):

```
https://github.com/jinwoo1601/VOSK-Unity-XR-SDK.git#v0.13.0
```

### Via manifest.json

Add to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.jinwoo1601.vosk-xr": "https://github.com/jinwoo1601/VOSK-Unity-XR-SDK.git#v0.13.0"
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

Any VOSK-compatible model works. Larger models improve accuracy at the cost of memory and download size.

### Model Validation

The SDK validates extracted models by checking for:
- `am/final.mdl`
- `conf/mfcc.conf`
- `graph/` directory

If validation fails, the SDK deletes the corrupt cache and re-extracts on next launch.

---

## Quick Start -- Transcription

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

---

## Quick Start -- Commands

```csharp
using UnityEngine;
using VoskXR;
using VoskXR.Commands;

public class CommandExample : MonoBehaviour
{
    [SerializeField] VoskSpeechRecogniser recogniser;
    [SerializeField] VoskCommandRecogniser commandRecogniser;

    void Start()
    {
        var targets = VoskSlotDefinition.OneOf("target", "alpha one", "bravo two", "hotel one");
        var weapons = VoskSlotDefinition.OneOf("weapon", "missiles", "torpedoes");

        var commands = new[]
        {
            new VoskCommandDefinition("launch_weapon",
                new[] { "launch", "{weapon}", "target", "{target}" }),
            new VoskCommandDefinition("cease_fire",
                new[] { "cease", "fire" }),
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

---

## API Reference: VoskSpeechRecogniser

`public class VoskSpeechRecogniser : MonoBehaviour` -- Namespace: `VoskXR`

The core speech recognition component. Attach to a GameObject, configure via Inspector, subscribe to events.

### Inspector Fields

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `modelRelativePath` | `string` | `"vosk-model-small-en-us-0.15"` | Path within StreamingAssets (without `.zip` extension) |
| `sampleRate` | `float` | `16000` | VOSK recogniser sample rate in Hz |
| `micGainTargetDb` | `float` | `-18` | AGC target level in dB (calibrated for Quest 3) |
| `maxAlternatives` | `int` | `0` | Number of n-best alternative hypotheses to return (0 = disabled) |

### Events

| Event | Signature | Description |
|-------|-----------|-------------|
| `OnPartialResult` | `Action<string>` | Fired on the main thread with partial transcript text as speech is being recognised |
| `OnFinalResult` | `Action<string>` | Fired on the main thread with final transcript text at utterance boundaries |
| `OnResult` | `Action<VoskResult>` | Fired with final result including per-word confidence, timing, and n-best alternatives |
| `OnError` | `Action<VoskBridgeErrorCode, string>` | Fired on the main thread with error code and human-readable description |
| `OnModelReady` | `Action` | Fired when model extraction and initialisation completes |

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `IsInitialised` | `bool` | True after `Initialise()` succeeds, false after `ReleaseNativeResources()` |
| `IsRecognising` | `bool` | True between `StartRecognition()` and `StopRecognition()` |
| `IsModelReady` | `bool` | True once model extraction and validation completes |

### Methods

| Method | Description |
|--------|-------------|
| `Initialise()` | Extracts model (if needed) and initialises the native bridge. No-op if already initialised. Fire-and-forget async wrapper. |
| `InitialiseAsync()` | `async Task`. Asynchronously initialises the native bridge with model loading. |
| `ReleaseNativeResources()` | Destroys the native bridge and frees all resources. Safe to call multiple times. |
| `StartRecognition()` | Starts audio capture and recognition. Calls `Initialise()` if needed. Fire-and-forget async wrapper. |
| `StartRecognitionAsync()` | `async Task`. Asynchronously starts recognition with permission handling. |
| `StopRecognition()` | Stops audio capture. Model stays loaded for fast restart. |
| `ResetRecogniser()` | Clears recogniser state without stopping audio. |
| `SetGrammar(string grammarJson)` | Sets a VOSK grammar JSON string for constrained recognition. Typically called by `VoskCommandRecogniser` internally. |
| `InjectResult(string text, VoskWord[] words, VoskAlternative[] alternatives)` | Fires `OnFinalResult` and `OnResult` as if VOSK recognised the text. Bypasses native bridge state -- use for Editor testing, replay, and CI. All parameters except `text` are optional. |
| `InjectPartialResult(string text)` | Fires `OnPartialResult` as if VOSK produced the partial text. |
| `CreateSimulatedWords(string text, float confidence)` | **Static.** Generates `VoskWord[]` from text with uniform confidence and sequential timing. Useful for threshold testing via injection. Default confidence is `1.0f`. |

---

## API Reference: VoskCommandRecogniser

`public class VoskCommandRecogniser : MonoBehaviour` -- Namespace: `VoskXR.Commands`

Subscribes to `VoskSpeechRecogniser` events and runs recognised text through the command parser pipeline: pattern matching, confidence/score thresholds, utterance buffering, sequential extraction, and debounce.

### Inspector Fields

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `speechRecogniser` | `VoskSpeechRecogniser` | -- | Reference to the speech recogniser component |
| `minConfidence` | `float` | `0.4` | Minimum per-word confidence to accept a command. Commands with confidence below this are rejected. `-1` (no data) bypasses this check. |
| `minScore` | `float` | `0.5` | Minimum pattern match score (0.0--1.0) to accept a command |
| `bufferWindow` | `float` | `1.5` | Seconds to buffer consecutive VOSK results before parsing. Merges speech split by mid-command pauses. Recommended: `2.0` on Quest 3. |
| `commandCooldown` | `float` | `0.5` | Per-intent debounce window in seconds. Suppresses duplicate firings of the same intent within this period. |
| `freeSpeechMode` | `bool` | `false` | When true, disables grammar constraint for unconstrained vocabulary with best-effort command matching |
| `slotAssets` | `VoskSlotAsset[]` | -- | Slot definitions for Inspector authoring |
| `commandSetAssets` | `VoskCommandSetAsset[]` | -- | Command set definitions for Inspector authoring |
| `initialActiveSetNames` | `string[]` | -- | Which sets to activate on startup when using Inspector authoring |

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `ActiveSetNames` | `string[]` | Names of currently active command sets (returns a snapshot copy) |

### Events

| Event | Signature | Description |
|-------|-----------|-------------|
| `OnCommandRecognised` | `Action<VoskCommand>` | Fired for each successfully recognised command that passes threshold and debounce filters |
| `OnCommandsRecognised` | `Action<VoskCommand[]>` | Fired with the full batch of commands extracted from a single utterance (after sequential extraction) |
| `OnUnrecognisedSpeech` | `Action<string>` | Fired when speech does not match any command pattern |

### Methods

| Method | Signature | Description |
|--------|-----------|-------------|
| `Configure` | `(VoskSlotDefinition[] slots, VoskCommandDefinition[] commands)` | Builds parser from slot and command definitions. Applies grammar constraint immediately if recognition is running. Use for simple setups without command sets. |
| `Configure` | `(VoskSlotDefinition[] slots, VoskCommandSet[] sets)` | Registers shared slots and named command sets. Does not activate any set -- call `SetActiveSets()` after. |
| `SetActiveSets` | `(params string[] setNames)` | Activates one or more named sets, rebuilding the parser and grammar from only those sets' commands. Handles stop/set/start if recognition is running. |
| `SetActiveSet` | `(string setName)` | Convenience wrapper for activating a single set. |
| `InjectText` | `(string text, VoskWord[] words = null)` | Injects text into the full command pipeline (parser -> threshold -> buffer -> debounce) as if it arrived from VOSK. Main-thread only. |
| `FlushPendingBuffer` | `()` | Immediately flushes any speech held in the utterance buffer, forcing parse. Useful for push-to-talk release, scene transitions, and synchronous test injection. |

---

## API Reference: Data Types

All data types are in namespace `VoskXR` (result types) or `VoskXR.Commands` (command types).

### VoskResult

`public readonly struct VoskResult` -- the full recognition result with word-level data.

| Field | Type | Description |
|-------|------|-------------|
| `Text` | `string` | The full recognised text |
| `Words` | `VoskWord[]` | Per-word confidence and timing for the best hypothesis (empty if unavailable) |
| `Alternatives` | `VoskAlternative[]` | N-best alternative hypotheses ranked best-first (empty when `maxAlternatives` is 0) |

### VoskWord

`public readonly struct VoskWord` -- a single recognised word with metadata.

| Field | Type | Description |
|-------|------|-------------|
| `Text` | `string` | The recognised word |
| `Confidence` | `float` | Confidence score in range [0, 1] |
| `StartTime` | `float` | Start time in seconds from beginning of utterance |
| `EndTime` | `float` | End time in seconds from beginning of utterance |

### VoskAlternative

`public readonly struct VoskAlternative` -- an alternative recognition hypothesis.

| Field | Type | Description |
|-------|------|-------------|
| `Text` | `string` | The recognised text for this hypothesis |
| `Confidence` | `float` | Acoustic model score (higher = better match) |
| `Words` | `VoskWord[]` | Per-word data for this hypothesis (may be empty) |

### VoskCommand

`public readonly struct VoskCommand` -- a parsed command with intent and extracted slots.

| Field | Type | Description |
|-------|------|-------------|
| `Intent` | `string` | The matched command intent (e.g. `"launch_weapon"`) |
| `Slots` | `VoskSlotMatch[]` | Matched slot name/value pairs |
| `Confidence` | `float` | Minimum word confidence across matched tokens. `-1` means no word data was available. |
| `Score` | `float` | Pattern match quality (0.0--1.0). Higher is better. |
| `RawText` | `string` | The original VOSK transcript text |

| Method | Description |
|--------|-------------|
| `GetSlot(string name)` | Returns the value of a named slot, or empty string if not matched. Logs a warning if the slot name was not registered. |
| `HasSlot(string name)` | Returns true if the named slot was matched in this command. |

### VoskSlotMatch

`public readonly struct VoskSlotMatch` -- a single slot extraction result.

| Field | Type | Description |
|-------|------|-------------|
| `Name` | `string` | Slot name (e.g. `"weapon"`) |
| `Value` | `string` | Matched value (e.g. `"missiles"`) |

### VoskCommandResult

`public readonly struct VoskCommandResult` -- parser output wrapping match/no-match.

| Field | Type | Description |
|-------|------|-------------|
| `IsMatch` | `bool` | True if a command pattern matched |
| `Command` | `VoskCommand` | The parsed command (only valid when `IsMatch` is true) |
| `RawText` | `string` | The original VOSK transcript |

---

## API Reference: Command Definitions

### VoskCommandDefinition

`public readonly struct VoskCommandDefinition` -- declares a command pattern.

| Field | Type | Description |
|-------|------|-------------|
| `Intent` | `string` | The intent name (e.g. `"launch_weapon"`) |
| `Patterns` | `string[][]` | One or more token arrays. Each token is a literal word, `{slotName}`, or `{?slotName}` (optional). |

```csharp
// Single pattern
new VoskCommandDefinition("cease_fire", new[] { "cease", "fire" })

// Multiple patterns (alternative phrasings)
new VoskCommandDefinition("launch_weapon", new[] {
    new[] { "launch", "{?quantity}", "{weapon}", "target", "{target}" },
    new[] { "fire", "{weapon}", "at", "{target}" },
})
```

### VoskSlotDefinition

`public readonly struct VoskSlotDefinition` -- declares a named slot with allowed values.

| Field | Type | Description |
|-------|------|-------------|
| `Name` | `string` | Slot name referenced in patterns as `{name}` or `{?name}` |
| `Type` | `VoskSlotType` | `Enumerated` (fixed values) or `NumberSequence` (digit words) |
| `Values` | `string[]` | Allowed values (Enumerated only) |
| `Aliases` | `Dictionary<string, string>` | Maps variant words to canonical values |
| `MinWords` | `int` | Minimum digit words to consume (NumberSequence only) |
| `MaxWords` | `int` | Maximum digit words to consume (NumberSequence only) |

**Factory methods:**

```csharp
// Enumerated slot with fixed values
var targets = VoskSlotDefinition.OneOf("target", "alpha one", "bravo two", "hotel one");

// Enumerated slot with aliases (constructor)
var quantity = new VoskSlotDefinition("quantity",
    new[] { "one", "two", "three", "all" },
    new Dictionary<string, string> { { "a", "one" } });

// NumberSequence slot for digit words
var heading = VoskSlotDefinition.NumberSequence("heading", minWords: 1, maxWords: 3);
// Matches: "two seven zero" -> 270, "one eight" -> 18
```

### VoskSlotType

`public enum VoskSlotType`

| Value | Description |
|-------|-------------|
| `Enumerated` | Matches against a fixed set of allowed values and aliases |
| `NumberSequence` | Greedily consumes consecutive digit-word tokens ("zero" through "nine") |

### VoskCommandSet

`public readonly struct VoskCommandSet` -- a named group of commands for mode-specific grammar.

| Field | Type | Description |
|-------|------|-------------|
| `Name` | `string` | Set name (e.g. `"weapons"`, `"navigation"`) |
| `Commands` | `VoskCommandDefinition[]` | Commands in this set |

```csharp
var weaponsSet = new VoskCommandSet("weapons", new[] {
    new VoskCommandDefinition("launch_weapon", ...),
    new VoskCommandDefinition("cease_fire", ...),
});
```

---

## API Reference: ScriptableObject Assets

For zero-code Inspector authoring. Create via **Assets > Create > VOSK XR**.

### VoskSlotAsset

`public class VoskSlotAsset : ScriptableObject`

| Field | Type | Description |
|-------|------|-------------|
| `slotName` | `string` | Slot name used in pattern references |
| `slotType` | `VoskSlotType` | `Enumerated` or `NumberSequence` |
| `values` | `string[]` | Allowed values (Enumerated only) |
| `aliases` | `AliasEntry[]` | Variant-to-canonical mappings |
| `minWords` | `int` | Minimum digit words (NumberSequence, default 1) |
| `maxWords` | `int` | Maximum digit words (NumberSequence, default 3) |

| Method | Description |
|--------|-------------|
| `ToDefinition()` | Converts to runtime `VoskSlotDefinition` struct |

**AliasEntry** (nested serializable struct):

| Field | Type | Description |
|-------|------|-------------|
| `variant` | `string` | The variant word/phrase (e.g. `"jackals"`) |
| `canonical` | `string` | The canonical value it maps to (e.g. `"jackal"`) |

### VoskCommandAsset

`public class VoskCommandAsset : ScriptableObject`

| Field | Type | Description |
|-------|------|-------------|
| `intent` | `string` | The intent name |
| `patterns` | `string[]` | Pattern strings with space-separated tokens (e.g. `"launch {?quantity} {weapon} target {target}"`) |

| Method | Description |
|--------|-------------|
| `ToDefinition()` | Converts to runtime `VoskCommandDefinition` struct. Splits pattern strings on whitespace into token arrays. |

### VoskCommandSetAsset

`public class VoskCommandSetAsset : ScriptableObject`

| Field | Type | Description |
|-------|------|-------------|
| `setName` | `string` | The command set name |
| `commands` | `VoskCommandAsset[]` | Command assets in this set |

| Method | Description |
|--------|-------------|
| `ToSet()` | Converts to runtime `VoskCommandSet` struct. Skips null entries with a warning. |

### VoskTestSuiteAsset

`public class VoskTestSuiteAsset : ScriptableObject` -- Create via **Assets > Create > VOSK XR > Test Suite**.

A collection of test cases for regression-testing command definitions with the [Batch Test Runner](#batch-test-runner).

| Field | Type | Description |
|-------|------|-------------|
| `suiteName` | `string` | Human-readable name for this test suite |
| `cases` | `List<VoskTestCase>` | Test cases to run |

| Method | Description |
|--------|-------------|
| `ToArray()` | Returns `cases` as a `VoskTestCase[]` for `VoskBatchTestRunner.RunAll()`. |
| `ToJson()` | Serializes all cases to a JSON string for portability and version control. |
| `FromJson(string json)` | Replaces `cases` from a JSON string. |

**VoskTestCase** (serializable struct):

| Field | Type | Description |
|-------|------|-------------|
| `input` | `string` | Text to feed through the command parser |
| `expectedIntent` | `string` | Expected intent name. Empty = expect rejection. |
| `expectedSlots` | `ExpectedSlot[]` | Array of `{name, value}` pairs. Omit to skip slot verification. |
| `wordConfidence` | `float` | Simulated uniform word confidence (0--1). `-1` = omit word data. |
| `description` | `string` | Human-readable description for the results table |

---

## API Reference: VoskNumberParser

`public static class VoskNumberParser` -- Namespace: `VoskXR.Commands`

Converts spoken digit words into integers.

| Member | Description |
|--------|-------------|
| `DigitVocabulary` | `static readonly HashSet<string>`. All number words the parser recognises (zero through nine, plus cardinals like hundred, thousand). |
| `ParseDigitSequence(string words)` | Parses digit-per-word sequences by concatenating single digits. `"two seven zero"` -> `270`. Returns `0` for null/empty. Throws `FormatException` for unrecognised words. |
| `ParseCardinal(string words)` | Parses cardinal number phrases. `"fifteen"` -> `15`, `"two hundred"` -> `200`. Returns `0` for null/empty. Throws `FormatException` for unrecognised words. |

---

## API Reference: VoskBridgeErrorCode

`public enum VoskBridgeErrorCode` -- Namespace: `VoskXR`

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

**Extension method:** `ToDescription()` returns a human-readable string for each code.

---

## Lifecycle

The SDK uses a two-tier lifecycle:

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

## Command Recognition

### Patterns and Slots

Commands are defined as token arrays. Literal tokens must appear in the speech. Slot tokens (wrapped in `{}`) match against registered slot values.

```csharp
// Pattern: "launch {weapon} target {target}"
// Matches: "launch missiles target alpha one"
// Extracts: weapon="missiles", target="alpha one"
new VoskCommandDefinition("launch_weapon",
    new[] { "launch", "{weapon}", "target", "{target}" })
```

Multi-word slot values (e.g. `"alpha one"`) are consumed greedily -- the parser tries longer matches first.

### Optional Slots

Prefix a slot reference with `?` to make it optional. The parser consumes it if present, skips if absent.

```csharp
// "{?quantity}" is optional
new[] { "launch", "{?quantity}", "{weapon}", "target", "{target}" }
// Matches both: "launch missiles target alpha one"
//           and: "launch two missiles target alpha one"
```

Optional literal tokens also work: `"?the"`, `"?a"`. Note that single-character words are unreliable in VOSK grammar mode -- prefer aliases instead.

### Scored Matching

Every match produces a normalised score (0.0--1.0) based on how well the input covers the pattern. The parser uses sliding start to tolerate preamble, hesitations, and false starts.

```csharp
commandRecogniser.minScore = 0.5f;       // Reject low-quality pattern matches
commandRecogniser.minConfidence = 0.4f;   // Reject low VOSK word confidence
```

The `Score` field on `VoskCommand` indicates match quality. `Confidence` is the minimum per-word VOSK confidence across matched tokens (`-1` means no word data was available, which bypasses the `minConfidence` check).

### Slot Value Aliases

Map variant words to canonical values:

```csharp
var quantity = new VoskSlotDefinition("quantity",
    new[] { "one", "two", "three", "all" },
    new Dictionary<string, string> { { "a", "one" }, { "jackals", "jackal" } });
```

When VOSK transcribes `"a"`, the alias resolves it to `"one"` in the extracted slot value. Aliases are included in the generated grammar JSON so VOSK knows to listen for them.

**Validation:** The parser warns at configure time about single-character slot values and alias keys, as these are unreliable in VOSK grammar mode.

### NumberSequence Slots

Parse spoken digit words into concatenated integers:

```csharp
var heading = VoskSlotDefinition.NumberSequence("heading", minWords: 1, maxWords: 3);

// "heading two seven zero" -> heading=270
// "heading one eight"      -> heading=18
```

The parser greedily consumes consecutive digit words ("zero" through "nine") within the configured `minWords`/`maxWords` range. Digit vocabulary is automatically merged into the grammar JSON.

Use `VoskNumberParser.ParseDigitSequence()` in your command handler to convert the extracted string to an integer:

```csharp
commandRecogniser.OnCommandRecognised += cmd =>
{
    if (cmd.Intent == "set_heading")
    {
        int heading = VoskNumberParser.ParseDigitSequence(cmd.GetSlot("heading"));
        Debug.Log($"Heading: {heading}");
    }
};
```

### Command Sets

Group commands into named sets for mode-specific grammars. Inactive sets are excluded from the grammar entirely, reducing VOSK's search space and preventing out-of-mode matches.

```csharp
var weaponsSet = new VoskCommandSet("weapons", weaponCommands);
var navSet = new VoskCommandSet("navigation", navCommands);
var commonSet = new VoskCommandSet("common", modeCommands);

// Register all sets (none active yet)
commandRecogniser.Configure(slots, new[] { weaponsSet, navSet, commonSet });

// Activate specific sets
commandRecogniser.SetActiveSets("weapons", "common");

// Switch modes at runtime
commandRecogniser.OnCommandRecognised += cmd =>
{
    if (cmd.Intent == "mode_navigation")
        commandRecogniser.SetActiveSets("navigation", "common");
};
```

`SetActiveSets()` rebuilds the parser and grammar, which causes a brief (~50 ms) audio gap on Quest. Pause ~500 ms before the next command after switching.

### Utterance Buffer

VOSK's voice activity detector can split mid-command pauses into separate utterances. The utterance buffer merges consecutive VOSK results within `bufferWindow` seconds before parsing.

```csharp
commandRecogniser.bufferWindow = 2.0f; // Recommended for Quest 3
```

If the speaker says "launch missiles" *pause* "target hotel one" and both results arrive within the window, they are concatenated and parsed as one command.

**Tuning:** 1.5s is the default. Quest 3 VOSK latency adds ~0.5--1.0s to inter-result gaps, so 2.0s is more reliable on device. Don't exceed ~2.5--3.0s or unrelated utterances may merge.

### Sequential Extraction

Multiple commands in a single utterance are extracted left-to-right:

```
"cease fire launch missiles target hotel one"
  -> cease_fire + launch_weapon(weapon=missiles, target=hotel one)
```

Both `OnCommandRecognised` (per-command) and `OnCommandsRecognised` (batch array) events fire.

### Debounce

Per-intent debounce suppresses duplicate firings within `commandCooldown` seconds. This applies both across separate VOSK results and within a single parse batch from sequential extraction.

```csharp
commandRecogniser.commandCooldown = 0.5f; // Default: 0.5s
```

### Grammar Mode vs Free Speech

By default, `VoskCommandRecogniser` constrains VOSK's decoder to only the words in registered commands and slots. This dramatically improves accuracy for command-driven UX.

Setting `freeSpeechMode = true` disables the grammar constraint, allowing unconstrained vocabulary. Command matching becomes best-effort -- homophones and uncommon words are significantly less reliable. Use free speech only when you need arbitrary dictation.

---

## Inspector Authoring

For zero-code setup, create ScriptableObject assets instead of writing `Configure()` calls:

1. **Assets > Create > VOSK XR > Slot Definition** -- define slot values, aliases, and type in the Inspector.
2. **Assets > Create > VOSK XR > Command** -- define patterns as human-readable strings (e.g. `"launch {?quantity} {weapon} target {target}"`).
3. **Assets > Create > VOSK XR > Command Set** -- group command assets into named sets.
4. On the `VoskCommandRecogniser` component, assign:
   - **Slot Assets** -- drag in all slot assets
   - **Command Set Assets** -- drag in set assets
   - **Initial Active Set Names** -- which sets to activate on startup

`VoskCommandRecogniser.Awake()` converts the assets to runtime structs and calls `Configure()` + `SetActiveSets()` automatically.

If both Inspector assets and a code-based `Configure()` call are present, the code call takes priority (it overwrites the asset-driven configuration).

The Command Recognition sample includes a complete set of 20 ScriptableObject assets (6 slots, 11 commands, 3 sets) under `Samples~/CommandRecognition/AssetAuthoring/`.

---

## Editor Iteration

Three complementary approaches let you iterate without deploying to Quest.

### Command Debug Window

Open **Window > VOSK XR > Command Debug** during Play Mode to inspect the full command pipeline in real time.

**Left panel** (recognition state):
- Audio level meters -- pre-AGC RMS, post-AGC RMS, and current AGC gain.
- Partial result -- live VOSK partial transcript as you speak.
- Final result -- the completed transcript text.
- Per-word confidence bars -- each word with a colour-coded confidence bar (green > yellow > red). Shows `[n/a]` when VOSK omits per-word confidence (happens with `maxAlternatives > 0`).
- N-best alternatives -- alternative hypotheses with confidence scores.

**Right panel** (command matching):
- Active command sets -- which sets are currently loaded.
- Last match breakdown -- for each command definition attempted: intent, score, confidence, threshold pass/fail, and reject reason (if any). Accepted commands are highlighted in green.
- Slot details -- matched slot word positions (start/end indices) with per-slot confidence.
- Match history -- scrolling list of the last 20 match results with timestamps.

**Bottom toolbar:**
- **Inject field** -- type a phrase and press Enter (or click Send) to push it through the full command pipeline without a microphone. Useful for testing specific phrases or edge cases.
- **Clear** -- clears match history and resets the display.
- **Pause / Resume** -- freezes the display so you can inspect a result without it being overwritten by the next utterance. On resume, stale results are skipped so the display jumps to the next genuinely new result.

The debug window is Editor-only (`#if UNITY_EDITOR`) and has zero cost in builds. The underlying diagnostic structs (`VoskMatchDiagnostics`, `VoskMatchAttempt`, `VoskDiagnosticSlotMatch`) are compiled out of non-Editor builds.

### Live Microphone (Windows Editor)

On Windows, `VoskSpeechRecogniser.StartRecognition()` transparently auto-routes audio through `UnityEngine.Microphone` and a desktop build of `libvosk.dll` via P/Invoke. Existing scenes and user code work with zero changes -- speak into your PC microphone, watch commands fire in the Console.

**Setup:**

1. Download `vosk-win64-*.zip` from [alphacep/vosk-api releases](https://github.com/alphacep/vosk-api/releases).
2. Extract and place these four DLLs into the package's `Runtime/Plugins/x86_64/` folder:
   - `libvosk.dll`
   - `libgcc_s_seh-1.dll`
   - `libstdc++-6.dll`
   - `libwinpthread-1.dll`
3. The plugin importer meta files are pre-configured for Editor-only loading on Windows x86_64. No build settings changes needed.

The Editor backend uses C# ports of the native bridge's DSP (48 kHz -> 16 kHz FIR downsampler and AGC with soft saturation). Model loading is offloaded to a background thread to avoid main-thread hitches.

**Scope:** Editor-only. The live mic backend is excluded from Android, standalone Windows, Linux, and macOS builds via `#if UNITY_EDITOR_WIN` guards. Android runtime behaviour is unchanged.

### Text Injection API

For unit tests, CI, replay, and threshold tuning without audio hardware:

```csharp
// Inject through full command pipeline (parser -> threshold -> buffer -> debounce)
commandRecogniser.InjectText("launch all missiles target hotel one");
commandRecogniser.FlushPendingBuffer(); // Force immediate parse

// Inject with simulated confidence for threshold testing
var words = VoskSpeechRecogniser.CreateSimulatedWords("cease fire", confidence: 0.85f);
commandRecogniser.InjectText("cease fire", words);

// Inject raw recogniser events (bypasses command pipeline)
recogniser.InjectResult("hello world");
recogniser.InjectPartialResult("hel");
```

All injection methods are main-thread only. They fire the same events as real recognition, so existing handlers work unchanged.

See `Tests/Runtime/VoskCommandRecogniserInjectionTests.cs` and `VoskSpeechRecogniserInjectionTests.cs` for executable usage examples.

### Batch Test Runner

Regression-test command definitions after changing thresholds, aliases, or slot values. Feeds a list of test cases through the command parser, applies threshold filtering, and compares against expected intents and slots.

**Visual UI:** Window > VOSK XR > Batch Test Runner. Assign slot/command assets and a `VoskTestSuiteAsset`, then click Run All. Results appear in a table with per-row expansion for diagnostics. Export results as CSV for diffing across runs.

**Programmatic API (Edit Mode tests / CI):**

```csharp
using VoskXR.Commands;
using VoskXR.Testing;

var runner = new VoskBatchTestRunner(slots, commands, minScore: 0.6f, minConfidence: 0.4f);
var results = runner.RunAll(testCases);
Assert.IsTrue(results.AllPassed, results.FailureSummary);
```

`VoskBatchTestRunner` is pure C# — no MonoBehaviour dependency, works in Edit Mode without Play Mode or audio hardware. It instantiates a `VoskCommandParser` directly (the same path that `InjectText` uses internally).

**Test case authoring:**

Create a `VoskTestSuiteAsset` via Assets > Create > VOSK XR > Test Suite and author test cases in the Inspector. Or import/export as JSON for portability:

```json
{
    "cases": [
        {
            "input": "launch all missiles target hotel one",
            "expectedIntent": "launch_weapon",
            "expectedSlots": [{"name": "target", "value": "hotel one"}],
            "wordConfidence": -1,
            "description": "Full launch command with target"
        },
        {
            "input": "hello world",
            "expectedIntent": "",
            "description": "Out-of-grammar phrase should be rejected"
        },
        {
            "input": "cease fire",
            "expectedIntent": "",
            "wordConfidence": 0.3,
            "description": "Low confidence should be rejected by threshold"
        }
    ]
}
```

| Field | Description |
|-------|-------------|
| `input` | Text to feed through the command parser. |
| `expectedIntent` | Expected intent name. Empty/null = expect rejection (no match or below threshold). |
| `expectedSlots` | Array of `{name, value}` pairs. Omit to skip slot verification. |
| `wordConfidence` | Simulated uniform word confidence (0–1). Set to -1 to omit word data. |
| `description` | Human-readable description for the results table. |

**API Reference:**

| Method | Description |
|--------|-------------|
| `VoskBatchTestRunner(slots, commands, minScore, minConfidence)` | Constructor. All commands active. |
| `VoskBatchTestRunner(slots, sets, activeSetNames, minScore, minConfidence)` | Constructor with named command sets. |
| `RunAll(VoskTestCase[])` | Returns `VoskBatchResults` with per-case pass/fail. |
| `Run(VoskTestCase)` | Returns a single `VoskTestResult`. |
| `ToCsv(VoskBatchResults)` | Static. Exports results as a CSV string. |

| Property | Type | Description |
|----------|------|-------------|
| `VoskBatchResults.AllPassed` | `bool` | True when every test case passed. |
| `VoskBatchResults.FailureSummary` | `string` | Multi-line summary of all failures for NUnit assertion messages. |
| `VoskBatchResults.PassCount` | `int` | Number of passing test cases. |
| `VoskBatchResults.FailCount` | `int` | Number of failing test cases. |

---

## Push-to-Talk Pattern

```csharp
public class PushToTalk : MonoBehaviour
{
    [SerializeField] VoskSpeechRecogniser recogniser;
    [SerializeField] VoskCommandRecogniser commandRecogniser;

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
        // Flush any buffered speech immediately on release
        commandRecogniser.FlushPendingBuffer();
    }
}
```

Push-to-talk also eliminates false triggers from ambient noise and coughs in grammar mode, since recognition is only active during the button press.

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
        case VoskBridgeErrorCode.AudioDeviceUnavailable:
            // Mic hardware not found or in use
            break;
        default:
            Debug.LogError($"VOSK [{code}]: {message}");
            break;
    }
};
```

---

## Running Tests

The package includes 18 test suites (Edit Mode and Play Mode) that run without audio hardware or a VOSK model.

| Suite | Mode | Covers |
|-------|------|--------|
| `VoskCommandParserTests` | Play Mode | Pattern matching, slots, optional tokens, aliases, scoring, sliding start |
| `VoskCommandRecogniserInjectionTests` | Play Mode | Injection API, threshold filtering, debounce, buffer flushing, end-to-end wiring |
| `VoskSpeechRecogniserInjectionTests` | Play Mode | Speech-level injection, simulated words, event firing |
| `VoskSpeechRecogniserLifecycleTests` | Play Mode | Initialise/start/stop state transitions |
| `VoskNumberParserTests` | Play Mode | Digit sequence and cardinal number parsing |
| `VoskCommandSetTests` | Play Mode | Set switching, grammar rebuild, active set queries |
| `VoskAssetConversionTests` | Play Mode | ScriptableObject-to-runtime-struct conversions |
| `ParseWordsFromJsonTests` | Play Mode | VOSK JSON word/confidence/timing parsing |
| `ParseAlternativesFromJsonTests` | Play Mode | VOSK JSON n-best alternatives parsing |
| `DownsamplerTests` | Edit Mode | FIR downsampler output count, silence, DC gain, reset, phase continuity |
| `AgcTests` | Edit Mode | AGC convergence, silence, extreme input, reset |
| `ModelExtractorValidationTests` | Edit Mode | Model path validation and error handling |
| `VoskBridgeErrorCodeTests` | Edit Mode | Error code descriptions |
| `AudioMetricTests` | Edit Mode | `ComputeRms` (silence, DC, known-amplitude sine) |
| `VoskCommandParserDiagnosticTests` | Edit Mode | Parser diagnostic entries, matched pattern, slot positions, score |
| `VoskCommandRecogniserDiagnosticTests` | Edit Mode | End-to-end diagnostic struct population via `InjectText`, accept/reject reasons |
| `VoskMatchDiagnosticsTests` | Edit Mode | Diagnostic struct defaults, field storage, slot match data |
| `VoskBatchTestRunnerTests` | Edit Mode | Batch runner pass/fail reporting, threshold filtering, slot checks, CSV export |

To run in a consuming Unity project:

1. Add `"testables": ["com.jinwoo1601.vosk-xr"]` to your project's `Packages/manifest.json`.
2. Open **Window > General > Test Runner**.
3. Run Edit Mode and Play Mode tests.

---

## Building the Native Bridge

The prebuilt `libvosk-bridge.so` is included in the package. To build from source:

### Prerequisites
- Android NDK (bundled with Unity 6 or standalone r26+)
- CMake 3.18+
- Ninja build system
- `libvosk.so` for Android arm64 (from [VOSK releases](https://github.com/alphacep/vosk-api/releases))

### Steps

1. Place `libvosk.so` in `Plugins/Android/libs/arm64-v8a/`.
2. Build with CMake:

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

The native bridge source is in `NativeBridge~/` (excluded from Unity import by the `~` suffix). It contains:
- `vosk_bridge.cpp` -- main bridge with AudioRecord JNI capture, AGC, FIR downsampler, and VOSK recognition thread
- `audio_capture_audiorecord.cpp` -- Java AudioRecord JNI backend (active)
- `audio_capture_aaudio.cpp` -- AAudio backend (retained for reference, not compiled -- AAudio input is broken on Quest 3)

---

## Troubleshooting

### "Model archive not found in StreamingAssets"
Ensure the model `.zip` is at `Assets/StreamingAssets/<modelName>.zip` where `<modelName>` matches the `modelRelativePath` field on the `VoskSpeechRecogniser` component.

### "Microphone permission (RECORD_AUDIO) was not granted"
Add `RECORD_AUDIO` to your Android manifest or enable it in Player Settings > Android > Other Settings. The SDK requests the permission at runtime, but the manifest entry must be present.

### No transcription output on Quest
- Verify the model extracted successfully (check `OnModelReady` event or `IsModelReady` property).
- Check logcat: `adb logcat -s "vosk-bridge:*" "Unity:*"`
- Ensure `RECORD_AUDIO` permission is granted.
- Quest 3 microphone gain is low by default -- the AGC compensates, but verify `micGainTargetDb` is set (default `-18` dB).

### No transcription output in Editor
- Ensure the four `libvosk.dll` DLLs are in `Runtime/Plugins/x86_64/` (see [Live Microphone](#live-microphone-windows-editor) setup).
- Check the Console for VOSK model loading errors.
- Verify a microphone is connected and set as the default Windows input device.

### Commands not matching
- Verify patterns and slot values are lowercase (VOSK outputs lowercase).
- Check that grammar mode is active (`freeSpeechMode = false`).
- Lower `minScore` and `minConfidence` temporarily to see if matches are being filtered.
- Use `OnUnrecognisedSpeech` to log raw transcripts and compare against your patterns.

### "Native bridge library (libvosk-bridge) not found"
The native libraries are Android arm64 only. In the Editor, recognition routes through `EditorMicBackend` instead (Windows only). On macOS/Linux Editor, only text injection is available.

---

## Platform Support

| Platform | Status |
|----------|--------|
| Meta Quest 2/3/Pro (Android arm64) | Supported -- primary target, extensively tested |
| Other Android arm64 XR (Pico, Lynx) | Should work -- same native bridge, not yet device-tested |
| Windows Editor (x86_64) | Supported -- live mic + text injection for iteration |
| macOS / Linux Editor | Text injection only -- no live mic backend |
| Standalone Windows (PCVR) | Not yet supported -- architecturally ready, deferred to a future release |

---

## Known Limitations

See [KNOWN_LIMITATIONS.md](../KNOWN_LIMITATIONS.md) for the full list with repro steps, root causes, and workarounds. Key items:

- **Short homophones:** VOSK's small model may misrecognise "to" as "two", "all" as "fall". Prefer longer, phonetically distinct command words.
- **Single-character words:** "a" is unreliable in grammar mode. Use aliases (`"a" -> "one"`) instead.
- **Free-speech mode:** Significantly less accurate than grammar-constrained mode for commands. Homophones and uncommon words are the first to break.
- **Set switching audio gap:** `SetActiveSets()` causes a brief (~50 ms) audio gap during grammar rebuild. Pause ~500 ms before the next command.
- **Mid-command pauses:** Pauses exceeding `bufferWindow` split the command. Set `bufferWindow = 2.0` on Quest 3.
- **AAudio silence on Quest 3:** The native bridge uses Java `AudioRecord` via JNI because AAudio input delivers silence on Quest 3 firmware.
- **`vosk_recognizer_accept_waveform_f` broken on arm64:** The bridge converts float to int16 before feeding VOSK as a workaround.
- **Confidence `-1`:** Means "no word data available", not "zero confidence". Commands with `-1` confidence bypass the `minConfidence` threshold.
