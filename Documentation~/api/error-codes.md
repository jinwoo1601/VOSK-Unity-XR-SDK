# VoxrBridgeErrorCode

`public enum VoxrBridgeErrorCode` -- Namespace: `VoXR`

Error codes reported by the native bridge via `VoxrSpeechRecogniser.OnError`.

## Values

| Value | Int | Description |
|-------|-----|-------------|
| `Ok` | 0 | Success |
| `ModelLoadFailed` | 1 | VOSK model failed to load |
| `AudioDeviceUnavailable` | 2 | Audio input device could not be opened |
| `PermissionDenied` | 3 | RECORD_AUDIO permission not granted |
| `RingBufferOverflow` | 4 | Audio buffer overflowed; recognition may have gaps |
| `AlreadyRunning` | 5 | Recognition is already running |
| `NotInitialised` | 6 | Bridge not initialised |
| `AlreadyInitialised` | 7 | Bridge already initialised |

## Extension Method

`ToDescription()` returns a human-readable string for each code.

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
