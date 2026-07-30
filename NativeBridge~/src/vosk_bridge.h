#ifndef VOSK_BRIDGE_H
#define VOSK_BRIDGE_H

#include <stdint.h>

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
    VOSK_BRIDGE_ERR_NOT_RUNNING           = 8,
};

// Heavyweight lifecycle (model load / teardown)
VOSK_BRIDGE_EXPORT int  vosk_bridge_init(const char* model_path, float sample_rate,
                                         float mic_gain_target_db);
VOSK_BRIDGE_EXPORT void vosk_bridge_destroy();

// Lightweight lifecycle (audio stream start / stop)
VOSK_BRIDGE_EXPORT int  vosk_bridge_start();
VOSK_BRIDGE_EXPORT void vosk_bridge_stop();
VOSK_BRIDGE_EXPORT int  vosk_bridge_reset();
VOSK_BRIDGE_EXPORT int  vosk_bridge_set_grammar(const char* grammar_json);

// Push-audio mode: the recognition thread runs without any capture backend;
// audio is supplied by the caller instead. Capture and push are mutually
// exclusive — a push-mode session never starts capture and vice versa.
// Starts like vosk_bridge_start(), same return codes.
VOSK_BRIDGE_EXPORT int vosk_bridge_start_push();

// Writes pre-DSP 48 kHz mono float samples into the recognition pipeline.
// Returns samples written (0..count): a short write means the ring is full —
// drain results, back off briefly, retry the remainder (unlike a capture
// backend, pushed audio is never overwritten on overflow). On misuse returns
// a NEGATIVE VoskBridgeError (-VOSK_BRIDGE_ERR_NOT_INITIALISED, or
// -VOSK_BRIDGE_ERR_NOT_RUNNING when not started in push mode).
// Call from one thread at a time (single-producer contract).
VOSK_BRIDGE_EXPORT int vosk_bridge_push_audio(const float* samples, uint32_t count);

// Rolling RMS of recent pre-DSP audio (linear, 0..1, ~300 ms window),
// updated by the recognition thread in either mode; 0 when not running
// (zeroed on start and stop). Safe to call at any lifecycle point.
VOSK_BRIDGE_EXPORT float vosk_bridge_get_input_level();

// Results (polled from C# Update loop)
VOSK_BRIDGE_EXPORT int         vosk_bridge_has_result();
VOSK_BRIDGE_EXPORT const char* vosk_bridge_get_result(int* out_is_final, int* out_length);

// Status
VOSK_BRIDGE_EXPORT int vosk_bridge_is_running();
VOSK_BRIDGE_EXPORT int vosk_bridge_is_initialised();
VOSK_BRIDGE_EXPORT int vosk_bridge_get_error(char* buf, int buf_size);

#ifdef __cplusplus
}
#endif

#endif // VOSK_BRIDGE_H
