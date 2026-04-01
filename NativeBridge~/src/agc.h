#ifndef AGC_H
#define AGC_H

#include <cmath>
#include <cstdint>

// Automatic Gain Control with soft saturation.
//
// Tracks signal level per-sample with asymmetric attack/release smoothing,
// computes a gain to reach a configurable target level, and applies it with
// per-sample interpolation to avoid clicks.  A tanh soft limiter replaces
// hard clipping to prevent harmonic distortion.
//
// Designed for the 16 kHz downsampled speech path before VOSK ingestion.
class Agc {
public:
    static constexpr float kDefaultTargetDb = -18.0f;   // dBFS

    void Configure(float target_db, float sample_rate) {
        if (sample_rate <= 0.0f) sample_rate = 16000.0f;
        target_level_ = std::pow(10.0f, target_db / 20.0f);

        // Level tracker — fast attack (catches onsets), slow release (holds
        // through brief pauses so gain doesn't pump between words).
        constexpr float kLevelAttackMs  =   5.0f;
        constexpr float kLevelReleaseMs = 200.0f;

        // Gain smoother — fast attack (reduce gain quickly when signal gets
        // louder to avoid saturating tanh), slow release (raise gain slowly
        // when signal gets quieter to avoid amplifying noise in pauses).
        constexpr float kGainAttackMs  =  10.0f;
        constexpr float kGainReleaseMs = 300.0f;

        level_attack_coeff_  = coeff(sample_rate, kLevelAttackMs);
        level_release_coeff_ = coeff(sample_rate, kLevelReleaseMs);
        gain_attack_coeff_   = coeff(sample_rate, kGainAttackMs);
        gain_release_coeff_  = coeff(sample_rate, kGainReleaseMs);

        Reset();
    }

    // Process samples in-place.  Output is bounded to (-1, 1) by tanh.
    void Process(float* samples, uint32_t count) {
        for (uint32_t i = 0; i < count; ++i) {
            float x = samples[i];
            float abs_x = std::fabs(x);

            // --- Track signal level (smoothed absolute value) ---
            float lc = (abs_x > smoothed_level_) ? level_attack_coeff_
                                                  : level_release_coeff_;
            smoothed_level_ += lc * (abs_x - smoothed_level_);

            // --- Compute desired gain ---
            float desired_gain;
            if (smoothed_level_ < kNoiseFloor) {
                // Signal is silence / noise — hold current gain to avoid
                // boosting the noise floor.
                desired_gain = current_gain_;
            } else {
                desired_gain = target_level_ / smoothed_level_;
                if (desired_gain < kMinGain) desired_gain = kMinGain;
                if (desired_gain > kMaxGain) desired_gain = kMaxGain;
            }

            // --- Smooth gain transition ---
            // Attack = gain decreasing (signal got louder).
            // Release = gain increasing (signal got quieter).
            float gc = (desired_gain < current_gain_) ? gain_attack_coeff_
                                                      : gain_release_coeff_;
            current_gain_ += gc * (desired_gain - current_gain_);

            // --- Apply gain + soft saturation ---
            samples[i] = std::tanh(x * current_gain_);
        }
    }

    void Reset() {
        smoothed_level_ = target_level_;   // Assume signal starts near target
        current_gain_   = 1.0f;            // Unity gain until we measure
    }

private:
    static constexpr float kNoiseFloor = 1e-5f;   // ~-100 dBFS
    static constexpr float kMinGain    = 0.5f;
    static constexpr float kMaxGain    = 20.0f;

    // Per-sample EMA coefficient from time constant in milliseconds.
    static float coeff(float sample_rate, float time_ms) {
        return 1.0f - std::exp(-1000.0f / (sample_rate * time_ms));
    }

    float target_level_ = 0.125f;    // -18 dBFS
    float smoothed_level_ = 0.125f;
    float current_gain_ = 1.0f;

    float level_attack_coeff_  = 0.0f;
    float level_release_coeff_ = 0.0f;
    float gain_attack_coeff_   = 0.0f;
    float gain_release_coeff_  = 0.0f;
};

#endif // AGC_H
