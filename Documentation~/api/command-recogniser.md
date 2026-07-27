# VoxrCommandRecogniser

`public class VoxrCommandRecogniser : MonoBehaviour` -- Namespace: `VoXR.Commands`

Subscribes to speech events and runs text through the command parser pipeline: pattern matching, confidence/score thresholds, utterance buffering, sequential extraction, and debounce.

## Inspector Fields

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `speechRecogniser` | `VoxrSpeechRecogniser` | -- | Reference to the speech recogniser component |
| `minConfidence` | `float` | `0.4` | Minimum per-word confidence to accept a command. Commands with confidence below this are rejected. `-1` (no data) bypasses this check. |
| `minScore` | `float` | `0.6` | Minimum pattern match score (0.0--1.0) to accept a command |
| `skippedWordPenalty` | `float` | `1.0` | How much each in-grammar word the sliding start skips before a match counts against the score. At `1.0` the score becomes the fraction of the utterance the pattern covers. `0` disables it. See [Skipped-word penalty](../command-recognition.md#skipped-word-penalty). |
| `bufferWindow` | `float` | `0.5` | Seconds to buffer consecutive VOSK results before parsing. Merges speech split by mid-command pauses. `0.5` matches typical PC latency; raise it on Quest 3 (see [troubleshooting](../troubleshooting.md#commands-split-across-two-results)). |
| `eagerFlushOnCompleteMatch` | `bool` | `false` | Fire a complete command immediately when the buffered speech can't be extended or completed by more words, instead of waiting out `bufferWindow`. Commands that are a prefix of a longer one, or whose trailing slot could still grow, keep waiting. See [Eager flush](../command-recognition.md#eager-flush-low-latency-complete-commands). |
| `commandCooldown` | `float` | `0.3` | Per-intent debounce window in seconds. Suppresses duplicate firings of the same intent within this period. |
| `freeSpeechMode` | `bool` | `false` | When true, disables grammar constraint for unconstrained vocabulary with best-effort command matching |
| `slotAssets` | `VoxrSlotAsset[]` | -- | Slot definitions for Inspector authoring |
| `commandSetAssets` | `VoxrCommandSetAsset[]` | -- | Command set definitions for Inspector authoring |
| `initialActiveSetNames` | `string[]` | -- | Which sets to activate on startup when using Inspector authoring |
| `pendingTimeout` | `float` | `5.0` | Maximum seconds a pending command waits for follow-up speech before timing out |
| `pendingTimeoutBehavior` | `VoxrPendingTimeoutBehavior` | `Cancel` | What happens on timeout: `Cancel` discards the command, `FireAsIs` fires it with whatever slots were filled |
| `confirmVocabulary` | `string[]` | -- | Phrases that confirm a pending command. Empty = defaults ("confirm", "affirmative", "yes", "go ahead", "do it"). |
| `cancelVocabulary` | `string[]` | -- | Phrases that cancel a pending command. Empty = defaults ("cancel", "abort", "negative", "belay that", "never mind"). |

## Properties

| Property | Type | Description |
|----------|------|-------------|
| `ActiveSetNames` | `string[]` | Names of currently active command sets (returns a snapshot copy) |
| `HasPendingCommand` | `bool` | True if a command is currently in pending state (partial match or awaiting confirmation) |
| `PendingCommand` | `VoxrCommand?` | The currently pending command, or null if none |

## Events

| Event | Signature | Description |
|-------|-----------|-------------|
| `OnCommandRecognised` | `Action<VoxrCommand>` | Fired for each successfully recognised command that passes threshold and debounce filters |
| `OnCommandsRecognised` | `Action<VoxrCommand[]>` | Fired with the full batch of commands extracted from a single utterance (after sequential extraction) |
| `OnUnrecognisedSpeech` | `Action<string>` | Fired when speech does not produce any accepted command -- either no pattern matched, or all matches were rejected by score, confidence, or debounce thresholds. The `string` parameter is the full buffered transcript. See [Unrecognised Speech](../command-recognition.md#unrecognised-speech). |
| `OnCommandPending` | `Action<VoxrCommand>` | Fired when a command enters pending state (partial match with unfilled required slots, or awaiting explicit confirmation). See [Pending Commands](../command-recognition.md#pending-commands). |
| `OnCommandConfirmed` | `Action<VoxrCommand>` | Fired when a pending command is confirmed by follow-up speech or explicit confirmation. Also fires `OnCommandRecognised` and `OnCommandsRecognised`. |
| `OnCommandCancelled` | `Action<VoxrCommand>` | Fired when a pending command is cancelled by timeout, explicit cancel vocabulary, or preemption by a new complete command. |

## Methods

| Method | Signature | Description |
|--------|-----------|-------------|
| `Configure` | `(VoxrSlotDefinition[] slots, VoxrCommandDefinition[] commands)` | Builds parser from slot and command definitions. Applies grammar constraint immediately if recognition is running. Use for simple setups without command sets. |
| `Configure` | `(VoxrSlotDefinition[] slots, VoxrCommandSet[] sets)` | Registers shared slots and named command sets. Does not activate any set -- call `SetActiveSets()` after. |
| `SetActiveSets` | `(params string[] setNames)` | Activates one or more named sets, rebuilding the parser and grammar from only those sets' commands. Handles stop/set/start if recognition is running. |
| `SetActiveSet` | `(string setName)` | Convenience wrapper for activating a single set. |
| `InjectText` | `(string text, VoxrWord[] words = null)` | Injects text into the full command pipeline (parser -> threshold -> buffer -> debounce) as if it arrived from VOSK. Main-thread only. |
| `FlushPendingBuffer` | `()` | Immediately flushes any speech held in the utterance buffer, forcing parse. Useful for push-to-talk release, scene transitions, and synchronous test injection. |
| `RegisterSlotValueProvider` | `(string slotName, Func<string[]> valueProvider)` | Registers a function that controls which values of the named slot the parser accepts. Call `NotifySlotChanged()` after the provider's return set changes. The grammar is unaffected -- it always reflects the full universe of slot values. |
| `UnregisterSlotValueProvider` | `(string slotName)` | Removes a value provider. The slot reverts to its full value set on the next parser rebuild. Returns `true` if a provider was removed. |
| `NotifySlotChanged` | `()` | Rebuilds the parser to reflect current value-provider results. Does not touch the grammar or VOSK recogniser. No-op if `Configure` has not been called. Performs a full parser rebuild -- call only when values have actually changed. |
| `RebuildParser` | `()` | Rebuilds only the parser from current effective slots and active commands. Grammar and VOSK recogniser are untouched. Throws if `Configure` has not been called. |
| `RebuildGrammar` | `()` | Rebuilds and re-applies the VOSK grammar from the full universe of slot values. Performs stop/set grammar/start when recognition is running. Clears the utterance buffer. Defers if a command is pending. Throws if `Configure` has not been called. |
| `CancelPendingCommand` | `()` | Cancels the currently pending command, firing `OnCommandCancelled`. No-op when no command is pending. |

## See Also

- [Command Recognition](../command-recognition.md) -- patterns, slots, matching concepts, dynamic slot filtering, and pending commands
- [Command Sets](../command-sets.md) -- mode-specific grammar switching
- [Inspector Authoring](../inspector-authoring.md) -- zero-code setup via ScriptableObjects
- [Push-to-Talk](../push-to-talk.md) -- buffer flush on release
- [VoxrSpeechRecogniser](speech-recogniser.md) -- the underlying speech engine
- [Command Definitions](command-definitions.md) -- `VoxrCommandDefinition`, `VoxrSlotDefinition`, `VoxrCommandSet`
- [Data Types](data-types.md) -- `VoxrCommand`, `VoxrSlotMatch`
