// ============================================================================
// Purpose:  Data structs for speech recognition results (word, alternative, result)
// Layer:    Runtime
// Owns:     VoxrWord, VoxrAlternative, VoxrResult (public readonly structs)
// Depends:  (none)
// ============================================================================
using System;

namespace VoXR
{
    public readonly struct VoxrWord
    {
        public readonly string Text;

        public readonly float Confidence;

        public readonly float StartTime;

        public readonly float EndTime;

        public VoxrWord(string text, float confidence, float startTime, float endTime)
        {
            Text = text;
            Confidence = confidence;
            StartTime = startTime;
            EndTime = endTime;
        }

        public override string ToString() => $"{Text} ({Confidence:F2})";
    }

    public readonly struct VoxrAlternative
    {
        public readonly string Text;

        public readonly float Confidence;

        public readonly VoxrWord[] Words;

        public VoxrAlternative(string text, float confidence, VoxrWord[] words)
        {
            Text = text;
            Confidence = confidence;
            Words = words;
        }

        public override string ToString() => $"{Text} (score {Confidence:F1})";
    }

    public readonly struct VoxrResult
    {
        public readonly string Text;

        public readonly VoxrWord[] Words;

        public readonly VoxrAlternative[] Alternatives;

        public VoxrResult(string text, VoxrWord[] words, VoxrAlternative[] alternatives)
        {
            Text = text;
            Words = words;
            Alternatives = alternatives;
        }
    }
}
