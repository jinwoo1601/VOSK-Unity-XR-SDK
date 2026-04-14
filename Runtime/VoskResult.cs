// ============================================================================
// Purpose:  Data structs for speech recognition results (word, alternative, result)
// Layer:    Runtime
// Owns:     VoskWord, VoskAlternative, VoskResult (public readonly structs)
// Depends:  (none)
// ============================================================================
using System;

namespace VoskXR
{
    public readonly struct VoskWord
    {
        public readonly string Text;

        public readonly float Confidence;

        public readonly float StartTime;

        public readonly float EndTime;

        public VoskWord(string text, float confidence, float startTime, float endTime)
        {
            Text = text;
            Confidence = confidence;
            StartTime = startTime;
            EndTime = endTime;
        }

        public override string ToString() => $"{Text} ({Confidence:F2})";
    }

    public readonly struct VoskAlternative
    {
        public readonly string Text;

        public readonly float Confidence;

        public readonly VoskWord[] Words;

        public VoskAlternative(string text, float confidence, VoskWord[] words)
        {
            Text = text;
            Confidence = confidence;
            Words = words;
        }

        public override string ToString() => $"{Text} (score {Confidence:F1})";
    }

    public readonly struct VoskResult
    {
        public readonly string Text;

        public readonly VoskWord[] Words;

        public readonly VoskAlternative[] Alternatives;

        public VoskResult(string text, VoskWord[] words, VoskAlternative[] alternatives)
        {
            Text = text;
            Words = words;
            Alternatives = alternatives;
        }
    }
}
