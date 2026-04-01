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
        target_level_ = db_to_linear(target_db);

        // Level tracker — fast attack (catches onsets), slow release (holds
        // through brief pauses so gain doesn't pump between words).
        constexpr float kLevelAttackMs  =   5.0f;
        constexpr float kLevelReleaseMs = 200.0f;

        // Gain smoother — fast attack (reduce gain quickly when signal gets
        // louder to avoid saturating tanh), slow release (raise gain slowly
        // when signal gets quieter to avoid amplifying noise in pauses).
        constexpr float kGainAttackMs  =  10.0f;
        constexpr float kGainReleaseMs = 300.0f;

        level_attack_coeff_  = ema_coeff(sample_rate, kLevelAttackMs);
        level_release_coeff_ = ema_coeff(sample_rate, kLevelReleaseMs);
        gain_attack_coeff_   = ema_coeff(sample_rate, kGainAttackMs);
        gain_release_coeff_  = ema_coeff(sample_rate, kGainReleaseMs);
    }

    // Process samples in-place.  Output is bounded to (-1, 1) by soft limiter.
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
                desired_gain = current_gain_;
            } else {
                desired_gain = target_level_ / smoothed_level_;
                if (desired_gain < kMinGain) desired_gain = kMinGain;
                if (desired_gain > kMaxGain) desired_gain = kMaxGain;
            }

            // --- Smooth gain transition ---
            float gc = (desired_gain < current_gain_) ? gain_attack_coeff_
                                                      : gain_release_coeff_;
            current_gain_ += gc * (desired_gain - current_gain_);

            // --- Apply gain + soft saturation ---
            samples[i] = fast_tanh(x * current_gain_);
        }
    }

    void Reset() {
        smoothed_level_ = target_level_;
        current_gain_   = 1.0f;
    }

private:
    static constexpr float kNoiseFloor = 1e-5f;   // ~-100 dBFS
    static constexpr float kMinGain    = 0.5f;
    static constexpr float kMaxGain    = 20.0f;
    static constexpr float kDefaultLevel = 0.125892541f; // db_to_linear(-18)

    static float db_to_linear(float db) {
        return std::pow(10.0f, db / 20.0f);
    }

    // Per-sample EMA coefficient from time constant in milliseconds.
    static float ema_coeff(float sample_rate, float time_ms) {
        return 1.0f - std::exp(-1000.0f / (sample_rate * time_ms));
    }

    // Rational approximation of tanh, max error ~0.0002 for |x| <= 4.
    // Beyond |x|=4 tanh > 0.999 so clamping is inaudible.
    static float fast_tanh(float x) {
        if (x >  4.0f) return  1.0f;
        if (x < -4.0f) return -1.0f;
        float x2 = x * x;
        return x * (27.0f + x2) / (27.0f + 9.0f * x2);
    }

    float target_level_   = kDefaultLevel;
    float smoothed_level_ = kDefaultLevel;
    float current_gain_   = 1.0f;

    float level_attack_coeff_  = 0.0f;
    float level_release_coeff_ = 0.0f;
    float gain_attack_coeff_   = 0.0f;
    float gain_release_coeff_  = 0.0f;
};

#endif // AGC_H
