#ifndef VOSK_BRIDGE_LOGGING_H
#define VOSK_BRIDGE_LOGGING_H

#include <android/log.h>

#define LOG_TAG "vosk-bridge"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

#endif // VOSK_BRIDGE_LOGGING_H
