// ============================================================================
// Purpose:  Data structs for speech recognition results (word, alternative, result)
// Layer:    Runtime
// Owns:     VoskWord, VoskAlternative, VoskResult (public readonly structs)
// Depends:  (none)
// ============================================================================
using System;

namespace VoskXR
{
    /// <summary>
    /// A single recognised word with its confidence score and timing.
    /// Returned by VOSK when word-level confidence is enabled.
    /// </summary>
    public readonly struct VoskWord
    {
        /// <summary>The recognised word.</summary>
        public readonly string Text;

        /// <summary>Confidence score in the range [0, 1]. Higher is more confident.</summary>
        public readonly float Confidence;

        /// <summary>Start time of the word in seconds from the beginning of the utterance.</summary>
        public readonly float StartTime;

        /// <summary>End time of the word in seconds from the beginning of the utterance.</summary>
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

    /// <summary>
    /// One recognition hypothesis from VOSK's n-best list.
    /// When <see cref="VoskSpeechRecogniser.maxAlternatives"/> is &gt; 0,
    /// each final result contains multiple alternatives ranked by confidence.
    /// </summary>
    public readonly struct VoskAlternative
    {
        /// <summary>The recognised text for this hypothesis.</summary>
        public readonly string Text;

        /// <summary>
        /// Acoustic model score. Higher values indicate a better match.
        /// Only meaningful for comparing alternatives within the same result;
        /// the scale varies between models.
        /// </summary>
        public readonly float Confidence;

        /// <summary>Per-word confidence and timing for this hypothesis. May be empty.</summary>
        public readonly VoskWord[] Words;

        public VoskAlternative(string text, float confidence, VoskWord[] words)
        {
            Text = text;
            Confidence = confidence;
            Words = words;
        }

        public override string ToString() => $"{Text} (score {Confidence:F1})";
    }

    /// <summary>
    /// A complete recognition result containing the full text, per-word confidence data,
    /// and alternative hypotheses when n-best is enabled.
    /// </summary>
    public readonly struct VoskResult
    {
        /// <summary>The full recognised text (same string as <see cref="VoskSpeechRecogniser.OnFinalResult"/>).</summary>
        public readonly string Text;

        /// <summary>
        /// Per-word confidence, timing, and text for the best hypothesis.
        /// Empty when no words were recognised.
        /// </summary>
        public readonly VoskWord[] Words;

        /// <summary>
        /// Alternative recognition hypotheses, ranked best-first.
        /// Empty when <see cref="VoskSpeechRecogniser.maxAlternatives"/> is 0 (the default).
        /// </summary>
        public readonly VoskAlternative[] Alternatives;

        public VoskResult(string text, VoskWord[] words, VoskAlternative[] alternatives)
        {
            Text = text;
            Words = words;
            Alternatives = alternatives;
        }
    }
}
