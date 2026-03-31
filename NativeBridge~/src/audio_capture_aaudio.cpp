#include "audio_capture_aaudio.h"
#include "vosk_bridge.h"
#include "logging.h"

static constexpr int32_t kSampleRate = 48000;
static constexpr int32_t kChannelCount = 1;

int AudioCapture::Start(RingBuffer<float>* ring_buffer) {
    if (running_.load(std::memory_order_acquire))
        return VOSK_BRIDGE_ERR_ALREADY_RUNNING;

    error_occurred_.store(false, std::memory_order_release);
    ring_buffer_ = ring_buffer;

    AAudioStreamBuilder* builder = nullptr;
    aaudio_result_t result = AAudio_createStreamBuilder(&builder);
    if (result != AAUDIO_OK) {
        LOGE("Failed to create AAudio stream builder: %s", AAudio_convertResultToText(result));
        return VOSK_BRIDGE_ERR_AUDIO_DEVICE_UNAVAIL;
    }

    AAudioStreamBuilder_setDirection(builder, AAUDIO_DIRECTION_INPUT);
    AAudioStreamBuilder_setSampleRate(builder, kSampleRate);
    AAudioStreamBuilder_setChannelCount(builder, kChannelCount);
    AAudioStreamBuilder_setFormat(builder, AAUDIO_FORMAT_PCM_FLOAT);
    AAudioStreamBuilder_setSharingMode(builder, AAUDIO_SHARING_MODE_SHARED);
    AAudioStreamBuilder_setPerformanceMode(builder, AAUDIO_PERFORMANCE_MODE_NONE);
    AAudioStreamBuilder_setInputPreset(builder, AAUDIO_INPUT_PRESET_UNPROCESSED);
    AAudioStreamBuilder_setDataCallback(builder, DataCallback, this);
    AAudioStreamBuilder_setErrorCallback(builder, ErrorCallback, this);

    result = AAudioStreamBuilder_openStream(builder, &stream_);
    AAudioStreamBuilder_delete(builder);

    if (result != AAUDIO_OK) {
        LOGE("Failed to open AAudio stream: %s", AAudio_convertResultToText(result));
        stream_ = nullptr;

        if (result == AAUDIO_ERROR_NO_SERVICE)
            return VOSK_BRIDGE_ERR_PERMISSION_DENIED;
        return VOSK_BRIDGE_ERR_AUDIO_DEVICE_UNAVAIL;
    }

    result = AAudioStream_requestStart(stream_);
    if (result != AAUDIO_OK) {
        LOGE("Failed to start AAudio stream: %s", AAudio_convertResultToText(result));
        AAudioStream_close(stream_);
        stream_ = nullptr;
        return VOSK_BRIDGE_ERR_AUDIO_DEVICE_UNAVAIL;
    }

    running_.store(true, std::memory_order_release);
    LOGI("AAudio capture started: %d Hz, float32, mono", kSampleRate);
    return VOSK_BRIDGE_OK;
}

void AudioCapture::Stop() {
    bool was_running = running_.exchange(false, std::memory_order_acq_rel);

    if (stream_) {
        if (was_running)
            AAudioStream_requestStop(stream_);
        AAudioStream_close(stream_);
        stream_ = nullptr;
    }

    LOGI("AAudio capture stopped");
}

aaudio_data_callback_result_t AudioCapture::DataCallback(
    AAudioStream* /*stream*/, void* user_data, void* audio_data, int32_t num_frames)
{
    auto* self = static_cast<AudioCapture*>(user_data);
    if (!self->running_.load(std::memory_order_acquire))
        return AAUDIO_CALLBACK_RESULT_STOP;

    auto* samples = static_cast<const float*>(audio_data);
    self->ring_buffer_->Write(samples, static_cast<uint32_t>(num_frames));

    // Diagnostic: log first callback and then every ~5 seconds (48kHz / 960 frames ≈ 50 cb/s)
    uint32_t count = self->callback_count_.fetch_add(1, std::memory_order_relaxed);
    if (count == 0 || count % 250 == 0) {
        float peak = 0.0f;
        for (int32_t i = 0; i < num_frames; ++i) {
            float v = samples[i] < 0 ? -samples[i] : samples[i];
            if (v > peak) peak = v;
        }
        LOGI("DataCallback #%u: frames=%d peak=%.6f", count, num_frames, peak);
    }

    return AAUDIO_CALLBACK_RESULT_CONTINUE;
}

void AudioCapture::ErrorCallback(
    AAudioStream* /*stream*/, void* user_data, aaudio_result_t error)
{
    auto* self = static_cast<AudioCapture*>(user_data);
    LOGE("AAudio error callback: %s", AAudio_convertResultToText(error));
    self->error_occurred_.store(true, std::memory_order_release);
    self->running_.store(false, std::memory_order_release);
}
