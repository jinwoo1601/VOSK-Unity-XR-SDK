// ============================================================================
// Purpose:  Strict WAV loader for fixture files — 48 kHz mono 16-bit PCM
//           only, decoded to float [-1,1]; anything else is rejected with a
//           message naming the file and the problem
// Layer:    NativeBridge.Harness
// Owns:     WavData (struct), LoadWav (function)
// Depends:  (none)
// ============================================================================

#ifndef VOSK_HARNESS_WAV_READER_H
#define VOSK_HARNESS_WAV_READER_H

#include <cstdint>
#include <string>
#include <vector>

struct WavData {
    std::vector<float> samples;   // mono, 48 kHz, [-1, 1]
    uint32_t sample_rate = 0;
};

// Loads `path` into `out`. Returns empty string on success, otherwise a
// human-readable error naming the file and the actual-vs-required format.
std::string LoadWav(const std::string& path, WavData& out);

#endif // VOSK_HARNESS_WAV_READER_H
