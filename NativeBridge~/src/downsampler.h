#ifndef DOWNSAMPLER_H
#define DOWNSAMPLER_H

#include <cstdint>
#include <cstring>

// FIR low-pass filter with integer-ratio decimation.
// Designed for 48 kHz -> 16 kHz (factor 3) downsampling.
//
// Uses a 15-tap windowed-sinc filter with a cutoff at ~7.5 kHz
// (Nyquist/2 for the 16 kHz target rate) to anti-alias before decimation.
class Downsampler {
public:
    static constexpr int kDecimationFactor = 3;
    static constexpr int kFilterTaps = 15;

    Downsampler() {
        std::memset(history_, 0, sizeof(history_));
    }

    // Process input samples at 48 kHz, produce output at 16 kHz.
    // output buffer must hold at least (input_count / kDecimationFactor + 1) samples
    // because residual phase from prior calls can produce one extra output.
    // Returns the number of output samples written.
    uint32_t Process(const float* input, uint32_t input_count, float* output) {
        uint32_t out_count = 0;

        for (uint32_t i = 0; i < input_count; ++i) {
            history_[write_pos_] = input[i];
            write_pos_ = (write_pos_ + 1) % kFilterTaps;

            phase_++;
            if (phase_ >= kDecimationFactor) {
                phase_ = 0;

                // Apply FIR filter using circular buffer
                float sum = 0.0f;
                int pos = (write_pos_ - 1 + kFilterTaps) % kFilterTaps;
                for (int j = 0; j < kFilterTaps; ++j) {
                    sum += kCoefficients[j] * history_[pos];
                    pos = (pos - 1 + kFilterTaps) % kFilterTaps;
                }

                output[out_count++] = sum;
            }
        }

        return out_count;
    }

    void Reset() {
        std::memset(history_, 0, sizeof(history_));
        write_pos_ = 0;
        phase_ = 0;
    }

private:
    // 15-tap FIR low-pass filter coefficients.
    // Windowed-sinc design: cutoff at 1/6 of sample rate (8 kHz at 48 kHz),
    // which gives ~7.5 kHz passband with transition band rolling off before
    // the 8 kHz Nyquist of the 16 kHz output. Hamming window applied.
    //
    // Symmetric coefficients: kCoefficients[i] == kCoefficients[14-i]
    static constexpr float kCoefficients[kFilterTaps] = {
        -0.0019f,  0.0000f,  0.0178f,  0.0536f,  0.1128f,
         0.1714f,  0.2074f,  0.2136f,  0.2074f,  0.1714f,
         0.1128f,  0.0536f,  0.0178f,  0.0000f, -0.0019f,
    };

    float history_[kFilterTaps] = {};
    int write_pos_ = 0;
    int phase_ = 0;
};

#endif // DOWNSAMPLER_H
