// ============================================================================
// Purpose:  Error codes returned by the native bridge, with human-readable descriptions
// Layer:    Runtime
// Owns:     VoskBridgeErrorCode (public enum), VoskBridgeErrorCodeExtensions (public static)
// Depends:  (none)
// ============================================================================
namespace VoskXR
{
    public enum VoskBridgeErrorCode
    {
        Ok = 0,
        ModelLoadFailed = 1,
        AudioDeviceUnavailable = 2,
        PermissionDenied = 3,
        RingBufferOverflow = 4,
        AlreadyRunning = 5,
        NotInitialised = 6,
        AlreadyInitialised = 7,
    }

    public static class VoskBridgeErrorCodeExtensions
    {
        public static string ToDescription(this VoskBridgeErrorCode code) => code switch
        {
            VoskBridgeErrorCode.Ok => "Success",
            VoskBridgeErrorCode.ModelLoadFailed => "VOSK model failed to load. Check the model path and archive integrity.",
            VoskBridgeErrorCode.AudioDeviceUnavailable => "Audio input device could not be opened.",
            VoskBridgeErrorCode.PermissionDenied => "Microphone permission (RECORD_AUDIO) was not granted.",
            VoskBridgeErrorCode.RingBufferOverflow => "Audio ring buffer overflowed. Recognition may have gaps.",
            VoskBridgeErrorCode.AlreadyRunning => "Recognition is already running.",
            VoskBridgeErrorCode.NotInitialised => "Bridge is not initialised. Call Initialise() first.",
            VoskBridgeErrorCode.AlreadyInitialised => "Bridge is already initialised.",
            _ => $"Unknown error code ({(int)code})",
        };
    }
}
