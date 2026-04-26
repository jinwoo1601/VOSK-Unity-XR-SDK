# VoxrSpeechRecogniser

`public class VoxrSpeechRecogniser : MonoBehaviour` -- Namespace: `VoXR`

The core speech recognition MonoBehaviour. Attach to a GameObject, configure via Inspector, subscribe to events.

## Inspector Fields

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `modelRelativePath` | `string` | `"vosk-model-small-en-us-0.15"` | Path within StreamingAssets (without `.zip` extension) |
| `sampleRate` | `float` | `16000` | VOSK recogniser sample rate in Hz |
| `micGainTargetDb` | `float` | `-18` | AGC target level in dB (calibrated for Quest 3) |
| `maxAlternatives` | `int` | `0` | Number of n-best alternative hypotheses to return (0 = disabled) |

## Events

| Event | Signature | Description |
|-------|-----------|-------------|
| `OnPartialResult` | `Action<string>` | Fired on the main thread with partial transcript text as speech is being recognised |
| `OnFinalResult` | `Action<string>` | Fired on the main thread with final transcript text at utterance boundaries |
| `OnResult` | `Action<VoxrResult>` | Fired with final result including per-word confidence, timing, and n-best alternatives |
| `OnError` | `Action<VoxrBridgeErrorCode, string>` | Fired on the main thread with error code and human-readable description |
| `OnModelReady` | `Action` | Fired when model extraction and initialisation completes |

## Properties

| Property | Type | Description |
|----------|------|-------------|
| `IsInitialised` | `bool` | True after `Initialise()` succeeds, false after `ReleaseNativeResources()` |
| `IsRecognising` | `bool` | True between `StartRecognition()` and `StopRecognition()` |
| `IsModelReady` | `bool` | True once model extraction and validation completes |

## Methods

| Method | Description |
|--------|-------------|
| `Initialise()` | Extracts model (if needed) and initialises the native bridge. No-op if already initialised. Fire-and-forget async wrapper. |
| `InitialiseAsync()` | `async Task`. Asynchronously initialises the native bridge with model loading. |
| `ReleaseNativeResources()` | Destroys the native bridge and frees all resources. Safe to call multiple times. |
| `StartRecognition()` | Starts audio capture and recognition. Calls `Initialise()` if needed. Fire-and-forget async wrapper. |
| `StartRecognitionAsync()` | `async Task`. Asynchronously starts recognition with permission handling. |
| `StopRecognition()` | Stops audio capture. Model stays loaded for fast restart. |
| `ResetRecogniser()` | Clears recogniser state without stopping audio. |
| `SetGrammar(string grammarJson)` | Sets a VOSK grammar JSON string for constrained recognition. Typically called by `VoxrCommandRecogniser` internally. |

### Injection Methods

| Method | Description |
|--------|-------------|
| `InjectResult(string text, VoxrWord[] words, VoxrAlternative[] alternatives)` | Fires `OnFinalResult` and `OnResult` as if VOSK recognised the text. Bypasses native bridge state -- use for Editor testing, replay, and CI. All parameters except `text` are optional. |
| `InjectPartialResult(string text)` | Fires `OnPartialResult` as if VOSK produced the partial text. |
| `CreateSimulatedWords(string text, float confidence)` | **Static.** Generates `VoxrWord[]` from text with uniform confidence and sequential timing. Useful for threshold testing via injection. Default confidence is `1.0f`. |

## Usage

For full setup and lifecycle examples, see the [Getting Started](../getting-started.md) guide.

## See Also

- [Getting Started](../getting-started.md) -- setup walkthrough and first recognition
- [Push-to-Talk](../push-to-talk.md) -- start/stop lifecycle pattern
- [Editor Testing](../editor-testing.md) -- injection workflows
- [VoxrCommandRecogniser](command-recogniser.md) -- command parsing layer
- [Data Types](data-types.md) -- `VoxrResult`, `VoxrWord`, `VoxrAlternative`
- [Error Codes](error-codes.md) -- `VoxrBridgeErrorCode` values
