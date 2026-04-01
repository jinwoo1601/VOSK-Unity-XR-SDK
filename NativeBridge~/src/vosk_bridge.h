#ifndef VOSK_BRIDGE_H
#define VOSK_BRIDGE_H

#define VOSK_BRIDGE_EXPORT __attribute__((visibility("default")))

#ifdef __cplusplus
extern "C" {
#endif

enum VoskBridgeError {
    VOSK_BRIDGE_OK                        = 0,
    VOSK_BRIDGE_ERR_MODEL_LOAD_FAILED     = 1,
    VOSK_BRIDGE_ERR_AUDIO_DEVICE_UNAVAIL  = 2,
    VOSK_BRIDGE_ERR_PERMISSION_DENIED     = 3,
    VOSK_BRIDGE_ERR_RING_BUFFER_OVERFLOW  = 4,
    VOSK_BRIDGE_ERR_ALREADY_RUNNING       = 5,
    VOSK_BRIDGE_ERR_NOT_INITIALISED       = 6,
    VOSK_BRIDGE_ERR_ALREADY_INITIALISED   = 7,
};

// Heavyweight lifecycle (model load / teardown)
VOSK_BRIDGE_EXPORT int  vosk_bridge_init(const char* model_path, float sample_rate);
VOSK_BRIDGE_EXPORT void vosk_bridge_destroy();

// Lightweight lifecycle (audio stream start / stop)
VOSK_BRIDGE_EXPORT int  vosk_bridge_start();
VOSK_BRIDGE_EXPORT void vosk_bridge_stop();
VOSK_BRIDGE_EXPORT int  vosk_bridge_reset();

// Results (polled from C# Update loop)
VOSK_BRIDGE_EXPORT int         vosk_bridge_has_result();
VOSK_BRIDGE_EXPORT const char* vosk_bridge_get_result(int* out_is_final);

// Status
VOSK_BRIDGE_EXPORT int vosk_bridge_is_running();
VOSK_BRIDGE_EXPORT int vosk_bridge_is_initialised();
VOSK_BRIDGE_EXPORT int vosk_bridge_get_error(char* buf, int buf_size);

#ifdef __cplusplus
}
#endif

#endif // VOSK_BRIDGE_H
