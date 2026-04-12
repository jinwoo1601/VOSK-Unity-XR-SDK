# Data Types

Result and command data structures returned by the recognition pipeline.

## VoskResult

`public readonly struct VoskResult` -- Namespace: `VoskXR`

The full recognition result with word-level data.

| Field | Type | Description |
|-------|------|-------------|
| `Text` | `string` | The full recognised text |
| `Words` | `VoskWord[]` | Per-word confidence and timing for the best hypothesis (empty if unavailable) |
| `Alternatives` | `VoskAlternative[]` | N-best alternative hypotheses ranked best-first (empty when `maxAlternatives` is 0) |

## VoskWord

`public readonly struct VoskWord` -- Namespace: `VoskXR`

A single recognised word with metadata.

| Field | Type | Description |
|-------|------|-------------|
| `Text` | `string` | The recognised word |
| `Confidence` | `float` | Confidence score in range [0, 1] |
| `StartTime` | `float` | Start time in seconds from beginning of utterance |
| `EndTime` | `float` | End time in seconds from beginning of utterance |

## VoskAlternative

`public readonly struct VoskAlternative` -- Namespace: `VoskXR`

An alternative recognition hypothesis.

| Field | Type | Description |
|-------|------|-------------|
| `Text` | `string` | The recognised text for this hypothesis |
| `Confidence` | `float` | Acoustic model score (higher = better match) |
| `Words` | `VoskWord[]` | Per-word data for this hypothesis (may be empty) |

## VoskCommand

`public readonly struct VoskCommand` -- Namespace: `VoskXR.Commands`

A parsed command with intent and extracted slots.

### Fields

| Field | Type | Description |
|-------|------|-------------|
| `Intent` | `string` | The matched command intent (e.g. `"launch_weapon"`) |
| `Slots` | `VoskSlotMatch[]` | Matched slot name/value pairs |
| `Confidence` | `float` | Minimum word confidence across matched tokens. `-1` means no word data was available. |
| `Score` | `float` | Pattern match quality (0.0--1.0). Higher is better. |
| `RawText` | `string` | The original VOSK transcript text |

### Methods

| Method | Description |
|--------|-------------|
| `GetSlot(string name)` | Returns the value of a named slot, or empty string if not matched. Logs a warning if the slot name was not registered. |
| `HasSlot(string name)` | Returns true if the named slot was matched in this command. |

## VoskSlotMatch

`public readonly struct VoskSlotMatch` -- Namespace: `VoskXR.Commands`

A single slot extraction result.

| Field | Type | Description |
|-------|------|-------------|
| `Name` | `string` | Slot name (e.g. `"weapon"`) |
| `Value` | `string` | Matched value (e.g. `"missiles"`) |

## VoskCommandResult

`public readonly struct VoskCommandResult` -- Namespace: `VoskXR.Commands`

Parser output wrapping match/no-match.

| Field | Type | Description |
|-------|------|-------------|
| `IsMatch` | `bool` | True if a command pattern matched |
| `Command` | `VoskCommand` | The parsed command (only valid when `IsMatch` is true) |
| `RawText` | `string` | The original VOSK transcript |

## See Also

- [VoskSpeechRecogniser](speech-recogniser.md) -- produces `VoskResult` via `OnResult`
- [VoskCommandRecogniser](command-recogniser.md) -- produces `VoskCommand` via `OnCommandRecognised`
- [Command Definitions](command-definitions.md) -- defining patterns and slots
- [Command Recognition](../command-recognition.md) -- how matching works
