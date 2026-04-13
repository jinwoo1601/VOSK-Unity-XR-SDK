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
    /// <summary>
    /// Diagnostic snapshot of a single utterance's journey through the command pipeline.
    /// Populated by <see cref="VoskCommandRecogniser"/> at the end of each parse cycle.
    /// The Editor debug window polls <see cref="Frame"/> to detect new data.
    /// </summary>
    internal readonly struct VoskMatchDiagnostics
    {
        /// <summary>The raw text that entered the parser.</summary>
        public readonly string InputText;

        /// <summary>Per-word confidence data from VOSK (may be empty for injected text).</summary>
        public readonly VoskWord[] Words;

        /// <summary>
        /// One entry per command extracted (or attempted) from this utterance.
        /// Covers both accepted and rejected matches, plus a "no match" entry
        /// when the parser found nothing.
        /// </summary>
        public readonly VoskMatchAttempt[] Attempts;

        /// <summary>
        /// <see cref="UnityEngine.Time.frameCount"/> when this diagnostic was created.
        /// The debug window compares against its last-seen frame to detect new data.
        /// </summary>
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

    /// <summary>
    /// Diagnostic detail for a single command match attempt within an utterance.
    /// </summary>
    internal readonly struct VoskMatchAttempt
    {
        /// <summary>Matched intent name, or null if no pattern matched.</summary>
        public readonly string Intent;

        /// <summary>The pattern string that matched (e.g. "fire {weapon}"), or null.</summary>
        public readonly string Pattern;

        /// <summary>Normalised match score from the parser (0.0–1.0).</summary>
        public readonly float Score;

        /// <summary>The minScore threshold that was applied.</summary>
        public readonly float MinScore;

        /// <summary>Minimum word confidence across the matched token span.</summary>
        public readonly float AggregateConfidence;

        /// <summary>The minConfidence threshold that was applied.</summary>
        public readonly float MinConfidence;

        /// <summary>Per-slot diagnostic detail with word positions and confidence.</summary>
        public readonly VoskDiagnosticSlotMatch[] Slots;

        /// <summary>
        /// Human-readable rejection reason, or null if the command was accepted.
        /// Examples: "score 0.42 &lt; minScore 0.60", "no match".
        /// </summary>
        public readonly string RejectReason;

        /// <summary>True if the command passed all thresholds and was fired.</summary>
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

    /// <summary>
    /// Diagnostic-only slot match with word-level position and confidence data.
    /// Separate from the runtime <see cref="VoskSlotMatch"/> to avoid enriching
    /// the hot path with fields only the debug window uses.
    /// </summary>
    internal readonly struct VoskDiagnosticSlotMatch
    {
        /// <summary>Slot name (e.g. "weapon").</summary>
        public readonly string Name;

        /// <summary>Matched canonical value (e.g. "missiles").</summary>
        public readonly string Value;

        /// <summary>Index of the first token consumed by this slot (inclusive).</summary>
        public readonly int StartWord;

        /// <summary>Index past the last token consumed by this slot (exclusive).</summary>
        public readonly int EndWord;

        /// <summary>Minimum word confidence across the slot's token span (-1 if unavailable).</summary>
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
