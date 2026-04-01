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
    /// A complete recognition result containing the full text and per-word confidence data.
    /// </summary>
    public readonly struct VoskResult
    {
        /// <summary>The full recognised text (same string as <see cref="VoskSpeechRecogniser.OnFinalResult"/>).</summary>
        public readonly string Text;

        /// <summary>
        /// Per-word confidence, timing, and text. Empty when no words were recognised.
        /// </summary>
        public readonly VoskWord[] Words;

        public VoskResult(string text, VoskWord[] words)
        {
            Text = text;
            Words = words;
        }
    }
}
