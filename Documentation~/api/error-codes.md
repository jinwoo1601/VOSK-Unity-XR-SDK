# VoxrBridgeErrorCode

`public enum VoxrBridgeErrorCode` -- Namespace: `VoXR`

Error codes reported by the native bridge via `VoxrSpeechRecogniser.OnError`.

## Values

| Value | Int | Description |
|-------|-----|-------------|
| `Ok` | 0 | Success -- returned by bridge calls, never delivered through `OnError` |
| `ModelLoadFailed` | 1 | VOSK model failed to load |
| `AudioDeviceUnavailable` | 2 | Audio input device could not be opened |
| `PermissionDenied` | 3 | RECORD_AUDIO permission not granted |
| `RingBufferOverflow` | 4 | Audio buffer overflowed; recognition may have gaps |
| `AlreadyRunning` | 5 | Recognition is already running |
| `NotInitialised` | 6 | Bridge not initialised |
| `AlreadyInitialised` | 7 | Bridge already initialised |
| `NotRunning` | 8 | Recognition not running (push mode requires a prior start) |

## Extension Method

`public static class VoxrBridgeErrorCodeExtensions` -- Namespace: `VoXR`

`public static string ToDescription(this VoxrBridgeErrorCode code)` returns a human-readable string for each code. A value outside the enum falls back to `"Unknown error code (N)"`, with `N` the integer value.

> `NotRunning` is reported by the bridge's push-audio seam (`vosk_bridge_push_audio`, a verification-only API with no managed caller yet). Unlike every other bridge call, that function returns error codes **negated** (negative values), reserving the non-negative range for sample counts — it never surfaces through `OnError`.

## Example

```csharp
recogniser.OnError += (code, message) =>
{
    switch (code)
    {
        case VoxrBridgeErrorCode.PermissionDenied:
            ShowPermissionDialog();
            break;
        case VoxrBridgeErrorCode.AudioDeviceUnavailable:
            Debug.LogError($"Audio device error: {code.ToDescription()}");
            break;
        default:
            Debug.LogWarning($"VOSK error {code}: {message}");
            break;
    }
};
```

## See Also

- [Push-to-Talk #error-handling](../push-to-talk.md#error-handling) -- worked examples for the codes most relevant during press/release flows
- [VoxrSpeechRecogniser](speech-recogniser.md) -- `OnError` event
