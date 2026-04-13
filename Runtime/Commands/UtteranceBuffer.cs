// ============================================================================
// Purpose:  Accumulates split VOSK results into a single utterance before parsing
// Layer:    Runtime.Commands
// Owns:     UtteranceBuffer (internal sealed class)
// Depends:  VoskWord
// ============================================================================
using System;
using System.Collections.Generic;

namespace VoskXR.Commands
{
    /// <summary>
    /// Collects speech fragments across multiple VOSK final results and
    /// concatenates them into a single utterance for the command parser.
    /// The recogniser checks <see cref="ShouldFlush"/> each frame and calls
    /// <see cref="Flush"/> when the buffer window has elapsed.
    /// </summary>
    internal sealed class UtteranceBuffer
    {
        readonly List<string> _texts = new List<string>();
        readonly List<VoskWord> _words = new List<VoskWord>();
        float _lastResultTime;

        /// <summary>True when the buffer holds at least one result.</summary>
        internal bool IsActive { get; private set; }

        /// <summary>
        /// Appends a speech result to the buffer and records the time.
        /// </summary>
        internal void Append(string text, VoskWord[] words, float currentTime)
        {
            _texts.Add(text);
            if (words != null && words.Length > 0)
                _words.AddRange(words);

            _lastResultTime = currentTime;
            IsActive = true;
        }

        /// <summary>
        /// Returns true when enough time has passed since the last result to flush.
        /// </summary>
        internal bool ShouldFlush(float currentTime, float bufferWindow)
        {
            return currentTime - _lastResultTime >= bufferWindow;
        }

        /// <summary>
        /// Concatenates buffered texts and words, clears the buffer, and returns the result.
        /// </summary>
        internal (string Text, VoskWord[] Words) Flush()
        {
            IsActive = false;

            if (_texts.Count == 0)
                return (string.Empty, Array.Empty<VoskWord>());

            // Fast path: single entry avoids string.Join allocation.
            string text = _texts.Count == 1 ? _texts[0] : string.Join(" ", _texts);
            var words = _words.Count > 0 ? _words.ToArray() : Array.Empty<VoskWord>();

            _texts.Clear();
            _words.Clear();

            return (text, words);
        }

        /// <summary>
        /// Discards all buffered data without processing.
        /// </summary>
        internal void Reset()
        {
            _texts.Clear();
            _words.Clear();
            IsActive = false;
        }
    }
}
