# Push-to-Talk Sample

Hold-to-talk gating with the `VoxrPushToTalkController` component. Includes a
runtime toggle between push-to-talk and continuous listening modes.

## Requirements

This sample uses Unity's new Input System for keyboard input and pointer
events on the Hold-to-Talk button:

- `com.unity.inputsystem` package installed (Window > Package Manager)
- Project Settings > Player > **Active Input Handling** = `Input System Package (New)` or `Both`

If you're on legacy input only, replace `Input System UI Input Module` on
the `EventSystem` GameObject with `Standalone Input Module`, and edit
`PushToTalkDemo.cs` to use `Input.GetKey` for keyboard polling.

## Setup

1. **Import the sample** via Package Manager > VoXR Speech Recognition > Samples > Push-to-Talk > Import.

2. **Download a VOSK model:**
   - Get [vosk-model-small-en-us-0.15](https://alphacephei.com/vosk/models) (~50 MB).
   - Place the `.zip` in `Assets/StreamingAssets/vosk-model-small-en-us-0.15.zip`.

3. **Open the scene** at `Assets/Samples/VoXR Speech Recognition/<version>/Push-to-Talk/PushToTalk.unity`.

4. **Run:**
   - **Windows Editor:** press Play. Hold `Space` (or click and hold the on-screen "Hold to Talk" button) to talk; release to stop. Press `Tab` to toggle Push-to-Talk vs Continuous mode.
   - **Quest:** switch platform to Android (arm64), enable `RECORD_AUDIO` in Player Settings, build, deploy. The on-screen button is pointer-driven and works with controller raycast input.

## What's in the scene

| GameObject | Role |
|---|---|
| `Recogniser` | `VoxrSpeechRecogniser` |
| `Controller` | `VoxrPushToTalkController` with `Listening Mode = PushToTalk` and `Initialise On Start = true` so the model pre-warms before the first press |
| `PushToTalkDemo` | Wires `Space` / `Tab` keyboard input, drives the recording indicator colour, and updates the mode label |
| `Canvas/HoldToTalkButton` | `Image` raycast target with the small `HoldToTalkButton` component. Its `onPointerDown` UnityEvent calls `Controller.PressTalk()`, and `onPointerUp` calls `Controller.ReleaseTalk()` |
| `Canvas/RecordingIndicator` | Square `Image` whose colour is set by `PushToTalkDemo.ShowRecording`/`ShowIdle` (subscribed to `OnTalkStarted` / `OnTalkEnded`) |
| `Canvas/TranscriptText` | Live transcript |
| `Canvas/ModeLabel` | Reads from `controller.ListeningMode` |

The scene does not wire `_commandRecogniser` on the controller. Drop a
`VoxrCommandRecogniser` into your scene and assign it to enable utterance-buffer
flush on release.

## Wiring an XR controller

The shipped scene is screen+keyboard so the sample doesn't pull in
`com.unity.xr.interaction.toolkit` as a dependency. To drive `PressTalk` /
`ReleaseTalk` from a real XR controller, add an Input System action and:

```csharp
using UnityEngine;
using UnityEngine.InputSystem;
using VoXR;

public class XrPushToTalkBinding : MonoBehaviour
{
    [SerializeField] InputActionReference talkAction;   // e.g. trigger / grip
    [SerializeField] VoxrPushToTalkController controller;

    void OnEnable()
    {
        talkAction.action.started  += _ => controller.PressTalk();
        talkAction.action.canceled += _ => controller.ReleaseTalk();
        talkAction.action.Enable();
    }

    void OnDisable()
    {
        talkAction.action.started  -= _ => controller.PressTalk();
        talkAction.action.canceled -= _ => controller.ReleaseTalk();
        talkAction.action.Disable();
    }
}
```

Or, with XRI's `XRBaseInteractor`:

```csharp
interactor.selectEntered.AddListener(_ => controller.PressTalk());
interactor.selectExited .AddListener(_ => controller.ReleaseTalk());
```

## Runtime mode switching

```csharp
// Continuous (always-on) listening — PressTalk / ReleaseTalk become no-ops.
controller.ListeningMode = VoxrListeningMode.Continuous;

// Back to hold-to-talk.
controller.ListeningMode = VoxrListeningMode.PushToTalk;
```

Switching to `Continuous` fires `OnTalkStarted`; switching away while
recognising fires `OnTalkEnded`. Setting the same mode twice is a no-op.

## See Also

- [Push-to-Talk guide](../../Documentation~/push-to-talk.md) — full component reference and the manual (low-level) pattern
- [Command Recognition sample](../CommandRecognition/README.md) — command parsing with intent and slot extraction
