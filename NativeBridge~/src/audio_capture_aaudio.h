#ifndef AUDIO_CAPTURE_AAUDIO_H
#define AUDIO_CAPTURE_AAUDIO_H

#include <aaudio/AAudio.h>
#include <atomic>
#include "ring_buffer.h"

// AAudio microphone capture.
// Captures at 48 kHz float32 mono via the callback model.
// The data callback writes directly into the provided ring buffer.
class AudioCapture {
public:
    AudioCapture() : stream_(nullptr), ring_buffer_(nullptr), running_(false), error_occurred_(false) {}
    ~AudioCapture() { Stop(); }

    // Start capturing audio into the given ring buffer.
    // Returns VOSK_BRIDGE_OK on success, or an error code.
    int Start(RingBuffer<float>* ring_buffer);

    // Stop capturing and close the stream.
    void Stop();

    bool IsRunning() const { return running_.load(std::memory_order_acquire); }
    bool HasError() const { return error_occurred_.load(std::memory_order_acquire); }

private:
    static aaudio_data_callback_result_t DataCallback(
        AAudioStream* stream, void* user_data, void* audio_data, int32_t num_frames);

    static void ErrorCallback(
        AAudioStream* stream, void* user_data, aaudio_result_t error);

    AAudioStream* stream_;
    RingBuffer<float>* ring_buffer_;
    std::atomic<bool> running_;
    std::atomic<bool> error_occurred_;
    std::atomic<uint32_t> callback_count_{0};
};

#endif // AUDIO_CAPTURE_AAUDIO_H
