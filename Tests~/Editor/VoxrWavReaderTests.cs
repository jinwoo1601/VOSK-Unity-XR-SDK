// ============================================================================
// Purpose:  Unit tests for VoxrWavReader byte-level WAV parsing and format gating
// Layer:    Tests.Editor
// Owns:     VoxrWavReaderTests (public class)
// Depends:  VoxrWavReader
// ============================================================================
using System.IO;
using NUnit.Framework;
using VoXR.Testing;

namespace VoXR.Tests.Editor
{
    public class VoxrWavReaderTests
    {
        // --- WAV byte builder -------------------------------------------------

        static byte[] BuildWav(
            int sampleRate = 48000,
            int channels = 1,
            int bitsPerSample = 16,
            short[] samples = null,
            int audioFormat = 1,
            bool junkChunkBeforeFmt = false,
            int dataSizeOverride = -1,
            bool omitData = false
        )
        {
            samples = samples ?? new short[] { 0, 100, -100 };
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write((byte)'R');
                w.Write((byte)'I');
                w.Write((byte)'F');
                w.Write((byte)'F');
                w.Write(0); // RIFF size (parser ignores it)
                w.Write((byte)'W');
                w.Write((byte)'A');
                w.Write((byte)'V');
                w.Write((byte)'E');

                if (junkChunkBeforeFmt)
                {
                    w.Write((byte)'L');
                    w.Write((byte)'I');
                    w.Write((byte)'S');
                    w.Write((byte)'T');
                    w.Write(4);
                    w.Write(0xDEADBEEF);
                }

                w.Write((byte)'f');
                w.Write((byte)'m');
                w.Write((byte)'t');
                w.Write((byte)' ');
                w.Write(16);
                w.Write((ushort)audioFormat);
                w.Write((ushort)channels);
                w.Write(sampleRate);
                w.Write(sampleRate * channels * bitsPerSample / 8); // byte rate
                w.Write((ushort)(channels * bitsPerSample / 8)); // block align
                w.Write((ushort)bitsPerSample);

                if (!omitData)
                {
                    w.Write((byte)'d');
                    w.Write((byte)'a');
                    w.Write((byte)'t');
                    w.Write((byte)'a');
                    w.Write(dataSizeOverride >= 0 ? dataSizeOverride : samples.Length * 2);
                    foreach (short s in samples)
                        w.Write(s);
                }

                return ms.ToArray();
            }
        }

        // --- Happy path -------------------------------------------------------

        [Test]
        public void Read_ValidWav_RoundTripsSamples()
        {
            var wav = BuildWav(samples: new short[] { 0, 16384, 32767, -32768 });

            float[] result = VoxrWavReader.Read(wav);

            Assert.AreEqual(4, result.Length);
            Assert.AreEqual(0f, result[0], 1e-4f);
            Assert.AreEqual(0.5f, result[1], 1e-4f);
            Assert.AreEqual(1f, result[2], 1e-4f);
            Assert.AreEqual(-1f, result[3], 1e-4f);
        }

        [Test]
        public void Read_UnknownChunkBeforeFmt_IsSkipped()
        {
            var wav = BuildWav(junkChunkBeforeFmt: true, samples: new short[] { 42 });

            float[] result = VoxrWavReader.Read(wav);

            Assert.AreEqual(1, result.Length);
        }

        [Test]
        public void ReadFile_ValidWav_Parses()
        {
            string path = Path.Combine(Path.GetTempPath(), "voxr_wavreader_test.wav");
            File.WriteAllBytes(path, BuildWav(samples: new short[] { 1, 2, 3 }));
            try
            {
                float[] result = VoxrWavReader.ReadFile(path);
                Assert.AreEqual(3, result.Length);
            }
            finally
            {
                File.Delete(path);
            }
        }

        // --- Format gate: every rejection is distinct and message-bearing ------

        [Test]
        public void Read_WrongSampleRate_RejectedNamingBothRates()
        {
            var wav = BuildWav(sampleRate: 44100);

            var ex = Assert.Throws<InvalidDataException>(() => VoxrWavReader.Read(wav));

            StringAssert.Contains("44100", ex.Message);
            StringAssert.Contains("48000", ex.Message);
        }

        [Test]
        public void Read_Stereo_Rejected()
        {
            var wav = BuildWav(channels: 2);

            var ex = Assert.Throws<InvalidDataException>(() => VoxrWavReader.Read(wav));

            StringAssert.Contains("mono", ex.Message);
        }

        [Test]
        public void Read_EightBit_Rejected()
        {
            var wav = BuildWav(bitsPerSample: 8);

            var ex = Assert.Throws<InvalidDataException>(() => VoxrWavReader.Read(wav));

            StringAssert.Contains("8-bit", ex.Message);
            StringAssert.Contains("16-bit", ex.Message);
        }

        [Test]
        public void Read_FloatPcm_Rejected()
        {
            var wav = BuildWav(audioFormat: 3);

            var ex = Assert.Throws<InvalidDataException>(() => VoxrWavReader.Read(wav));

            StringAssert.Contains("PCM", ex.Message);
        }

        [Test]
        public void Read_NotRiff_Rejected()
        {
            var bytes = new byte[64];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = 0x55;

            var ex = Assert.Throws<InvalidDataException>(() => VoxrWavReader.Read(bytes));

            StringAssert.Contains("RIFF", ex.Message);
        }

        [Test]
        public void Read_TooShort_Rejected()
        {
            var ex = Assert.Throws<InvalidDataException>(() => VoxrWavReader.Read(new byte[4]));

            StringAssert.Contains("too short", ex.Message);
        }

        [Test]
        public void Read_TruncatedData_Rejected()
        {
            // data chunk declares more bytes than the file contains
            var wav = BuildWav(samples: new short[] { 1, 2 }, dataSizeOverride: 400);

            var ex = Assert.Throws<InvalidDataException>(() => VoxrWavReader.Read(wav));

            StringAssert.Contains("truncated", ex.Message);
        }

        [Test]
        public void Read_OddDataSize_Rejected()
        {
            var wav = BuildWav(samples: new short[] { 1, 2 }, dataSizeOverride: 3);

            var ex = Assert.Throws<InvalidDataException>(() => VoxrWavReader.Read(wav));

            StringAssert.Contains("16-bit", ex.Message);
        }

        [Test]
        public void Read_MissingDataChunk_Rejected()
        {
            var wav = BuildWav(omitData: true);

            var ex = Assert.Throws<InvalidDataException>(() => VoxrWavReader.Read(wav));

            StringAssert.Contains("data", ex.Message);
        }

        [Test]
        public void Read_SourceName_AppearsInMessage()
        {
            var wav = BuildWav(sampleRate: 22050);

            var ex = Assert.Throws<InvalidDataException>(() =>
                VoxrWavReader.Read(wav, "fixture_7.wav")
            );

            StringAssert.Contains("fixture_7.wav", ex.Message);
        }
    }
}
