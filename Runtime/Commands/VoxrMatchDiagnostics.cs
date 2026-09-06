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

        /// <summary>
        /// Whether this attempt records a round the leading-required-miss bar refused: the
        /// candidate won selection and consumed its span, but produced no command. Always
        /// accompanied by <see cref="IsAccepted"/> false and a <see cref="RejectReason"/> of
        /// <c>barred</c>. Distinguishable from an ordinary rejection because the bar refuses
        /// before a result exists, so <see cref="Slots"/> is always empty here.
        /// </summary>
        public readonly bool Barred;

        /// <summary>
        /// The intent of the round's second-ranked candidate — what would have won had the
        /// winner not been there — or null when the round had only one candidate. Ranked by the
        /// same order selection used, so "second" means second by every key and not merely by
        /// score. Distinct from <see cref="TiedRival"/>, which is set only on an exact tie: a
        /// runner-up is recorded however far behind it finished. May equal
        /// <see cref="Intent"/> — a command's own second phrasing, or the same pattern at a
        /// later start index. Null on the synthetic attempts that come from no parse round.
        /// </summary>
        public readonly string RunnerUpIntent;

        /// <summary>
        /// That candidate's score, or -1 when there was no runner-up. Because earliest start
        /// outranks score in selection, this can exceed <see cref="Score"/>.
        /// </summary>
        public readonly float RunnerUpScore;

        public VoxrMatchAttempt(string intent, string pattern, float score, float minScore,
            float aggregateConfidence, float minConfidence, VoxrDiagnosticSlotMatch[] slots,
            string rejectReason,
            bool isAccepted,
            string tiedRival = null,
            bool tiedRivalIsSibling = false,
            bool barred = false,
            string runnerUpIntent = null,
            float runnerUpScore = -1f
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
            Barred = barred;
            RunnerUpIntent = runnerUpIntent;
            RunnerUpScore = runnerUpScore;
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
