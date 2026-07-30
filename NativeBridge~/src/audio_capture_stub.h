// ============================================================================
// Purpose:  No-op capture backend for platforms without audio capture
//           (desktop builds); audio enters via vosk_bridge_push_audio instead
// Layer:    NativeBridge
// Owns:     AudioCapture (class)
// Depends:  RingBuffer
// ============================================================================

#ifndef AUDIO_CAPTURE_STUB_H
#define AUDIO_CAPTURE_STUB_H

#include <atomic>
#include "ring_buffer.h"

// Stub capture: Start is a no-op success, HasError is always false — a
// silent microphone, so even non-push code paths are safe on platforms
// with no capture hardware path.
class AudioCapture {
public:
    AudioCapture() = default;
    ~AudioCapture() { Stop(); }

    // "Start capturing" — records running state, never produces samples.
    int Start(RingBuffer<float>* ring_buffer);

    void Stop();

    bool IsRunning() const { return running_.load(std::memory_order_acquire); }
    bool HasError() const { return false; }

private:
    std::atomic<bool> running_{false};
};

#endif // AUDIO_CAPTURE_STUB_H
