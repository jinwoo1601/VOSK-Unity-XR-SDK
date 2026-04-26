// ============================================================================
// Purpose:  Automatic gain control with soft-limiter for 16kHz speech normalization
// Layer:    Runtime.Dsp
// Owns:     Agc (internal sealed class)
// Depends:  (none)
// ============================================================================
using System;

namespace VoXR.Dsp
{
    internal sealed class Agc
    {
        public const float DefaultTargetDb = -18f;

        // Level tracker — fast attack (catches onsets), slow release (holds
        // through brief pauses so gain doesn't pump between words).
        const float LevelAttackMs  =   5f;
        const float LevelReleaseMs = 200f;

        // Gain smoother — fast attack (reduce gain quickly when signal gets
        // louder to avoid saturating tanh), slow release (raise gain slowly
        // when signal gets quieter to avoid amplifying noise in pauses).
        const float GainAttackMs  =  10f;
        const float GainReleaseMs = 300f;

        const float NoiseFloor   = 1e-5f;          // ~-100 dBFS
        const float MinGain      = 0.5f;
        const float MaxGain      = 20f;
        const float DefaultLevel = 0.125892541f;   // DbToLinear(-18)

        float _targetLevel   = DefaultLevel;
        float _smoothedLevel = DefaultLevel;
        float _currentGain   = 1f;

        float _levelAttackCoeff;
        float _levelReleaseCoeff;
        float _gainAttackCoeff;
        float _gainReleaseCoeff;

        public void Configure(float targetDb, float sampleRate)
        {
            if (sampleRate <= 0f) sampleRate = 16000f;
            _targetLevel = DbToLinear(targetDb);

            _levelAttackCoeff  = EmaCoeff(sampleRate, LevelAttackMs);
            _levelReleaseCoeff = EmaCoeff(sampleRate, LevelReleaseMs);
            _gainAttackCoeff   = EmaCoeff(sampleRate, GainAttackMs);
            _gainReleaseCoeff  = EmaCoeff(sampleRate, GainReleaseMs);
        }

        public void Process(float[] samples, int count)
        {
            for (int i = 0; i < count; i++)
            {
                float x = samples[i];
                float absX = Math.Abs(x);

                float lc = (absX > _smoothedLevel) ? _levelAttackCoeff : _levelReleaseCoeff;
                _smoothedLevel += lc * (absX - _smoothedLevel);

                // Gate on the current input rather than the smoothed level so that
                // pure silence holds the gain in place without waiting for the
                // envelope follower to drain below NoiseFloor. See divergence note
                // in the class summary.
                float desiredGain;
                if (absX < NoiseFloor)
                {
                    desiredGain = _currentGain;
                }
                else
                {
                    desiredGain = _targetLevel / _smoothedLevel;
                    if (desiredGain < MinGain) desiredGain = MinGain;
                    if (desiredGain > MaxGain) desiredGain = MaxGain;
                }

                float gc = (desiredGain < _currentGain) ? _gainAttackCoeff : _gainReleaseCoeff;
                _currentGain += gc * (desiredGain - _currentGain);

                samples[i] = FastTanh(x * _currentGain);
            }
        }

        public void Reset()
        {
            _smoothedLevel = _targetLevel;
            _currentGain   = 1f;
        }

        // Current gain value, exposed for tests and diagnostics.
        internal float CurrentGain => _currentGain;

        static float DbToLinear(float db)
        {
            return MathF.Pow(10f, db / 20f);
        }

        // Per-sample EMA coefficient from a time constant in milliseconds.
        static float EmaCoeff(float sampleRate, float timeMs)
        {
            return 1f - MathF.Exp(-1000f / (sampleRate * timeMs));
        }

        // Rational approximation of tanh, max error ~0.0002 for |x| <= 4.
        // Beyond |x|=4 tanh > 0.999 so clamping is inaudible.
        static float FastTanh(float x)
        {
            if (x >  4f) return  1f;
            if (x < -4f) return -1f;
            float x2 = x * x;
            return x * (27f + x2) / (27f + 9f * x2);
        }
    }
}
