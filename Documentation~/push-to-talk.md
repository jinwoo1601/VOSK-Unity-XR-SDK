# Push-to-Talk

`VoxrPushToTalkController` is a drop-in MonoBehaviour that gates `VoxrSpeechRecogniser` behind a talk button and optionally flushes buffered commands on release. It also exposes a runtime-switchable listening mode so you can offer both hold-to-talk and always-on listening in the same build.

## Overview

Use push-to-talk when false triggers from ambient noise, coughs, or background conversation are unacceptable — that is, for most serious XR applications. In grammar mode, VOSK has no "silence" output and must map every detected audio frame to an in-vocabulary word, so push-to-talk is the cleanest way to suppress spurious matches.

`VoxrPushToTalkController` wraps the recommended pattern (`Initialise` on scene load, `Start/Stop` on press/release, `FlushPendingBuffer` on release) and adds:

- A best-effort guard for the Android mic-permission race (the dialog can resolve *after* the user released).
- Runtime switching between `PushToTalk` and `Continuous` modes via the `ListeningMode` property.
- `UnityEvent` hooks for wiring a recording indicator in the Inspector.
- Correct lifecycle handling across disable/enable and `OnApplicationPause` so Continuous mode survives the Quest home overlay.

## Quick Setup

1. Add `VoxrSpeechRecogniser` and `VoxrPushToTalkController` to a GameObject in your scene. If you are using command parsing, add `VoxrCommandRecogniser` to the same GameObject.
2. On the controller, assign the `Speech Recogniser` reference, and optionally the `Command Recogniser`.
3. Wire your input to the controller's public methods:
   - On press: `VoxrPushToTalkController.PressTalk()`
   - On release: `VoxrPushToTalkController.ReleaseTalk()`
   - A UI `Button.onClick` fires on release only. For true press/release, use an `EventTrigger` component (`PointerDown` → `PressTalk`, `PointerUp` → `ReleaseTalk`), an XRI `InteractableUnityEventsWrapper`, or an `InputAction` with `started`/`canceled` callbacks.
4. (Optional) Wire `On Talk Started` and `On Talk Ended` in the controller's Inspector to your recording indicator (e.g. toggle an `Image.color`).

The controller pre-warms the model on `Start` via `VoxrSpeechRecogniser.Initialise()`. Disable `Initialise On Start` if your code calls `Initialise()` elsewhere.

## Listening Modes

```csharp
public enum VoxrListeningMode { Continuous, PushToTalk }
```

- **`PushToTalk`** (default) — recognition only runs between `PressTalk()` and `ReleaseTalk()`.
- **`Continuous`** — recognition runs whenever the controller is enabled; `PressTalk()` and `ReleaseTalk()` become no-ops.

Switch at runtime by assigning the property:

```csharp
controller.ListeningMode = VoxrListeningMode.Continuous;
```

Setter semantics:

| From → To                      | Behaviour                                                                                           |
|--------------------------------|-----------------------------------------------------------------------------------------------------|
| `PushToTalk` → `Continuous`    | Starts recognition if not already running; fires `OnTalkStarted` (unless a press was already held). |
| `Continuous` → `PushToTalk`    | Stops recognition; fires `OnTalkEnded`.                                                             |
| Same → same                    | No-op.                                                                                              |

## Inspector Reference

| Field                         | Purpose                                                                                 |
|-------------------------------|-----------------------------------------------------------------------------------------|
| `Speech Recogniser`           | The `VoxrSpeechRecogniser` the controller drives. Required.                              |
| `Command Recogniser`          | Optional. When assigned, `ReleaseTalk` calls `FlushPendingBuffer` so trailing speech parses immediately rather than waiting for the buffer window. |
| `Listening Mode`              | Initial mode (`PushToTalk` or `Continuous`). Can be changed at runtime via the property. |
| `Initialise On Start`         | When enabled, calls `VoxrSpeechRecogniser.Initialise()` in `Start` so the model is pre-warmed before the first press. |
| `Cancel Pending On Release`   | When enabled, `ReleaseTalk` also cancels any pending command on the command recogniser (see below). |
| `On Talk Started`             | `UnityEvent` fired when recognition begins (first press, or switch to Continuous).      |
| `On Talk Ended`               | `UnityEvent` fired when recognition ends (release, or switch from Continuous).          |

## Cancel Pending On Release

`VoxrCommandRecogniser` can hold a command in a *pending* state when it waits for confirmation (`RequiresConfirmation`) or for a follow-up slot-fill (`AllowPartialMatch`). Pending commands normally resolve on their own — via follow-up speech, a confirm/cancel phrase, or the configurable `pendingTimeout` (default 5 s).

With `Cancel Pending On Release` enabled, lifting the talk button immediately cancels any pending command, firing `OnCommandCancelled`. Use this when you want the talk button to act as a hard reset for partial utterances. Leave it disabled if you want pending commands to survive release and resolve on their own timer.

## Android Permission Race

`VoxrSpeechRecogniser.StartRecognition` requests `RECORD_AUDIO` on first use. The request is asynchronous — a user who presses and releases the talk button faster than the permission dialog resolves would, without this controller, end up with recognition running *after* they released.

The controller reconciles this in `Update`: if it observes `IsRecognising == true` while the user is not asking to listen, it calls `StopRecognition`. The window between the native start and the reconciling stop is at most one frame. Fully closing the race would require cancelling the permission coroutine from inside `VoxrSpeechRecogniser` itself; the `Update` check is additive and keeps the low-level API unchanged.

(The guard cannot fire in the editor test runner — without the native DLL, `IsRecognising` is always false. Verification is manual, on a Quest device, using `logcat -s vosk-bridge:*`.)

## Lifecycle

- `OnDisable` / `OnApplicationPause(true)` stop recognition but preserve the want-to-recognise flag, so `OnEnable` / `OnApplicationPause(false)` resume silently without re-firing `OnTalkStarted`. This matters for Continuous mode: a disable/enable cycle (or the Quest home overlay) otherwise drops Continuous listening entirely.

---

## Manual Pattern (advanced)

If you want full control — or you are adding push-to-talk behind a feature flag in an existing scene — you can wire the recipe yourself instead of using the controller. This is the pattern the controller encapsulates:

```csharp
public class PushToTalk : MonoBehaviour
{
    [SerializeField] VoxrSpeechRecogniser recogniser;
    [SerializeField] VoxrCommandRecogniser commandRecogniser;

    void Start()
    {
        // Pre-warm the model at scene load (heavyweight, takes seconds)
        recogniser.Initialise();
    }

    public void OnTalkButtonPressed()
    {
        // Lightweight -- opens audio stream, starts recognition instantly
        recogniser.StartRecognition();
    }

    public void OnTalkButtonReleased()
    {
        // Stops audio capture, model stays loaded for fast restart
        recogniser.StopRecognition();
        // Flush any buffered speech immediately on release
        commandRecogniser.FlushPendingBuffer();
    }
}
```

### Why Push-to-Talk Matters

In grammar mode, VOSK **must** produce an in-vocabulary word for any detected audio -- it has no "silence" output. This means coughs, hums, taps on the microphone, and ambient noise can all trigger false matches, typically short words like "on", "from", or "four" that sit closest to low-energy noise in phoneme space.

Push-to-talk eliminates this problem entirely by only running recognition during the button press. The two-tier lifecycle (see [Getting Started](getting-started.md)) makes this efficient: `Initialise()` loads the model once at scene start, and subsequent `StartRecognition()` / `StopRecognition()` calls are near-instant.

### Implementation Tips

- Call `FlushPendingBuffer()` on button release to ensure any speech still in the utterance buffer is parsed immediately, rather than waiting for the buffer window to expire.
- Wire the button press to an XR controller input (e.g. grip or trigger) using Unity's Input System or XR Interaction Toolkit.
- Provide visual feedback (e.g. a microphone icon or recording indicator) so the user knows when the system is listening.
- The manual pattern does **not** close the Android permission race (see above). If you hit it in practice, either switch to `VoxrPushToTalkController` or add the same `Update` guard yourself.

---

## Error Handling

All errors are surfaced via the `OnError` event on `VoxrSpeechRecogniser` with a structured `VoxrBridgeErrorCode` and a human-readable description.

```csharp
recogniser.OnError += (code, message) =>
{
    switch (code)
    {
        case VoxrBridgeErrorCode.PermissionDenied:
            // Prompt user to grant microphone permission
            break;
        case VoxrBridgeErrorCode.ModelLoadFailed:
            // Check model archive in StreamingAssets
            break;
        case VoxrBridgeErrorCode.AudioDeviceUnavailable:
            // Mic hardware not found or in use
            break;
        case VoxrBridgeErrorCode.RingBufferOverflow:
            // Audio buffer overflowed -- recognition may have gaps
            // Typically transient, no action needed
            break;
        default:
            Debug.LogError($"VOSK [{code}]: {message}");
            break;
    }
};
```

### Error Codes Most Relevant to PTT

- **`PermissionDenied`** -- `RECORD_AUDIO` permission not granted on Android. Must be in the manifest and granted at runtime.
- **`AudioDeviceUnavailable`** -- Audio input device could not be opened -- mic not found or in use.
- **`AlreadyRunning`** -- `StartRecognition()` called while recognition is already running (e.g. double button press).

For the complete error code reference, see [VoxrBridgeErrorCode](api/error-codes.md).

### Common Error Scenarios

**PermissionDenied on Quest:** Add `RECORD_AUDIO` to your Android manifest or enable it in Player Settings > Android > Other Settings. The SDK requests the permission at runtime, but the manifest entry must be present.

**ModelLoadFailed:** Verify the `.zip` is at `Assets/StreamingAssets/<modelName>.zip` where `<modelName>` matches the `modelRelativePath` field on `VoxrSpeechRecogniser`. Check `OnModelReady` to confirm extraction succeeded.

**AudioDeviceUnavailable in Editor:** Ensure the four `libvosk.dll` DLLs are placed correctly and a microphone is connected as the Windows default input device. See [Editor Testing](editor-testing.md) for setup details.

---

## See Also

- [Getting Started](getting-started.md) -- The two-tier lifecycle that makes push-to-talk efficient
- [Editor Testing](editor-testing.md) -- Text injection and batch testing for iteration without audio hardware
- [Troubleshooting](troubleshooting.md) -- Platform-specific issues and solutions
- [Known Limitations](../KNOWN_LIMITATIONS.md) -- Noise and false triggers in grammar mode
