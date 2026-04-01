#include "vosk_bridge.h"
#include "vosk_api.h"
#include "ring_buffer.h"
#include "downsampler.h"
#include "agc.h"
#include "result_queue.h"
#include "audio_capture_audiorecord.h"

#include <thread>
#include <atomic>
#include <string>
#include <cstring>
#include <chrono>
#include "logging.h"

// --- Static global state (singleton bridge) ---

static VoskModel*       g_model       = nullptr;
static VoskRecognizer*  g_recognizer  = nullptr;
static float            g_sample_rate = 16000.0f;

static RingBuffer<float> g_ring_buffer;
static ResultQueue       g_result_queue;
static AudioCapture      g_audio_capture;
static Downsampler       g_downsampler;
static Agc               g_agc;

static std::thread       g_recognition_thread;
static std::atomic<bool> g_running{false};
static std::atomic<bool> g_initialised{false};

static std::string       g_last_error;
static std::string       g_last_partial;
static QueuedResult      g_current_result;

// Processing buffer sizes
static constexpr uint32_t kReadChunkSize = 4096;       // 48 kHz samples per read (~85 ms)
static constexpr uint32_t kDownsampledSize = kReadChunkSize / Downsampler::kDecimationFactor + 1;

// Convert float [-1,1] to int16 for vosk_recognizer_accept_waveform_s.
// The float variant of the VOSK API does not work reliably on all arm64
// builds of libvosk, so we use the int16 path instead.
static void float_to_int16(const float* src, short* dst, uint32_t count) {
    for (uint32_t i = 0; i < count; ++i) {
        float v = src[i] * 32767.0f;
        if (v > 32767.0f) v = 32767.0f;
        else if (v < -32768.0f) v = -32768.0f;
        dst[i] = static_cast<short>(v);
    }
}

static void reset_pipeline() {
    g_ring_buffer.Reset();
    g_result_queue.Clear();
    g_downsampler.Reset();
    g_agc.Reset();
    g_last_partial.clear();
}

// --- Recognition thread ---

static void recognition_loop() {
    float read_buf[kReadChunkSize];
    float downsampled_buf[kDownsampledSize];
    short int16_buf[kDownsampledSize];

    LOGI("Recognition thread started");

    while (g_running.load(std::memory_order_acquire)) {
        if (g_audio_capture.HasError()) {
            LOGE("Audio capture error detected, stopping recognition");
            g_result_queue.Push(
                std::string("{\"error\": \"audio device error\", \"code\": ")
                    + std::to_string(VOSK_BRIDGE_ERR_AUDIO_DEVICE_UNAVAIL) + "}",
                false);
            break;
        }

        uint32_t read_count = g_ring_buffer.Read(read_buf, kReadChunkSize);

        if (read_count == 0) {
            std::this_thread::sleep_for(std::chrono::milliseconds(10));
            continue;
        }

        // Downsample 48 kHz -> 16 kHz
        uint32_t ds_count = g_downsampler.Process(read_buf, read_count, downsampled_buf);

        // Automatic gain control + soft saturation
        g_agc.Process(downsampled_buf, ds_count);

        // Convert to int16 for VOSK
        float_to_int16(downsampled_buf, int16_buf, ds_count);

        if (ds_count > 0) {
            int result = vosk_recognizer_accept_waveform_s(
                g_recognizer, int16_buf, static_cast<int>(ds_count));

            if (result == 1) {
                // Utterance boundary — final result
                const char* json = vosk_recognizer_result(g_recognizer);
                if (json)
                    g_result_queue.Push(std::string(json), true);
                else
                    LOGE("vosk_recognizer_result returned NULL");
            } else {
                const char* json = vosk_recognizer_partial_result(g_recognizer);
                if (json && json != g_last_partial) {
                    g_last_partial = json;
                    g_result_queue.Push(std::string(json), false);
                }
            }
        }

        // Check for ring buffer overflow
        if (g_ring_buffer.CheckOverflow()) {
            g_result_queue.Push(
                std::string("{\"error\": \"ring buffer overflow\", \"code\": ")
                    + std::to_string(VOSK_BRIDGE_ERR_RING_BUFFER_OVERFLOW) + "}",
                false);
            LOGI("Ring buffer overflow detected");
        }
    }

    // Ensure g_running is false so vosk_bridge_is_running() returns 0 to C#
    g_running.store(false, std::memory_order_release);

    // Flush remaining audio as final result
    const char* final_json = vosk_recognizer_final_result(g_recognizer);
    if (final_json)
        g_result_queue.Push(std::string(final_json), true);
    else
        LOGE("vosk_recognizer_final_result returned NULL");

    LOGI("Recognition thread exiting");
}

// --- Bridge C API implementation ---

extern "C" {

int vosk_bridge_init(const char* model_path, float sample_rate,
                     float mic_gain_target_db) {
    g_last_error.clear();

    if (g_initialised.load(std::memory_order_acquire))
        return VOSK_BRIDGE_ERR_ALREADY_INITIALISED;

    vosk_set_log_level(-1);  // Suppress VOSK internal logging

    g_model = vosk_model_new(model_path);
    if (!g_model) {
        g_last_error = "vosk_model_new() returned NULL for path: " + std::string(model_path);
        LOGE("%s", g_last_error.c_str());
        return VOSK_BRIDGE_ERR_MODEL_LOAD_FAILED;
    }

    g_sample_rate = sample_rate;
    g_recognizer = vosk_recognizer_new(g_model, g_sample_rate);
    if (!g_recognizer) {
        g_last_error = "vosk_recognizer_new() returned NULL";
        LOGE("%s", g_last_error.c_str());
        vosk_model_free(g_model);
        g_model = nullptr;
        return VOSK_BRIDGE_ERR_MODEL_LOAD_FAILED;
    }

    g_agc.Configure(mic_gain_target_db, g_sample_rate);

    reset_pipeline();

    g_initialised.store(true, std::memory_order_release);
    LOGI("Bridge initialised: model=%s, sample_rate=%.0f, agc_target=%.1f dB",
         model_path, sample_rate, mic_gain_target_db);
    return VOSK_BRIDGE_OK;
}

void vosk_bridge_destroy() {
    if (!g_initialised.load(std::memory_order_acquire))
        return;

    g_running.store(false, std::memory_order_release);
    g_audio_capture.Stop();
    if (g_recognition_thread.joinable())
        g_recognition_thread.join();

    if (g_recognizer) {
        vosk_recognizer_free(g_recognizer);
        g_recognizer = nullptr;
    }

    if (g_model) {
        vosk_model_free(g_model);
        g_model = nullptr;
    }

    reset_pipeline();

    g_initialised.store(false, std::memory_order_release);
    LOGI("Bridge destroyed");
}

int vosk_bridge_start() {
    g_last_error.clear();

    if (!g_initialised.load(std::memory_order_acquire))
        return VOSK_BRIDGE_ERR_NOT_INITIALISED;

    if (g_running.load(std::memory_order_acquire))
        return VOSK_BRIDGE_ERR_ALREADY_RUNNING;

    reset_pipeline();

    // Start audio capture
    int audio_result = g_audio_capture.Start(&g_ring_buffer);
    if (audio_result != VOSK_BRIDGE_OK) {
        g_last_error = "Failed to start audio capture";
        return audio_result;
    }

    // Launch recognition thread
    g_running.store(true, std::memory_order_release);
    g_recognition_thread = std::thread(recognition_loop);

    LOGI("Recognition started");
    return VOSK_BRIDGE_OK;
}

void vosk_bridge_stop() {
    g_running.store(false, std::memory_order_release);
    g_audio_capture.Stop();

    if (g_recognition_thread.joinable())
        g_recognition_thread.join();

    LOGI("Recognition stopped");
}

int vosk_bridge_reset() {
    g_last_error.clear();

    if (!g_initialised.load(std::memory_order_acquire))
        return VOSK_BRIDGE_ERR_NOT_INITIALISED;

    bool was_running = g_running.load(std::memory_order_acquire);
    if (was_running)
        vosk_bridge_stop();

    vosk_recognizer_reset(g_recognizer);

    if (was_running) {
        int restart = vosk_bridge_start();
        if (restart != VOSK_BRIDGE_OK)
            return restart;
    }

    return VOSK_BRIDGE_OK;
}

int vosk_bridge_has_result() {
    return g_result_queue.HasResult() ? 1 : 0;
}

const char* vosk_bridge_get_result(int* out_is_final) {
    if (!g_result_queue.Pop(g_current_result))
        return nullptr;
    if (out_is_final)
        *out_is_final = g_current_result.is_final ? 1 : 0;
    return g_current_result.json.c_str();
}

int vosk_bridge_is_running() {
    return g_running.load(std::memory_order_acquire) ? 1 : 0;
}

int vosk_bridge_is_initialised() {
    return g_initialised.load(std::memory_order_acquire) ? 1 : 0;
}

int vosk_bridge_get_error(char* buf, int buf_size) {
    if (!buf || buf_size <= 0)
        return 0;

    int len = static_cast<int>(g_last_error.size());
    int copy_len = (len < buf_size - 1) ? len : buf_size - 1;
    std::memcpy(buf, g_last_error.c_str(), copy_len);
    buf[copy_len] = '\0';
    return copy_len;
}

} // extern "C"
