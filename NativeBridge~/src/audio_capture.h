// ============================================================================
// Purpose:  Backend-neutral capture include — forwards to the backend selected
//           by the VOSK_BRIDGE_CAPTURE CMake option
// Layer:    NativeBridge
// Owns:     (no types — include forwarding only)
// Depends:  AudioCapture (selected backend)
// ============================================================================

#ifndef VOSK_BRIDGE_AUDIO_CAPTURE_H
#define VOSK_BRIDGE_AUDIO_CAPTURE_H

#if defined(VOSK_BRIDGE_CAPTURE_AUDIORECORD)
#include "audio_capture_audiorecord.h"
#elif defined(VOSK_BRIDGE_CAPTURE_AAUDIO)
#include "audio_capture_aaudio.h"
#elif defined(VOSK_BRIDGE_CAPTURE_STUB)
#include "audio_capture_stub.h"
#else
#error "No capture backend selected — set the VOSK_BRIDGE_CAPTURE CMake option (audiorecord|aaudio|stub)"
#endif

#endif // VOSK_BRIDGE_AUDIO_CAPTURE_H
