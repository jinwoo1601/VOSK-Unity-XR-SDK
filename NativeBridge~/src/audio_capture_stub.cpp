// ============================================================================
// Purpose:  Implementation of the no-op capture backend
// Layer:    NativeBridge
// Owns:     AudioCapture (class, stub implementation)
// Depends:  RingBuffer, VoskBridgeError
// ============================================================================

#include "audio_capture_stub.h"
#include "vosk_bridge.h"

int AudioCapture::Start(RingBuffer<float>* /*ring_buffer*/) {
    running_.store(true, std::memory_order_release);
    return VOSK_BRIDGE_OK;
}

void AudioCapture::Stop() {
    running_.store(false, std::memory_order_release);
}
