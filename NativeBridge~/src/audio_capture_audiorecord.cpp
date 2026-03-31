#include "audio_capture_audiorecord.h"
#include "vosk_bridge.h"
#include "logging.h"

#include <algorithm>

static JavaVM* g_jvm = nullptr;

// Sample rate for AudioRecord — Quest 3 mic natively runs at 48 kHz.
static constexpr int kSampleRate = 48000;
static constexpr int kChannelCount = 1;
// Read ~20 ms of audio per iteration (960 frames at 48 kHz).
static constexpr int kReadFrames = 960;

void AudioCapture_SetJavaVM(JavaVM* vm) {
    g_jvm = vm;
}

// JNI_OnLoad is called automatically when the .so is loaded.
extern "C" JNIEXPORT jint JNI_OnLoad(JavaVM* vm, void* /*reserved*/) {
    AudioCapture_SetJavaVM(vm);
    LOGI("JNI_OnLoad: JavaVM captured");
    return JNI_VERSION_1_6;
}

// Helper: attach current thread to JVM, returns JNIEnv.
static JNIEnv* AttachThread(JavaVM* jvm) {
    JNIEnv* env = nullptr;
    if (jvm->GetEnv(reinterpret_cast<void**>(&env), JNI_VERSION_1_6) == JNI_OK)
        return env;
    if (jvm->AttachCurrentThread(&env, nullptr) == JNI_OK)
        return env;
    return nullptr;
}

int AudioCapture::Start(RingBuffer<float>* ring_buffer) {
    if (running_.load(std::memory_order_acquire))
        return VOSK_BRIDGE_ERR_ALREADY_RUNNING;

    if (!g_jvm) {
        LOGE("AudioCapture: JavaVM not set");
        return VOSK_BRIDGE_ERR_AUDIO_DEVICE_UNAVAIL;
    }

    ring_buffer_ = ring_buffer;
    error_occurred_.store(false, std::memory_order_release);

    JNIEnv* env = AttachThread(g_jvm);
    if (!env) {
        LOGE("AudioCapture: Failed to attach to JVM");
        return VOSK_BRIDGE_ERR_AUDIO_DEVICE_UNAVAIL;
    }

    // android.media.AudioRecord constants
    // ENCODING_PCM_FLOAT = 4
    // CHANNEL_IN_MONO = 16
    // AudioSource.VOICE_RECOGNITION = 6
    jclass arClass = env->FindClass("android/media/AudioRecord");
    if (!arClass) {
        LOGE("AudioCapture: Failed to find AudioRecord class");
        return VOSK_BRIDGE_ERR_AUDIO_DEVICE_UNAVAIL;
    }

    // Get minimum buffer size
    jmethodID getMinBufSize = env->GetStaticMethodID(arClass, "getMinBufferSize", "(III)I");
    jint minBufSize = env->CallStaticIntMethod(arClass, getMinBufSize,
        kSampleRate, 16 /*CHANNEL_IN_MONO*/, 4 /*ENCODING_PCM_FLOAT*/);

    if (minBufSize <= 0) {
        LOGE("AudioCapture: getMinBufferSize returned %d", minBufSize);
        return VOSK_BRIDGE_ERR_AUDIO_DEVICE_UNAVAIL;
    }

    // Use at least 2x min buffer for safety
    jint bufferSize = std::max(minBufSize * 2, kReadFrames * 4 * 4);

    LOGI("AudioCapture: minBufSize=%d, using bufferSize=%d", minBufSize, bufferSize);

    // Construct AudioRecord(audioSource, sampleRate, channelConfig, audioFormat, bufferSizeInBytes)
    jmethodID ctor = env->GetMethodID(arClass, "<init>", "(IIIII)V");
    jobject localRecord = env->NewObject(arClass, ctor,
        6,             // AudioSource.VOICE_RECOGNITION
        kSampleRate,
        16,            // CHANNEL_IN_MONO
        4,             // ENCODING_PCM_FLOAT
        bufferSize);

    if (env->ExceptionCheck()) {
        env->ExceptionDescribe();
        env->ExceptionClear();
        LOGE("AudioCapture: AudioRecord constructor threw exception");
        return VOSK_BRIDGE_ERR_AUDIO_DEVICE_UNAVAIL;
    }

    if (!localRecord) {
        LOGE("AudioCapture: AudioRecord construction returned null");
        return VOSK_BRIDGE_ERR_AUDIO_DEVICE_UNAVAIL;
    }

    // Check state — must be STATE_INITIALIZED (1)
    jmethodID getState = env->GetMethodID(arClass, "getState", "()I");
    jint state = env->CallIntMethod(localRecord, getState);
    if (state != 1) {
        LOGE("AudioCapture: AudioRecord state=%d (expected 1=INITIALIZED)", state);
        env->DeleteLocalRef(localRecord);
        return VOSK_BRIDGE_ERR_AUDIO_DEVICE_UNAVAIL;
    }

    // Start recording
    jmethodID startMethod = env->GetMethodID(arClass, "startRecording", "()V");
    env->CallVoidMethod(localRecord, startMethod);

    if (env->ExceptionCheck()) {
        env->ExceptionDescribe();
        env->ExceptionClear();
        LOGE("AudioCapture: startRecording threw exception");
        env->DeleteLocalRef(localRecord);
        return VOSK_BRIDGE_ERR_AUDIO_DEVICE_UNAVAIL;
    }

    // Make a global reference so it survives across threads
    audio_record_ = env->NewGlobalRef(localRecord);
    env->DeleteLocalRef(localRecord);

    running_.store(true, std::memory_order_release);
    read_thread_ = std::thread(&AudioCapture::ReadLoop, this, g_jvm);

    LOGI("AudioCapture: AudioRecord started at %d Hz, float32, mono", kSampleRate);
    return VOSK_BRIDGE_OK;
}

void AudioCapture::Stop() {
    bool was_running = running_.exchange(false, std::memory_order_acq_rel);

    if (read_thread_.joinable())
        read_thread_.join();

    if (audio_record_ && g_jvm) {
        JNIEnv* env = AttachThread(g_jvm);
        if (env) {
            jclass arClass = env->FindClass("android/media/AudioRecord");
            if (arClass) {
                jmethodID stopMethod = env->GetMethodID(arClass, "stop", "()V");
                jmethodID releaseMethod = env->GetMethodID(arClass, "release", "()V");
                if (was_running)
                    env->CallVoidMethod(audio_record_, stopMethod);
                env->CallVoidMethod(audio_record_, releaseMethod);
            }
            env->DeleteGlobalRef(audio_record_);
            audio_record_ = nullptr;
        }
    }

    LOGI("AudioCapture: stopped");
}

void AudioCapture::ReadLoop(JavaVM* jvm) {
    JNIEnv* env = nullptr;
    bool attached = false;

    if (jvm->GetEnv(reinterpret_cast<void**>(&env), JNI_VERSION_1_6) != JNI_OK) {
        if (jvm->AttachCurrentThread(&env, nullptr) != JNI_OK) {
            LOGE("AudioCapture: ReadLoop failed to attach to JVM");
            error_occurred_.store(true, std::memory_order_release);
            running_.store(false, std::memory_order_release);
            return;
        }
        attached = true;
    }

    LOGI("AudioCapture: ReadLoop started");

    jclass arClass = env->FindClass("android/media/AudioRecord");
    // read(float[] audioData, int offsetInFloats, int sizeInFloats, int readMode)
    // READ_BLOCKING = 0
    jmethodID readMethod = env->GetMethodID(arClass, "read", "([FIII)I");
    jfloatArray jbuf = env->NewFloatArray(kReadFrames);

    while (running_.load(std::memory_order_acquire)) {
        jint framesRead = env->CallIntMethod(audio_record_, readMethod,
            jbuf, 0, kReadFrames, 0 /*READ_BLOCKING*/);

        if (framesRead < 0) {
            LOGE("AudioCapture: read() returned error %d", framesRead);
            error_occurred_.store(true, std::memory_order_release);
            break;
        }

        if (framesRead == 0)
            continue;

        float* samples = env->GetFloatArrayElements(jbuf, nullptr);
        ring_buffer_->Write(samples, static_cast<uint32_t>(framesRead));
        env->ReleaseFloatArrayElements(jbuf, samples, JNI_ABORT);
    }

    env->DeleteLocalRef(jbuf);
    running_.store(false, std::memory_order_release);

    if (attached)
        jvm->DetachCurrentThread();

    LOGI("AudioCapture: ReadLoop exiting");
}
