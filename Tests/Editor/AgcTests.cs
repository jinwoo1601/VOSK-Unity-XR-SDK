using NUnit.Framework;
using VoXR.Dsp;

namespace VoXR.Tests.Editor
{
    public class AgcTests
    {
        const float SampleRate = 16000f;
        const float TargetDb   = -18f;

        static Agc BuildAgc()
        {
            var agc = new Agc();
            agc.Configure(TargetDb, SampleRate);
            return agc;
        }

        [Test]
        public void Configure_ZeroSampleRate_DoesNotProduceNaN()
        {
            var agc = new Agc();
            agc.Configure(TargetDb, 0f);   // should be clamped to 16 kHz fallback

            var samples = new float[100];
            for (int i = 0; i < samples.Length; i++) samples[i] = 0.1f;
            agc.Process(samples, samples.Length);

            for (int i = 0; i < samples.Length; i++)
                Assert.False(float.IsNaN(samples[i]), $"Sample[{i}] became NaN");
        }

        [Test]
        public void Process_SilentInput_LeavesGainAtUnity()
        {
            var agc = BuildAgc();
            var samples = new float[16000];   // 1 second of silence
            agc.Process(samples, samples.Length);

            // With absX == 0 the noise-floor gate fires on every sample, so
            // desiredGain is pinned to currentGain and the smoother never moves
            // it off its field-initializer value of 1.0.
            Assert.AreEqual(1f, agc.CurrentGain, 1e-6f);
        }

        [Test]
        public void Process_LoudSignal_ReducesGainBelowUnity()
        {
            var agc = BuildAgc();
            // Amplitude 0.5 is ~4× the target level (0.126). Gain should drop to ~0.25.
            var samples = new float[16000];
            for (int i = 0; i < samples.Length; i++) samples[i] = 0.5f;
            agc.Process(samples, samples.Length);

            Assert.Less(agc.CurrentGain, 1f,
                "AGC should have reduced gain below 1.0 for a signal 4× above target");
            Assert.GreaterOrEqual(agc.CurrentGain, 0.5f,
                "AGC should not drop below the MinGain floor of 0.5");
        }

        [Test]
        public void Process_QuietSignal_RaisesGainAboveUnity()
        {
            var agc = BuildAgc();
            // Amplitude 0.01 is ~12× below target. Gain should climb toward ~12.6.
            // Gain release coefficient is 300 ms, so 3+ seconds of audio are needed
            // for the smoothed gain to visibly climb.
            var samples = new float[80000];   // 5 seconds at 16 kHz
            for (int i = 0; i < samples.Length; i++) samples[i] = 0.01f;
            agc.Process(samples, samples.Length);

            Assert.Greater(agc.CurrentGain, 1f,
                "AGC should have raised gain above 1.0 for a signal well below target");
        }

        [Test]
        public void Process_ExtremeInput_OutputBoundedBySoftLimiter()
        {
            var agc = BuildAgc();
            // Pathological ±10 input. Even with gain driven to MinGain (0.5),
            // input × gain = ±5, which the fast-tanh soft limiter clamps into (-1, 1).
            var samples = new float[4000];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = (i % 2 == 0) ? 10f : -10f;
            agc.Process(samples, samples.Length);

            for (int i = 0; i < samples.Length; i++)
            {
                Assert.GreaterOrEqual(samples[i], -1f, $"Sample[{i}] below -1");
                Assert.LessOrEqual(samples[i], 1f, $"Sample[{i}] above +1");
            }
        }

        [Test]
        public void Reset_AfterLoudSignal_RestoresGainToUnity()
        {
            var agc = BuildAgc();
            var loud = new float[16000];
            for (int i = 0; i < loud.Length; i++) loud[i] = 0.8f;
            agc.Process(loud, loud.Length);

            // Gain should be below 1.0 after processing a loud signal.
            Assert.Less(agc.CurrentGain, 1f);

            agc.Reset();

            Assert.AreEqual(1f, agc.CurrentGain, 1e-6f,
                "Reset should return currentGain to 1.0");
        }
    }
}
