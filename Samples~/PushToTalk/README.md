# Push-to-Talk Sample

Hold-to-talk gating with the `VoskPushToTalkController` component. Includes a runtime toggle between push-to-talk and continuous listening modes.

## Setup

1. **Import the sample** via Package Manager > VOSK XR Speech Recognition > Samples > Push-to-Talk > Import.

2. **Download a VOSK model:**
   - Get [vosk-model-small-en-us-0.15](https://alphacephei.com/vosk/models) (~50 MB).
   - Place the `.zip` in `Assets/StreamingAssets/vosk-model-small-en-us-0.15.zip`.

3. **Build the scene:**
   - Create a new scene.
   - Add a GameObject named `VoskRig`. Attach:
     - `VoskSpeechRecogniser`
     - `VoskPushToTalkController`
     - `VoskCommandRecogniser` (optional — enables pending-buffer flush on release)
   - In the `VoskPushToTalkController` Inspector:
     - Drag the `VoskSpeechRecogniser` into the `Speech Recogniser` slot.
     - Drag the `VoskCommandRecogniser` into the `Command Recogniser` slot (optional).
     - Leave `Listening Mode` on `PushToTalk` (default).
   - Add a Canvas with:
     - A `Button` labelled "Hold to Talk".
     - A `TextMeshPro - Text (UI)` element for live transcription.
     - A small `Image` (recording indicator).
   - Add the `PushToTalkDemo` script to any GameObject and wire the `recogniser`, `controller`, `transcriptText`, and `recordingIndicator` references in the Inspector.

4. **Wire the talk button:**
   - Select the Button > Inspector > add an `EventTrigger` component.
   - Add `PointerDown` event → drag the `VoskPushToTalkController` → select `PressTalk()`.
   - Add `PointerUp` event → drag the `VoskPushToTalkController` → select `ReleaseTalk()`.
   - (Plain `onClick` fires on release only; use `EventTrigger` for true press/release semantics.)

5. **Optional — Mode toggle:**
   - Add a second Button labelled "Toggle Mode".
   - On its `onClick`, call `PushToTalkDemo.TogglePushToTalkMode()`.

6. **Optional — Recording indicator via UnityEvents:**
   - In the controller's `On Talk Started` list, add a callback on the indicator's `Image.color` setter pointing to your active colour.
   - In `On Talk Ended`, point it back to your idle colour.
   - The demo script does the same thing in code — use whichever approach fits your project.

7. **Build and deploy:**
   - Switch platform to Android (arm64).
   - Ensure `RECORD_AUDIO` is enabled in Player Settings > Android > Other Settings.
   - Build and run on a Meta Quest headset.

## Wiring XR controllers

For XR Interaction Toolkit, bind a controller button (e.g. grip) to a press/release action:

```csharp
[SerializeField] UnityEngine.InputSystem.InputActionReference talkAction;
[SerializeField] VoskPushToTalkController controller;

void OnEnable()
{
    talkAction.action.started  += _ => controller.PressTalk();
    talkAction.action.canceled += _ => controller.ReleaseTalk();
    talkAction.action.Enable();
}
```

## Runtime mode switching

```csharp
// Continuous (always-on) listening — PressTalk / ReleaseTalk become no-ops.
controller.ListeningMode = VoskListeningMode.Continuous;

// Back to hold-to-talk.
controller.ListeningMode = VoskListeningMode.PushToTalk;
```

Switching to `Continuous` fires `OnTalkStarted`; switching away while recognising fires `OnTalkEnded`. Setting the same mode twice is a no-op.

## See Also

- [Push-to-Talk guide](../../Documentation~/push-to-talk.md) — full component reference and the manual (low-level) pattern
- [Command Recognition sample](../CommandRecognition/README.md) — command parsing with intent and slot extraction
