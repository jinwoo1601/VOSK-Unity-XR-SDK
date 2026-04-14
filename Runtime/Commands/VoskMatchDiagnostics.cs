// ============================================================================
// Purpose:  Editor-only diagnostic structs capturing per-utterance match attempts
// Layer:    Runtime.Commands (UNITY_EDITOR only)
// Owns:     VoskMatchDiagnostics, VoskMatchAttempt, VoskDiagnosticSlotMatch (internal readonly structs)
// Depends:  VoskWord
// ============================================================================
#if UNITY_EDITOR
using System;

namespace VoskXR.Commands
{
    internal readonly struct VoskMatchDiagnostics
    {
        public readonly string InputText;

        public readonly VoskWord[] Words;

        public readonly VoskMatchAttempt[] Attempts;

        public readonly int Frame;

        public VoskMatchDiagnostics(string inputText, VoskWord[] words,
            VoskMatchAttempt[] attempts, int frame)
        {
            InputText = inputText;
            Words = words ?? Array.Empty<VoskWord>();
            Attempts = attempts ?? Array.Empty<VoskMatchAttempt>();
            Frame = frame;
        }
    }

    internal readonly struct VoskMatchAttempt
    {
        public readonly string Intent;

        public readonly string Pattern;

        public readonly float Score;

        public readonly float MinScore;

        public readonly float AggregateConfidence;

        public readonly float MinConfidence;

        public readonly VoskDiagnosticSlotMatch[] Slots;

        public readonly string RejectReason;

        public readonly bool IsAccepted;

        public VoskMatchAttempt(string intent, string pattern, float score, float minScore,
            float aggregateConfidence, float minConfidence, VoskDiagnosticSlotMatch[] slots,
            string rejectReason, bool isAccepted)
        {
            Intent = intent;
            Pattern = pattern;
            Score = score;
            MinScore = minScore;
            AggregateConfidence = aggregateConfidence;
            MinConfidence = minConfidence;
            Slots = slots ?? Array.Empty<VoskDiagnosticSlotMatch>();
            RejectReason = rejectReason;
            IsAccepted = isAccepted;
        }
    }

    internal readonly struct VoskDiagnosticSlotMatch
    {
        public readonly string Name;

        public readonly string Value;

        public readonly int StartWord;

        public readonly int EndWord;

        public readonly float Confidence;

        public VoskDiagnosticSlotMatch(string name, string value,
            int startWord, int endWord, float confidence)
        {
            Name = name;
            Value = value;
            StartWord = startWord;
            EndWord = endWord;
            Confidence = confidence;
        }
    }
}
#endif
