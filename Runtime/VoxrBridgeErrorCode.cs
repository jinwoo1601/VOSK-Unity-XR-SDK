// ============================================================================
// Purpose:  Error codes returned by the native bridge, with human-readable descriptions
// Layer:    Runtime
// Owns:     VoxrBridgeErrorCode (public enum), VoxrBridgeErrorCodeExtensions (public static)
// Depends:  (none)
// ============================================================================
namespace VoXR
{
    public enum VoxrBridgeErrorCode
    {
        Ok = 0,
        ModelLoadFailed = 1,
        AudioDeviceUnavailable = 2,
        PermissionDenied = 3,
        RingBufferOverflow = 4,
        AlreadyRunning = 5,
        NotInitialised = 6,
        AlreadyInitialised = 7,
        NotRunning = 8,
    }

    public static class VoxrBridgeErrorCodeExtensions
    {
        public static string ToDescription(this VoxrBridgeErrorCode code) => code switch
        {
            VoxrBridgeErrorCode.Ok => "Success",
            VoxrBridgeErrorCode.ModelLoadFailed => "VOSK model failed to load. Check the model path and archive integrity.",
            VoxrBridgeErrorCode.AudioDeviceUnavailable => "Audio input device could not be opened.",
            VoxrBridgeErrorCode.PermissionDenied => "Microphone permission (RECORD_AUDIO) was not granted.",
            VoxrBridgeErrorCode.RingBufferOverflow => "Audio ring buffer overflowed. Recognition may have gaps.",
            VoxrBridgeErrorCode.AlreadyRunning => "Recognition is already running.",
            VoxrBridgeErrorCode.NotInitialised => "Bridge is not initialised. Call Initialise() first.",
            VoxrBridgeErrorCode.AlreadyInitialised => "Bridge is already initialised.",
                VoxrBridgeErrorCode.NotRunning =>
                    "Recognition is not running (push mode requires a prior start).",
            _ => $"Unknown error code ({(int)code})",
        };
    }
}
