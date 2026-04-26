using NUnit.Framework;
using VoXR.Dsp;

namespace VoXR.Tests.Editor
{
    public class DownsamplerTests
    {
        [Test]
        public void Process_EmptyInput_ReturnsZero()
        {
            var ds = new Downsampler();
            var input = new float[0];
            var output = new float[8];

            int count = ds.Process(input, 0, output);

            Assert.AreEqual(0, count);
        }

        [Test]
        public void Process_48Samples_Produces16Outputs()
        {
            // Phase starts at 0. Outputs occur when _phase reaches DecimationFactor (3).
            // Pattern: i=2, 5, 8, ..., 47 → exactly 16 output samples.
            var ds = new Downsampler();
            var input = new float[48];
            for (int i = 0; i < input.Length; i++) input[i] = 1f;
            var output = new float[48];

            int count = ds.Process(input, input.Length, output);

            Assert.AreEqual(16, count);
        }

        [Test]
        public void Process_AllZeros_ProducesAllZeros()
        {
            var ds = new Downsampler();
            var input = new float[300];       // 300 zero samples
            var output = new float[150];

            int count = ds.Process(input, input.Length, output);

            Assert.AreEqual(100, count);
            for (int i = 0; i < count; i++)
                Assert.AreEqual(0f, output[i], 1e-9f, $"Output[{i}] should be zero");
        }

        [Test]
        public void Reset_ClearsHistory_SubsequentZerosProduceZeros()
        {
            var ds = new Downsampler();
            var nonzero = new float[60];
            for (int i = 0; i < nonzero.Length; i++) nonzero[i] = 0.5f;
            var scratch = new float[60];
            ds.Process(nonzero, nonzero.Length, scratch);

            ds.Reset();

            var zeros = new float[60];
            var output = new float[60];
            int count = ds.Process(zeros, zeros.Length, output);

            Assert.AreEqual(20, count);
            for (int i = 0; i < count; i++)
                Assert.AreEqual(0f, output[i], 1e-9f,
                    $"Output[{i}] should be zero after Reset — history not cleared");
        }

        [Test]
        public void Process_DcInput_ProducesDcGainApproximatelyUnity()
        {
            // DC gain of an FIR is the sum of its coefficients. For this 15-tap
            // design the sum is ~1.336 — the coefficients are not perfectly
            // normalised but are kept verbatim to stay bit-identical with the
            // Android C++ source (NativeBridge~/src/downsampler.h). The AGC stage
            // downstream compensates for this constant pre-gain, so the steady-
            // state DC output sits around 1.336 rather than 1.0. See risk R4 in
            // v3.1-editor-mic-plan.md.
            //
            // We need at least FilterTaps * DecimationFactor = 45 samples to fully
            // prime the history buffer before sampling a "steady state" output.
            var ds = new Downsampler();
            var input = new float[300];
            for (int i = 0; i < input.Length; i++) input[i] = 1f;
            var output = new float[150];

            int count = ds.Process(input, input.Length, output);

            Assert.AreEqual(100, count);
            // Later outputs (after the filter primes) should hit steady state
            // at ~1.336 (the sum of the 15 coefficients).
            float steady = output[count - 1];
            Assert.Greater(steady, 1.25f, "DC gain unexpectedly low");
            Assert.Less(steady, 1.40f, "DC gain unexpectedly high");
        }

        [Test]
        public void Process_PhaseStatePersistsAcrossCalls()
        {
            // Feed 2 samples (phase=2, no output), then 1 more (phase=3, 1 output).
            var ds = new Downsampler();
            var output = new float[4];

            int countA = ds.Process(new float[] { 1f, 1f }, 2, output);
            Assert.AreEqual(0, countA, "2 samples should produce no output from a fresh Downsampler");

            int countB = ds.Process(new float[] { 1f }, 1, output);
            Assert.AreEqual(1, countB, "Third sample should flush one output because phase state persisted");
        }
    }
}
