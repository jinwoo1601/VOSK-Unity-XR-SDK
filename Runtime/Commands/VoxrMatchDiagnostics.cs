// ============================================================================
// Purpose:  Editor-only diagnostic structs capturing per-utterance match attempts
// Layer:    Runtime.Commands (UNITY_EDITOR only)
// Owns:     VoxrMatchDiagnostics, VoxrMatchAttempt, VoxrDiagnosticSlotMatch (internal readonly structs)
// Depends:  VoxrWord
// ============================================================================
#if UNITY_EDITOR
using System;

namespace VoXR.Commands
{
    internal readonly struct VoxrMatchDiagnostics
    {
        public readonly string InputText;

        public readonly VoxrWord[] Words;

        public readonly VoxrMatchAttempt[] Attempts;

        public readonly int Frame;

        public VoxrMatchDiagnostics(string inputText, VoxrWord[] words,
            VoxrMatchAttempt[] attempts, int frame)
        {
            InputText = inputText;
            Words = words ?? Array.Empty<VoxrWord>();
            Attempts = attempts ?? Array.Empty<VoxrMatchAttempt>();
            Frame = frame;
        }
    }

    internal readonly struct VoxrMatchAttempt
    {
        public readonly string Intent;

        public readonly string Pattern;

        public readonly float Score;

        public readonly float MinScore;

        public readonly float AggregateConfidence;

        public readonly float MinConfidence;

        public readonly VoxrDiagnosticSlotMatch[] Slots;

        public readonly string RejectReason;

        public readonly bool IsAccepted;

        /// <summary>
        /// The equally-good rival this attempt beat on registration order alone, as
        /// <c>intent (pattern N)</c>, or null when nothing tied it. Optional because most
        /// attempts are not built from a parse round at all — a confirm/cancel resolution or a
        /// pending timeout has no candidate set behind it.
        /// </summary>
        public readonly string TiedRival;

        /// <summary>
        /// Whether <see cref="TiedRival"/> was a sibling rival — one dropped word apart from the
        /// winner, which is speech ambiguity the runtime can offer as a choice — rather than a
        /// grammar-authoring hazard such as two duplicate patterns under different intents.
        /// Meaningless when <see cref="TiedRival"/> is null.
        /// </summary>
        public readonly bool TiedRivalIsSibling;

        public VoxrMatchAttempt(string intent, string pattern, float score, float minScore,
            float aggregateConfidence, float minConfidence, VoxrDiagnosticSlotMatch[] slots,
            string rejectReason,
            bool isAccepted,
            string tiedRival = null,
            bool tiedRivalIsSibling = false
        )
        {
            Intent = intent;
            Pattern = pattern;
            Score = score;
            MinScore = minScore;
            AggregateConfidence = aggregateConfidence;
            MinConfidence = minConfidence;
            Slots = slots ?? Array.Empty<VoxrDiagnosticSlotMatch>();
            RejectReason = rejectReason;
            IsAccepted = isAccepted;
            TiedRival = tiedRival;
            TiedRivalIsSibling = tiedRivalIsSibling;
        }
    }

    internal readonly struct VoxrDiagnosticSlotMatch
    {
        public readonly string Name;

        public readonly string Value;

        public readonly int StartWord;

        public readonly int EndWord;

        public readonly float Confidence;

        public VoxrDiagnosticSlotMatch(string name, string value,
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
