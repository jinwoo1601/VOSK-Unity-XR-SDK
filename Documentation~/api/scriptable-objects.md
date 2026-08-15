# ScriptableObject Assets

Inspector-friendly assets for zero-code command authoring. Create via **Assets > Create > VoXR**.

## VoxrSlotAsset

`public class VoxrSlotAsset : ScriptableObject` -- Namespace: `VoXR.Commands`

### Fields

| Field | Type | Description |
|-------|------|-------------|
| `slotName` | `string` | Slot name used in pattern references |
| `slotType` | `VoxrSlotType` | `Enumerated` or `NumberSequence` |
| `values` | `string[]` | Allowed values (Enumerated only) |
| `aliases` | `AliasEntry[]` | Variant-to-canonical mappings |
| `minWords` | `int` | Minimum digit words (NumberSequence, default 1) |
| `maxWords` | `int` | Maximum digit words (NumberSequence, default 3) |

### Methods

| Method | Description |
|--------|-------------|
| `ToDefinition()` | Converts to runtime `VoxrSlotDefinition` struct |

### AliasEntry

Nested serializable struct for variant-to-canonical mappings.

| Field | Type | Description |
|-------|------|-------------|
| `variant` | `string` | The variant word/phrase (e.g. `"jackals"`) |
| `canonical` | `string` | The canonical value it maps to (e.g. `"jackal"`) |

## VoxrCommandAsset

`public class VoxrCommandAsset : ScriptableObject` -- Namespace: `VoXR.Commands`

### Fields

| Field | Type | Description |
|-------|------|-------------|
| `intent` | `string` | The intent name |
| `patterns` | `string[]` | Pattern strings with space-separated tokens (e.g. `"launch {?quantity} {weapon} target {target}"`) |
| `allowPartialMatch` | `bool` | When enabled, the command enters pending state when matched with unfilled required slots, instead of being rejected. Follow-up speech can fill the missing slots, one per utterance if that is how they arrive. It is also the precondition for [both ways an incomplete command still fires](../command-recognition.md#the-two-ways-an-incomplete-command-still-fires) — a confirm phrase, or `FireAsIs` on timeout — so this command's handler must tolerate every required slot being absent. |
| `requiresConfirmation` | `bool` | When enabled, the command enters pending state even when fully matched, requiring explicit confirmation before firing. |

Each pattern string is split on whitespace into a token array at runtime. Tokens can be literal words, `{slotName}`, or `{?slotName}` (optional).

### Methods

| Method | Description |
|--------|-------------|
| `ToDefinition()` | Converts to runtime `VoxrCommandDefinition` struct. Splits pattern strings on whitespace into token arrays. |

## VoxrCommandSetAsset

`public class VoxrCommandSetAsset : ScriptableObject` -- Namespace: `VoXR.Commands`

### Fields

| Field | Type | Description |
|-------|------|-------------|
| `setName` | `string` | The command set name |
| `commands` | `VoxrCommandAsset[]` | Command assets in this set |

### Methods

| Method | Description |
|--------|-------------|
| `ToSet()` | Converts to runtime `VoxrCommandSet` struct. Skips null entries with a warning. |

## VoxrTestSuiteAsset

`public class VoxrTestSuiteAsset : ScriptableObject` -- Namespace: `VoXR.Testing`

Create via **Assets > Create > VoXR > Test Suite**.

A collection of test cases for regression-testing command definitions with the [Batch Test Runner](batch-test-runner.md).

### Fields

| Field | Type | Description |
|-------|------|-------------|
| `suiteName` | `string` | Human-readable name for this test suite |
| `cases` | `List<VoxrTestCase>` | Test cases to run |

### Methods

| Method | Description |
|--------|-------------|
| `ToArray()` | Returns `cases` as a `VoxrTestCase[]` for `VoxrBatchTestRunner.RunAll()`. |
| `ToJson()` | Serializes all cases to a JSON string for portability and version control. |
| `FromJson(string json)` | Replaces `cases` from a JSON string. |

## VoxrTestCase

`[Serializable] public class VoxrTestCase` -- Namespace: `VoXR.Testing`

Serializable struct representing a single test case.

| Field | Type | Description |
|-------|------|-------------|
| `input` | `string` | Text to feed through the command parser |
| `expectedIntent` | `string` | Expected intent name. Empty = expect rejection. |
| `expectedSlots` | `ExpectedSlot[]` | Array of `{name, value}` pairs. Omit to skip slot verification. |
| `wordConfidence` | `float` | Simulated uniform word confidence (0--1). `-1` = omit word data. |
| `description` | `string` | Human-readable description for the results table |

## VoxrAudioTestSuiteAsset

`public class VoxrAudioTestSuiteAsset : ScriptableObject` -- Namespace: `VoXR.Testing`

Create via **Assets > Create > VoXR > Audio Test Suite**.

A collection of audio test cases for acoustic regression testing: each case pairs a fixture WAV file with the command expected when that audio is replayed through the recognition pipeline. Consumed by the repository's WAV-replay PlayMode suite; the fixture corpus itself lives under the repository's `Tests~/` folder and is not part of the published package.

### Fields

| Field | Type | Description |
|-------|------|-------------|
| `suiteName` | `string` | Human-readable name for this audio test suite |
| `cases` | `List<VoxrAudioTestCase>` | Audio test cases to run |

### Methods

| Method | Description |
|--------|-------------|
| `ToArray()` | Returns `cases` as a `VoxrAudioTestCase[]`. |
| `ToJson()` | Serializes all cases to a JSON string. Carries the expectation fields only -- generation-side manifest fields (phrases, peaks, gaps) are not part of this type. |
| `FromJson(string json)` | Replaces `cases` from a JSON string. |

## VoxrAudioTestCase

`[Serializable] public class VoxrAudioTestCase` -- Namespace: `VoXR.Testing`

A single audio test case.

| Field | Type | Description |
|-------|------|-------------|
| `file` | `string` | WAV path relative to the fixture root (e.g. `audio/tts/cease_fire.wav`). 48 kHz mono 16-bit. |
| `category` | `string` | Fixture category (`clean`, `slot-variant`, `homophone`, `filler`, `split`, `silence`) |
| `expectedIntent` | `string` | Expected intent name. Empty = expect no recognized command (negative baseline). |
| `expectedSlots` | `ExpectedSlot[]` | Array of `{name, value}` pairs. Omit to skip slot verification. |
| `expectedTranscript` | `string` | Expected final transcript. Empty = skip the transcript assertion. |
| `description` | `string` | Human-readable description, echoed into test failure messages |

## See Also

- [Inspector Authoring](../inspector-authoring.md) -- workflow guide for zero-code setup
- [Command Definitions](command-definitions.md) -- runtime equivalents: `VoxrCommandDefinition`, `VoxrSlotDefinition`
- [Batch Test Runner](batch-test-runner.md) -- regression testing with `VoxrTestSuiteAsset`
- [Editor Testing](../editor-testing.md) -- visual test UI guide
