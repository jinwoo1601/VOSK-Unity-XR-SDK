# Data Types

Result and command data structures returned by the recognition pipeline.

## VoxrResult

`public readonly struct VoxrResult` -- Namespace: `VoXR`

The full recognition result with word-level data.

| Field | Type | Description |
|-------|------|-------------|
| `Text` | `string` | The full recognised text |
| `Words` | `VoxrWord[]` | Per-word confidence and timing for the best hypothesis (empty if unavailable) |

## VoxrWord

`public readonly struct VoxrWord` -- Namespace: `VoXR`

A single recognised word with metadata.

| Field | Type | Description |
|-------|------|-------------|
| `Text` | `string` | The recognised word |
| `Confidence` | `float` | Confidence score in range [0, 1] |
| `StartTime` | `float` | Start time in seconds from beginning of utterance |
| `EndTime` | `float` | End time in seconds from beginning of utterance |

## VoxrCommand

`public readonly struct VoxrCommand` -- Namespace: `VoXR.Commands`

A parsed command with intent and extracted slots.

### Fields

| Field | Type | Description |
|-------|------|-------------|
| `Intent` | `string` | The matched command intent (e.g. `"launch_weapon"`) |
| `Slots` | `VoxrSlotMatch[]` | Matched slot name/value pairs |
| `Confidence` | `float` | Minimum word confidence across matched tokens. `-1` means no word data was available. |
| `Score` | `float` | Pattern match quality (0.0--1.0). Higher is better. |
| `RawText` | `string` | The original VOSK transcript text |
| `MatchedPatternIndex` | `int` | Index into the definition's `Patterns` array identifying which pattern produced this match. `-1` when unavailable. |

### Methods

| Method | Description |
|--------|-------------|
| `GetSlot(string name)` | Returns the value of a named slot, or empty string if not matched. Logs a warning if the slot name was not registered. For a `NumberSequence` slot the value is the number words as spoken — `"two seven zero"`, not `"270"` (see below); for an `Enumerated` slot it is the canonical value, with any spoken alias already resolved. |
| `HasSlot(string name)` | Returns true if the named slot was matched in this command. |

> **NumberSequence slots return spoken words, not digits.** `int.TryParse` on the returned value fails on every utterance and yields `0` without throwing. Convert with [`VoxrNumberParser`](number-parser.md) — `ParseDigitSequence` for digit-by-digit values, `ParseCardinal` for cardinal phrases. The canonical fallback snippet is in [Command Recognition → NumberSequence Slots](../command-recognition.md#numbersequence-slots).

## VoxrSlotMatch

`public readonly struct VoxrSlotMatch` -- Namespace: `VoXR.Commands`

A single slot extraction result.

| Field | Type | Description |
|-------|------|-------------|
| `Name` | `string` | Slot name (e.g. `"weapon"`) |
| `Value` | `string` | Matched value. For an `Enumerated` slot this is the canonical value (e.g. `"missiles"`), with any spoken alias already resolved (`"jackals"` → `"jackal"`). For a `NumberSequence` slot it is the number words as spoken (`"two seven zero"`), not a numeric string — convert with [`VoxrNumberParser`](number-parser.md). |

## VoxrCommandResult

`public readonly struct VoxrCommandResult` -- Namespace: `VoXR.Commands`

Parser output wrapping match/no-match.

| Field | Type | Description |
|-------|------|-------------|
| `IsMatch` | `bool` | True if a command pattern matched |
| `Command` | `VoxrCommand` | The parsed command (only valid when `IsMatch` is true) |
| `RawText` | `string` | The original VOSK transcript |

## VoxrPendingTimeoutBehavior

`public enum VoxrPendingTimeoutBehavior` -- Namespace: `VoXR.Commands`

Determines what happens when a pending command's timeout expires.

| Value | Description |
|-------|-------------|
| `Cancel` | The pending command is cancelled and discarded. `OnCommandCancelled` fires. |
| `FireAsIs` | The pending command fires as-is with whatever slots were filled. `OnCommandConfirmed` and `OnCommandRecognised` fire. |

`FireAsIs` is one of the two [deliberate exceptions](../command-recognition.md#the-two-ways-an-incomplete-command-still-fires) to the rule that a command missing a required argument does not fire. Handlers for a command whose recogniser uses it must tolerate every required slot being absent.

## See Also

- [VoxrSpeechRecogniser](speech-recogniser.md) -- produces `VoxrResult` via `OnResult`
- [VoxrCommandRecogniser](command-recogniser.md) -- produces `VoxrCommand` via `OnCommandRecognised`
- [Command Definitions](command-definitions.md) -- defining patterns and slots
- [Number Parser](number-parser.md) -- converting `NumberSequence` slot values returned by `GetSlot`
- [Command Recognition](../command-recognition.md) -- how matching works
