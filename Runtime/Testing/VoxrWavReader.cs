// ============================================================================
// Purpose:  Byte-level RIFF/PCM WAV parser for audio test fixtures (48 kHz mono 16-bit only)
// Layer:    Runtime.Testing
// Owns:     VoxrWavReader (internal static class)
// Depends:  (none)
// ============================================================================
using System.IO;

namespace VoXR.Testing
{
    internal static class VoxrWavReader
    {
        internal const int RequiredSampleRate = 48000;

        internal static float[] ReadFile(string path) =>
            Read(File.ReadAllBytes(path), Path.GetFileName(path));

        /// <summary>
        /// Parses canonical RIFF/PCM WAV bytes and returns samples as floats in [-1, 1].
        /// Accepts exactly 48 kHz mono 16-bit PCM; rejects anything else with a
        /// distinct message naming the actual and required format.
        /// </summary>
        internal static float[] Read(byte[] wav, string sourceName = "WAV data")
        {
            if (wav == null || wav.Length < 12)
                throw new InvalidDataException(
                    $"{sourceName}: too short to be a WAV file ({(wav == null ? 0 : wav.Length)} bytes)."
                );
            if (!HasFourCc(wav, 0, "RIFF") || !HasFourCc(wav, 8, "WAVE"))
                throw new InvalidDataException($"{sourceName}: not a RIFF/WAVE file.");

            bool fmtSeen = false;
            int offset = 12;
            while (offset + 8 <= wav.Length)
            {
                string chunkId = FourCc(wav, offset);
                int chunkSize = ReadInt32(wav, offset + 4);
                int dataStart = offset + 8;

                if (chunkSize < 0)
                    throw new InvalidDataException(
                        $"{sourceName}: chunk '{chunkId}' declares an invalid size ({chunkSize})."
                    );
                if (dataStart + chunkSize > wav.Length)
                    throw new InvalidDataException(
                        $"{sourceName}: chunk '{chunkId}' is truncated (declares {chunkSize} bytes, "
                            + $"{wav.Length - dataStart} available)."
                    );

                if (chunkId == "fmt ")
                {
                    if (chunkSize < 16)
                        throw new InvalidDataException(
                            $"{sourceName}: 'fmt ' chunk is malformed ({chunkSize} bytes; at least 16 required)."
                        );

                    int audioFormat = ReadUInt16(wav, dataStart);
                    int channels = ReadUInt16(wav, dataStart + 2);
                    int sampleRate = ReadInt32(wav, dataStart + 4);
                    int bitsPerSample = ReadUInt16(wav, dataStart + 14);

                    if (audioFormat != 1)
                        throw new InvalidDataException(
                            $"{sourceName}: audio format tag is {audioFormat}; only PCM (1) is supported."
                        );
                    if (channels != 1)
                        throw new InvalidDataException(
                            $"{sourceName}: {channels} channels; mono (1) is required."
                        );
                    if (sampleRate != RequiredSampleRate)
                        throw new InvalidDataException(
                            $"{sourceName}: sample rate is {sampleRate} Hz; {RequiredSampleRate} Hz is required."
                        );
                    if (bitsPerSample != 16)
                        throw new InvalidDataException(
                            $"{sourceName}: {bitsPerSample}-bit samples; 16-bit is required."
                        );

                    fmtSeen = true;
                }
                else if (chunkId == "data")
                {
                    if (!fmtSeen)
                        throw new InvalidDataException(
                            $"{sourceName}: 'data' chunk appears before 'fmt ' chunk."
                        );
                    if ((chunkSize & 1) != 0)
                        throw new InvalidDataException(
                            $"{sourceName}: 'data' chunk size {chunkSize} is not a whole number of 16-bit samples."
                        );

                    int count = chunkSize / 2;
                    var samples = new float[count];
                    for (int i = 0; i < count; i++)
                    {
                        short s = (short)(
                            wav[dataStart + i * 2] | (wav[dataStart + i * 2 + 1] << 8)
                        );
                        samples[i] = s / 32768f;
                    }
                    return samples;
                }

                // Chunks are word-aligned; odd sizes carry one pad byte.
                offset = dataStart + chunkSize + (chunkSize & 1);
            }

            throw new InvalidDataException(
                fmtSeen
                    ? $"{sourceName}: no 'data' chunk found."
                    : $"{sourceName}: no 'fmt ' chunk found."
            );
        }

        static bool HasFourCc(byte[] bytes, int offset, string fourCc) =>
            bytes[offset] == fourCc[0]
            && bytes[offset + 1] == fourCc[1]
            && bytes[offset + 2] == fourCc[2]
            && bytes[offset + 3] == fourCc[3];

        static string FourCc(byte[] bytes, int offset) =>
            $"{(char)bytes[offset]}{(char)bytes[offset + 1]}{(char)bytes[offset + 2]}{(char)bytes[offset + 3]}";

        static int ReadInt32(byte[] bytes, int offset) =>
            bytes[offset]
            | (bytes[offset + 1] << 8)
            | (bytes[offset + 2] << 16)
            | (bytes[offset + 3] << 24);

        static int ReadUInt16(byte[] bytes, int offset) => bytes[offset] | (bytes[offset + 1] << 8);
    }
}
