#ifndef AUDIO_CAPTURE_AUDIORECORD_H
#define AUDIO_CAPTURE_AUDIORECORD_H

#include <jni.h>
#include <atomic>
#include <thread>
#include "ring_buffer.h"

// Audio capture using Android's Java AudioRecord API via JNI.
// Required on Meta Quest where AAudio input delivers silence.
class AudioCapture {
public:
    AudioCapture() = default;
    ~AudioCapture() { Stop(); }

    // Start capturing audio into the given ring buffer.
    int Start(RingBuffer<float>* ring_buffer);

    // Stop capturing and release the AudioRecord.
    void Stop();

    bool IsRunning() const { return running_.load(std::memory_order_acquire); }
    bool HasError() const { return error_occurred_.load(std::memory_order_acquire); }

private:
    void ReadLoop(JavaVM* jvm);

    jobject audio_record_ = nullptr;
    RingBuffer<float>* ring_buffer_ = nullptr;
    std::thread read_thread_;
    std::atomic<bool> running_{false};
    std::atomic<bool> error_occurred_{false};
};

// Must be called once at library load (JNI_OnLoad).
void AudioCapture_SetJavaVM(JavaVM* vm);

#endif // AUDIO_CAPTURE_AUDIORECORD_H
