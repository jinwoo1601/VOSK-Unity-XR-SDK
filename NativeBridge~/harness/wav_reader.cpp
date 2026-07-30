// ============================================================================
// Purpose:  Implementation of the strict fixture WAV loader
// Layer:    NativeBridge.Harness
// Owns:     LoadWav (function)
// Depends:  WavData
// ============================================================================

#include "wav_reader.h"

#include <cstdio>
#include <cstring>

namespace {

constexpr uint32_t kRequiredRate     = 48000;
constexpr uint16_t kRequiredChannels = 1;
constexpr uint16_t kRequiredBits     = 16;
constexpr uint16_t kFormatPcm        = 1;

uint16_t ReadU16(const uint8_t* p) {
    return static_cast<uint16_t>(p[0] | (p[1] << 8));
}

uint32_t ReadU32(const uint8_t* p) {
    return static_cast<uint32_t>(p[0]) | (static_cast<uint32_t>(p[1]) << 8) |
           (static_cast<uint32_t>(p[2]) << 16) | (static_cast<uint32_t>(p[3]) << 24);
}

} // namespace

std::string LoadWav(const std::string& path, WavData& out) {
    FILE* f = std::fopen(path.c_str(), "rb");
    if (!f)
        return path + ": cannot open file";

    std::vector<uint8_t> bytes;
    std::fseek(f, 0, SEEK_END);
    long file_len = std::ftell(f);
    std::fseek(f, 0, SEEK_SET);
    if (file_len <= 0) {
        std::fclose(f);
        return path + ": empty or unreadable file";
    }
    bytes.resize(static_cast<size_t>(file_len));
    size_t got = std::fread(bytes.data(), 1, bytes.size(), f);
    std::fclose(f);
    if (got != bytes.size())
        return path + ": short read";

    if (bytes.size() < 12 || std::memcmp(bytes.data(), "RIFF", 4) != 0 ||
        std::memcmp(bytes.data() + 8, "WAVE", 4) != 0)
        return path + ": not a RIFF/WAVE file";

    bool have_fmt = false;
    uint16_t format = 0, channels = 0, bits = 0;
    uint32_t rate = 0;
    const uint8_t* data_ptr = nullptr;
    uint32_t data_len = 0;

    // Walk chunks; guard every size against the remaining bytes so a hostile
    // or truncated size can never index past the buffer.
    size_t pos = 12;
    while (pos + 8 <= bytes.size()) {
        const uint8_t* hdr = bytes.data() + pos;
        uint32_t chunk_size = ReadU32(hdr + 4);
        size_t body = pos + 8;
        if (chunk_size > bytes.size() - body)
            return path + ": truncated chunk (size exceeds file)";

        if (std::memcmp(hdr, "fmt ", 4) == 0) {
            if (chunk_size < 16)
                return path + ": fmt chunk too small";
            const uint8_t* p = bytes.data() + body;
            format   = ReadU16(p + 0);
            channels = ReadU16(p + 2);
            rate     = ReadU32(p + 4);
            bits     = ReadU16(p + 14);
            have_fmt = true;
        } else if (std::memcmp(hdr, "data", 4) == 0) {
            data_ptr = bytes.data() + body;
            data_len = chunk_size;
        }

        pos = body + chunk_size + (chunk_size & 1); // chunks are word-aligned
    }

    if (!have_fmt)
        return path + ": no fmt chunk";
    if (!data_ptr)
        return path + ": no data chunk";

    if (format != kFormatPcm || channels != kRequiredChannels ||
        rate != kRequiredRate || bits != kRequiredBits) {
        char buf[160];
        std::snprintf(buf, sizeof(buf),
                      ": unsupported format (%u ch, %u Hz, %u-bit, fmt %u) — "
                      "required: %u ch, %u Hz, %u-bit PCM",
                      channels, rate, bits, format,
                      kRequiredChannels, kRequiredRate, kRequiredBits);
        return path + buf;
    }

    uint32_t sample_count = data_len / 2;
    out.sample_rate = rate;
    out.samples.resize(sample_count);
    for (uint32_t i = 0; i < sample_count; ++i) {
        int16_t s = static_cast<int16_t>(ReadU16(data_ptr + i * 2));
        out.samples[i] = static_cast<float>(s) / 32768.0f;
    }

    return {};
}
