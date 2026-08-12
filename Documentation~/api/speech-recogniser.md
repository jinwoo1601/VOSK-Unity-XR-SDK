# VoxrSpeechRecogniser

`public class VoxrSpeechRecogniser : MonoBehaviour` -- Namespace: `VoXR`

The core speech recognition MonoBehaviour. Attach to a GameObject, configure via Inspector, subscribe to events.

## One recogniser per process

**Only one `VoxrSpeechRecogniser` may be initialised at a time.** On device the recogniser
is file-scope native state with no per-instance handle in its ABI, so the process has
exactly one bridge and one model no matter how many components reference it.

Ownership is claimed by whichever component initialises it and released when that component
calls `ReleaseNativeResources()` or is destroyed. A second component may exist in the
scene, but until the owner lets go it is **inert**:

- `IsInitialised` and `IsRecognising` report `false` — it initialised nothing, whatever the
  process-wide bridge is doing.
- `InitialiseAsync()`, `SetGrammar()`, and `ResetRecogniser()` reject the call, logging an
  error and firing `OnError` with `AlreadyInitialised` and the owner's GameObject name.
  `Initialise()` and `StartRecognition()` route through `InitialiseAsync()`, so they inherit
  that rejection — under push-to-talk wiring it is reported once per press.
- `StopRecognition()` is a quiet no-op, and `ReleaseNativeResources()` frees only that
  component's own resources, so its `OnDestroy` cannot free the owner's recognizer.

A single recogniser is unaffected — this only engages once a second one exists.

> **Editor note.** The Windows Editor backend (`EditorMicBackend`) is genuinely
> per-instance: it loads its own VOSK model and never touches the native bridge, so two
> recognisers *could* coexist there. The rule is nevertheless enforced uniformly, so that a
> scene which works in the Editor cannot fail on device. Treat the constraint as a property
> of the package, not of the platform you happen to be running on.

**Handing the bridge over:** call `ReleaseNativeResources()` on the outgoing recogniser
before initialising the incoming one. That frees the claim **synchronously**, so the
incoming recogniser can initialise in the same frame. `Object.Destroy()` also frees it —
via `OnDestroy` — but Unity defers destruction to the end of the frame, so a
`Destroy(outgoing); incoming.Initialise();` pair in one frame is rejected. The same applies
to `UnloadSceneAsync`, which completes over several frames while an incoming scene's
`Start()` may already be calling `Initialise()`. Under the default push-to-talk wiring the
next press retries and succeeds, at the cost of the pre-warm; in `Continuous` listening
mode there is no such retry, so prefer the explicit `ReleaseNativeResources()` handover.

## Inspector Fields

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `modelRelativePath` | `string` | `"vosk-model-small-en-us-0.15"` | Path within StreamingAssets (without `.zip` extension) |
| `sampleRate` | `float` | `16000` | VOSK recogniser sample rate in Hz |
| `micGainTargetDb` | `float` | `-18` | AGC target level in dB (calibrated for Quest 3) |

## Events

| Event | Signature | Description |
|-------|-----------|-------------|
| `OnPartialResult` | `Action<string>` | Fired on the main thread with partial transcript text as speech is being recognised |
| `OnFinalResult` | `Action<string>` | Fired on the main thread with final transcript text at utterance boundaries |
| `OnResult` | `Action<VoxrResult>` | Fired with final result including per-word confidence and timing |
| `OnError` | `Action<VoxrBridgeErrorCode, string>` | Fired on the main thread with error code and human-readable description |
| `OnModelReady` | `Action` | Fired when model extraction and initialisation completes |

## Properties

| Property | Type | Description |
|----------|------|-------------|
| `IsInitialised` | `bool` | True after `Initialise()` succeeds, false after `ReleaseNativeResources()`. Always false while another component owns the bridge |
| `IsRecognising` | `bool` | True between `StartRecognition()` and `StopRecognition()`. Always false while another component owns the bridge |
| `IsModelReady` | `bool` | True once model extraction and validation completes |

## Methods

| Method | Description |
|--------|-------------|
| `Initialise()` | Extracts model (if needed) and initialises the native bridge. No-op if already initialised. Fire-and-forget async wrapper. |
| `InitialiseAsync()` | `async Task`. Asynchronously initialises the native bridge with model loading. Rejected if another component already owns the bridge. |
| `ReleaseNativeResources()` | Destroys the native bridge, frees all resources, and releases the bridge claim. Safe to call multiple times. No-op on a component that does not own the bridge. |
| `StartRecognition()` | Starts audio capture and recognition. Calls `Initialise()` if needed. Fire-and-forget async wrapper. |
| `StartRecognitionAsync()` | `async Task`. Asynchronously starts recognition with permission handling. |
| `StopRecognition()` | Stops audio capture. Model stays loaded for fast restart. |
| `ResetRecogniser()` | Clears recogniser state without stopping audio. |
| `SetGrammar(string grammarJson)` | Sets a VOSK grammar JSON string for constrained recognition. Typically called by `VoxrCommandRecogniser` internally. |

### Injection Methods

| Method | Description |
|--------|-------------|
| `InjectResult(string text, VoxrWord[] words)` | Fires `OnFinalResult` and `OnResult` as if VOSK recognised the text. Bypasses native bridge state -- use for Editor testing, replay, and CI. `words` is optional. |
| `InjectPartialResult(string text)` | Fires `OnPartialResult` as if VOSK produced the partial text. |
| `CreateSimulatedWords(string text, float confidence)` | **Static.** Generates `VoxrWord[]` from text with uniform confidence and sequential timing. Useful for threshold testing via injection. Default confidence is `1.0f`. |

## Usage

For full setup and lifecycle examples, see the [Getting Started](../getting-started.md) guide.

## See Also

- [Getting Started](../getting-started.md) -- setup walkthrough and first recognition
- [Push-to-Talk](../push-to-talk.md) -- start/stop lifecycle pattern
- [Editor Testing](../editor-testing.md) -- injection workflows
- [VoxrCommandRecogniser](command-recogniser.md) -- command parsing layer
- [Data Types](data-types.md) -- `VoxrResult`, `VoxrWord`
- [Error Codes](error-codes.md) -- `VoxrBridgeErrorCode` values
