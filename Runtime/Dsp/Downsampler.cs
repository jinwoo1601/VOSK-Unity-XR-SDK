using System;

namespace VoskXR.Dsp
{
    /// <summary>
    /// FIR low-pass filter with integer-ratio decimation.
    /// Designed for 48 kHz -> 16 kHz (factor 3) downsampling.
    ///
    /// Uses a 15-tap windowed-sinc filter with a cutoff at ~7.5 kHz
    /// (Nyquist/2 for the 16 kHz target rate) to anti-alias before decimation.
    ///
    /// C# port of <c>NativeBridge~/src/downsampler.h</c> — keep the coefficients
    /// and state layout in sync if the C++ version ever changes.
    /// </summary>
    internal sealed class Downsampler
    {
        public const int DecimationFactor = 3;
        public const int FilterTaps = 15;

        // 15-tap FIR low-pass filter coefficients.
        // Windowed-sinc design: cutoff at 1/6 of sample rate (8 kHz at 48 kHz),
        // which gives ~7.5 kHz passband with transition band rolling off before
        // the 8 kHz Nyquist of the 16 kHz output. Hamming window applied.
        //
        // Symmetric coefficients: Coefficients[i] == Coefficients[14-i]
        static readonly float[] Coefficients =
        {
            -0.0019f,  0.0000f,  0.0178f,  0.0536f,  0.1128f,
             0.1714f,  0.2074f,  0.2136f,  0.2074f,  0.1714f,
             0.1128f,  0.0536f,  0.0178f,  0.0000f, -0.0019f,
        };

        readonly float[] _history = new float[FilterTaps];
        int _writePos;
        int _phase;

        /// <summary>
        /// Processes input samples at the source rate, producing output samples
        /// at source/<see cref="DecimationFactor"/>. The output buffer must hold
        /// at least <c>inputCount / DecimationFactor + 1</c> samples because
        /// residual phase from prior calls can produce one extra output.
        /// </summary>
        /// <returns>The number of output samples written.</returns>
        public int Process(float[] input, int inputCount, float[] output)
        {
            int outCount = 0;

            for (int i = 0; i < inputCount; i++)
            {
                _history[_writePos] = input[i];
                _writePos++;
                if (_writePos == FilterTaps) _writePos = 0;

                _phase++;
                if (_phase >= DecimationFactor)
                {
                    _phase = 0;

                    float sum = 0f;
                    int pos = _writePos == 0 ? FilterTaps - 1 : _writePos - 1;
                    for (int j = 0; j < FilterTaps; j++)
                    {
                        sum += Coefficients[j] * _history[pos];
                        pos = pos == 0 ? FilterTaps - 1 : pos - 1;
                    }

                    output[outCount++] = sum;
                }
            }

            return outCount;
        }

        public void Reset()
        {
            Array.Clear(_history, 0, _history.Length);
            _writePos = 0;
            _phase = 0;
        }
    }
}
