# Push-to-Talk and Error Handling

This guide covers the push-to-talk pattern for gating recognition to intentional speech, and the error handling system for responding to runtime failures.

---

## Push-to-Talk Pattern

Push-to-talk gates recognition to a button press, ensuring the SDK only processes intentional speech. This is the recommended approach for noisy environments and any application where false triggers from ambient sound, coughs, or background conversation are unacceptable.

```csharp
public class PushToTalk : MonoBehaviour
{
    [SerializeField] VoskSpeechRecogniser recogniser;
    [SerializeField] VoskCommandRecogniser commandRecogniser;

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

---

## Error Handling

All errors are surfaced via the `OnError` event on `VoskSpeechRecogniser` with a structured `VoskBridgeErrorCode` and a human-readable description.

```csharp
recogniser.OnError += (code, message) =>
{
    switch (code)
    {
        case VoskBridgeErrorCode.PermissionDenied:
            // Prompt user to grant microphone permission
            break;
        case VoskBridgeErrorCode.ModelLoadFailed:
            // Check model archive in StreamingAssets
            break;
        case VoskBridgeErrorCode.AudioDeviceUnavailable:
            // Mic hardware not found or in use
            break;
        case VoskBridgeErrorCode.RingBufferOverflow:
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

For the complete error code reference, see [VoskBridgeErrorCode](api/error-codes.md).

### Common Error Scenarios

**PermissionDenied on Quest:** Add `RECORD_AUDIO` to your Android manifest or enable it in Player Settings > Android > Other Settings. The SDK requests the permission at runtime, but the manifest entry must be present.

**ModelLoadFailed:** Verify the `.zip` is at `Assets/StreamingAssets/<modelName>.zip` where `<modelName>` matches the `modelRelativePath` field on `VoskSpeechRecogniser`. Check `OnModelReady` to confirm extraction succeeded.

**AudioDeviceUnavailable in Editor:** Ensure the four `libvosk.dll` DLLs are placed correctly and a microphone is connected as the Windows default input device. See [Editor Testing](editor-testing.md) for setup details.

---

## See Also

- [Getting Started](getting-started.md) -- The two-tier lifecycle that makes push-to-talk efficient
- [Editor Testing](editor-testing.md) -- Text injection and batch testing for iteration without audio hardware
- [Troubleshooting](troubleshooting.md) -- Platform-specific issues and solutions
- [Known Limitations](../KNOWN_LIMITATIONS.md) -- Noise and false triggers in grammar mode
