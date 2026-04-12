# VoskCommandRecogniser

`public class VoskCommandRecogniser : MonoBehaviour` -- Namespace: `VoskXR.Commands`

Subscribes to speech events and runs text through the command parser pipeline: pattern matching, confidence/score thresholds, utterance buffering, sequential extraction, and debounce.

## Inspector Fields

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `speechRecogniser` | `VoskSpeechRecogniser` | -- | Reference to the speech recogniser component |
| `minConfidence` | `float` | `0.4` | Minimum per-word confidence to accept a command. Commands with confidence below this are rejected. `-1` (no data) bypasses this check. |
| `minScore` | `float` | `0.6` | Minimum pattern match score (0.0--1.0) to accept a command |
| `bufferWindow` | `float` | `1.5` | Seconds to buffer consecutive VOSK results before parsing. Merges speech split by mid-command pauses. Recommended: `2.0` on Quest 3. |
| `commandCooldown` | `float` | `0.3` | Per-intent debounce window in seconds. Suppresses duplicate firings of the same intent within this period. |
| `freeSpeechMode` | `bool` | `false` | When true, disables grammar constraint for unconstrained vocabulary with best-effort command matching |
| `slotAssets` | `VoskSlotAsset[]` | -- | Slot definitions for Inspector authoring |
| `commandSetAssets` | `VoskCommandSetAsset[]` | -- | Command set definitions for Inspector authoring |
| `initialActiveSetNames` | `string[]` | -- | Which sets to activate on startup when using Inspector authoring |

## Properties

| Property | Type | Description |
|----------|------|-------------|
| `ActiveSetNames` | `string[]` | Names of currently active command sets (returns a snapshot copy) |

## Events

| Event | Signature | Description |
|-------|-----------|-------------|
| `OnCommandRecognised` | `Action<VoskCommand>` | Fired for each successfully recognised command that passes threshold and debounce filters |
| `OnCommandsRecognised` | `Action<VoskCommand[]>` | Fired with the full batch of commands extracted from a single utterance (after sequential extraction) |
| `OnUnrecognisedSpeech` | `Action<string>` | Fired when speech does not match any command pattern |

## Methods

| Method | Signature | Description |
|--------|-----------|-------------|
| `Configure` | `(VoskSlotDefinition[] slots, VoskCommandDefinition[] commands)` | Builds parser from slot and command definitions. Applies grammar constraint immediately if recognition is running. Use for simple setups without command sets. |
| `Configure` | `(VoskSlotDefinition[] slots, VoskCommandSet[] sets)` | Registers shared slots and named command sets. Does not activate any set -- call `SetActiveSets()` after. |
| `SetActiveSets` | `(params string[] setNames)` | Activates one or more named sets, rebuilding the parser and grammar from only those sets' commands. Handles stop/set/start if recognition is running. |
| `SetActiveSet` | `(string setName)` | Convenience wrapper for activating a single set. |
| `InjectText` | `(string text, VoskWord[] words = null)` | Injects text into the full command pipeline (parser -> threshold -> buffer -> debounce) as if it arrived from VOSK. Main-thread only. |
| `FlushPendingBuffer` | `()` | Immediately flushes any speech held in the utterance buffer, forcing parse. Useful for push-to-talk release, scene transitions, and synchronous test injection. |

## See Also

- [Command Recognition](../command-recognition.md) -- patterns, slots, and matching concepts
- [Command Sets](../command-sets.md) -- mode-specific grammar switching
- [Inspector Authoring](../inspector-authoring.md) -- zero-code setup via ScriptableObjects
- [Push-to-Talk](../push-to-talk.md) -- buffer flush on release
- [VoskSpeechRecogniser](speech-recogniser.md) -- the underlying speech engine
- [Command Definitions](command-definitions.md) -- `VoskCommandDefinition`, `VoskSlotDefinition`, `VoskCommandSet`
- [Data Types](data-types.md) -- `VoskCommand`, `VoskSlotMatch`
