# ScriptableObject Assets

Inspector-friendly assets for zero-code command authoring. Every type here has its own **Assets > Create > VoXR** entry, given below.

## VoxrSlotAsset

`public class VoxrSlotAsset : ScriptableObject` -- Namespace: `VoXR.Commands`

Create via **Assets > Create > VoXR > Slot Definition**.

### Fields

| Field | Type | Description |
|-------|------|-------------|
| `slotName` | `string` | Slot name used in pattern references |
| `slotType` | `VoxrSlotType` | `Enumerated` or `NumberSequence` |
| `values` | `string[]` | Allowed values (Enumerated only). Each should be lowercase, punctuation-free, and longer than one character; the parser logs a warning per violation when the grammar is built, in player builds as well as the Editor. See [Authoring warnings](../inspector-authoring.md#authoring-warnings). |
| `aliases` | `AliasEntry[]` | Variant-to-canonical mappings. A slot filled through an alias reports the **canonical** value, never the variant that was spoken. Variants are checked on the same three counts as `values` -- uppercase, punctuation, single character -- and an uppercase or punctuated variant never matches, because VOSK never produces one. See [Authoring warnings](../inspector-authoring.md#authoring-warnings). |
| `minWords` | `int` | Minimum digit words (NumberSequence, default 1). Must be at least 1 — `ToDefinition()` throws `ArgumentOutOfRangeException` otherwise, and the field is unclamped in the Inspector, so a 0 typed here surfaces as that exception from `Awake()`. |
| `maxWords` | `int` | Maximum digit words (NumberSequence, default 3). Must be at least `minWords`, on the same terms. Both are `0` on the runtime definition of an Enumerated slot. |

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

Create via **Assets > Create > VoXR > Command Definition**.

### Fields

| Field | Type | Description |
|-------|------|-------------|
| `intent` | `string` | The intent name |
| `patterns` | `string[]` | Pattern strings with space-separated tokens (e.g. `"launch {?quantity} {weapon} target {target}"`) |
| `allowPartialMatch` | `bool` | When enabled, the command enters pending state when matched with unfilled required slots, instead of being rejected. A winner that missed its own **first required element** is [barred](../scoring.md#the-leading-required-miss-bar) before this is consulted, so it enters no pending at any score. Follow-up speech can fill the missing slots, one per utterance if that is how they arrive. It is also the precondition for [both ways an incomplete command still fires](../command-recognition.md#the-two-ways-an-incomplete-command-still-fires) — a confirm phrase, or `FireAsIs` on timeout — so this command's handler must tolerate every required slot being absent. |
| `requiresConfirmation` | `bool` | When enabled, the command enters pending state even when fully matched, requiring explicit confirmation before firing. |

Each pattern string is split into a token array at runtime. Tokens take one of four forms: a literal word, `{slotName}` (required slot), `{?slotName}` (optional slot), or `?word` (optional literal — the word may be spoken or dropped without costing the match, at [two costs worth reading first](../command-recognition.md#never-leave-a-required-function-word-between-a-bare-pattern-and-its-slot)).

The `?` marking an optional slot goes **inside** the braces. `?{slotName}` is charged as a required literal whose text is `?{slotName}` — no utterance can produce that, so the pattern can never match, and nothing warns about the transposition.

The split separator is the **space character only**, not whitespace in general: a tab or newline inside a pattern string stays inside its token, yielding a token that matches nothing.

### Methods

| Method | Description |
|--------|-------------|
| `ToDefinition()` | Converts to runtime `VoxrCommandDefinition` struct. Splits each pattern string on spaces into a token array, discarding empty entries. |

## VoxrCommandSetAsset

`public class VoxrCommandSetAsset : ScriptableObject` -- Namespace: `VoXR.Commands`

Create via **Assets > Create > VoXR > Command Set**.

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
| `FromJson(string json)` | Replaces `cases` from a JSON string. Throws `ArgumentException` if `json` is null, empty, or whitespace. |

## VoxrTestCase

`[Serializable] public class VoxrTestCase` -- Namespace: `VoXR.Testing`

Serializable class representing a single test case.

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

The shipped package contains **no runner for this asset type** -- the Batch Test Runner window takes a `VoxrTestSuiteAsset` and nothing else. Its only consumer is that WAV-replay suite, which builds its cases in memory, so an asset of this type authored in a game project has nothing to execute it.

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
| `FromJson(string json)` | Replaces `cases` from a JSON string. Throws `ArgumentException` if `json` is null, empty, or whitespace. |

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
