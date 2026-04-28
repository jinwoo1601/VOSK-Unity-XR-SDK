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
| `GetSlot(string name)` | Returns the value of a named slot, or empty string if not matched. Logs a warning if the slot name was not registered. |
| `HasSlot(string name)` | Returns true if the named slot was matched in this command. |

## VoxrSlotMatch

`public readonly struct VoxrSlotMatch` -- Namespace: `VoXR.Commands`

A single slot extraction result.

| Field | Type | Description |
|-------|------|-------------|
| `Name` | `string` | Slot name (e.g. `"weapon"`) |
| `Value` | `string` | Matched value (e.g. `"missiles"`) |

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

## See Also

- [VoxrSpeechRecogniser](speech-recogniser.md) -- produces `VoxrResult` via `OnResult`
- [VoxrCommandRecogniser](command-recogniser.md) -- produces `VoxrCommand` via `OnCommandRecognised`
- [Command Definitions](command-definitions.md) -- defining patterns and slots
- [Command Recognition](../command-recognition.md) -- how matching works
