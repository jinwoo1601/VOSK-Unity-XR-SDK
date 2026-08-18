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

Both transitions are guarded by the `Speech Recogniser` reference. Without one — unassigned, or a component Unity has destroyed — the property still changes the mode, but starts and stops nothing and fires neither event, the same way `PressTalk()` and `ReleaseTalk()` do nothing without it. An event is only ever an announcement of a start or stop that actually happened.

## Inspector Reference

| Field                         | Purpose                                                                                 |
|-------------------------------|-----------------------------------------------------------------------------------------|
| `Speech Recogniser`           | The `VoxrSpeechRecogniser` the controller drives. Required.                              |
| `Command Recogniser`          | Optional. When assigned, `ReleaseTalk` calls `FlushPendingBuffer` so trailing speech parses immediately rather than waiting for the buffer window. |
| `Listening Mode`              | Initial mode (`PushToTalk` or `Continuous`). Can be changed at runtime via the property. |
| `Initialise On Start`         | When enabled, calls `VoxrSpeechRecogniser.Initialise()` in `Start` so the model is pre-warmed before the first press. |
| `Cancel Pending On Release`   | When enabled, `ReleaseTalk` also cancels any pending command on the command recogniser (see below). Does nothing unless the optional `Command Recogniser` reference is assigned — the cancel runs inside the same guard as the flush. |
| `On Talk Started`             | `UnityEvent` fired whenever the controller starts recognition: from `PressTalk()`, from a runtime switch to `Continuous`, or when a scene authored on `Continuous` is first enabled. |
| `On Talk Ended`               | `UnityEvent` fired when recognition ends (release, or switch from Continuous).          |

**Subscribe before enable to catch the `Continuous` startup event.** A scene authored with `Listening Mode = Continuous` fires `On Talk Started` from the controller's `OnEnable` — during scene load, or the moment you `Instantiate` such a prefab, before that call even returns. Listeners wired in the Inspector always receive it, because they are serialized with the component; that is the recommended way to drive a recording indicator. A listener added from code receives it only if it subscribed before the controller was enabled. `Start` is always too late, and another component's `OnEnable` cannot be relied on — ordering across GameObjects is undefined unless you pin it in Script Execution Order. Three ways to be certain:

- Wire the event in the Inspector.
- Keep the GameObject inactive until you have subscribed, then activate it.
- Leave the Inspector on `PushToTalk` and assign `ListeningMode = VoxrListeningMode.Continuous` from your own `Start()`. That is a *change*, so the setter fires the event — given the assigned `Speech Recogniser` it needs — and by `Start` every listener has registered.

If you would rather not depend on event timing at all, `VoxrSpeechRecogniser.IsRecognising` reflects live recognition state and is safe to poll. (`PushToTalk` scenes are unaffected either way: the first event follows a button press, long after any listener has registered.)

## Public API

| Member | Kind | Description |
|--------|------|-------------|
| `ListeningMode` | `VoxrListeningMode` property (get/set) | Reads or switches the mode at runtime. The setter starts/stops recognition and fires the events per *Setter semantics* above, including its `Speech Recogniser` guard. See [`VoxrListeningMode`](api/data-types.md#voxrlisteningmode). |
| `OnTalkStarted` | `UnityEvent` property (get) | The Inspector's `On Talk Started`. Subscribe from code with `OnTalkStarted.AddListener(...)`. |
| `OnTalkEnded` | `UnityEvent` property (get) | The Inspector's `On Talk Ended`, same access pattern. |
| `PressTalk()` | `void` | Starts recognition and fires `OnTalkStarted`. No-op in `Continuous` mode, without a `Speech Recogniser`, or while a press is already held. |
| `ReleaseTalk()` | `void` | Stops recognition, flushes the utterance buffer (and cancels a pending command if `Cancel Pending On Release` is set), then fires `OnTalkEnded`. No-op in `Continuous` mode, without a `Speech Recogniser`, or when no press is held. |

## Cancel Pending On Release

`VoxrCommandRecogniser` can hold a command in a *pending* state when it waits for confirmation (`RequiresConfirmation`), for a follow-up slot-fill (`AllowPartialMatch`), or — with `disambiguateSiblingTies` enabled — for the speaker to say which of several indistinguishable commands they meant. Pending commands normally resolve on their own: via follow-up speech, a confirm/cancel phrase, a discriminating word, or the configurable `pendingTimeout` (default 5 s).

With `Cancel Pending On Release` enabled, lifting the talk button immediately cancels any pending command, firing `OnCommandCancelled`. Use this when you want the talk button to act as a hard reset for partial utterances. Leave it disabled if you want pending commands to survive release and resolve on their own timer.

**Turn this off if you use [`disambiguateSiblingTies`](command-recognition.md#ambiguous-commands-ask-instead-of-guessing).** `ReleaseTalk` flushes the buffer and *then* cancels, and the flush is what creates a disambiguation — so with this setting on, the question is raised and discarded in consecutive statements and the speaker is never asked. That is the setting doing its job, not a bug: it exists to make the button a hard reset.

Left off, the question survives release and the speaker answers it on their next press. Note the clock does not stop: `pendingTimeout` runs from the moment the question was raised, not from the next press, so the whole round trip -- prompt, react, press, speak -- has to fit inside it. Raise `pendingTimeout` above its 5 s default if that is tight.

## Android Permission Race

`VoxrSpeechRecogniser.StartRecognition` requests `RECORD_AUDIO` on first use. The request is asynchronous — a user who presses and releases the talk button faster than the permission dialog resolves would, without this controller, end up with recognition running *after* they released.

The controller reconciles this in `Update`: if it observes `IsRecognising == true` while the user is not asking to listen, it calls `StopRecognition`. The window between the native start and the reconciling stop is at most one frame. Fully closing the race would require cancelling the permission coroutine from inside `VoxrSpeechRecogniser` itself; the `Update` check is additive and keeps the low-level API unchanged.

(The race cannot be exercised in the Editor: the permission-wait coroutine this guard reconciles is compiled only for Android player builds — `#if UNITY_ANDROID && !UNITY_EDITOR` — so nothing off-device ever starts recognition late. The Windows Editor backend does report `IsRecognising == true`, so the `Update` check itself runs there; it simply has no late start to catch. Verification is manual, on a Quest device, using `logcat -s vosk-bridge:*`.)

## Lifecycle

- `OnDisable` / `OnApplicationPause(true)` stop recognition but preserve the want-to-recognise flag, so `OnEnable` / `OnApplicationPause(false)` resume silently without re-firing `OnTalkStarted`. This matters for Continuous mode: a disable/enable cycle (or the Quest home overlay) otherwise drops Continuous listening entirely.
- The one enable that is *not* silent is the first one in a scene authored on `Continuous`. There is no press and no mode change to carry the intent, so that enable is where wanting to recognise begins — and, provided a `Speech Recogniser` is assigned, it fires `OnTalkStarted` like any other start. Every enable after it resumes silently under the rule above.
- A controller authored on `Continuous` whose *component checkbox* is unticked does nothing until you tick it — the checkbox serializes separately from the GameObject's active state. Ticking it is that controller's first enable, so the announcement happens there rather than at scene load. Switching such a controller to `PushToTalk` before it has ever been enabled is silent: nothing had started, so there is no start for an `OnTalkEnded` to pair with.

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

**AudioDeviceUnavailable in Editor:** Ensure a microphone is connected and set as the Windows default input device. See [Editor Testing](editor-testing.md) for the live-mic backend overview.

---

## See Also

- [Getting Started](getting-started.md) -- The two-tier lifecycle that makes push-to-talk efficient
- [Editor Testing](editor-testing.md) -- Text injection and batch testing for iteration without audio hardware
- [Command Recognition](command-recognition.md) -- pending commands, and resolving two commands that are too alike to tell apart
- [Troubleshooting](troubleshooting.md) -- Platform-specific issues and solutions
- [Known Limitations](../KNOWN_LIMITATIONS.md) -- Noise and false triggers in grammar mode
