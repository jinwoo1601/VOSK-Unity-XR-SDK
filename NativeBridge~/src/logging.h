#ifndef VOSK_BRIDGE_LOGGING_H
#define VOSK_BRIDGE_LOGGING_H

#define LOG_TAG "vosk-bridge"

#if defined(__ANDROID__)

#include <android/log.h>

#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

#else // Desktop fallback: plain stdio, no liblog dependency.
      // Both levels go to stderr — stdout belongs to tool output (the
      // harness prints its JSON report there).

#include <cstdio>

#define LOGI(...) (std::fprintf(stderr, "[" LOG_TAG "] "), std::fprintf(stderr, __VA_ARGS__), std::fprintf(stderr, "\n"))
#define LOGE(...) (std::fprintf(stderr, "[" LOG_TAG "] "), std::fprintf(stderr, __VA_ARGS__), std::fprintf(stderr, "\n"))

#endif

#endif // VOSK_BRIDGE_LOGGING_H
