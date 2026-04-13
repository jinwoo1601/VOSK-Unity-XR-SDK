// ============================================================================
// Purpose:  Accumulates split VOSK results into a single utterance before parsing
// Layer:    Runtime.Commands
// Owns:     UtteranceBuffer (internal sealed class)
// Depends:  VoskWord
// ============================================================================
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

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
        readonly System.Text.StringBuilder _sb = new System.Text.StringBuilder();
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
        /// Concatenates buffered texts, clears text buffer, and returns the joined text.
        /// Call <see cref="GetWordsSpan"/> before <see cref="ClearWords"/> to read word data
        /// without copying. Both must happen synchronously in the same <c>Update</c> tick.
        /// </summary>
        internal string Flush()
        {
            IsActive = false;

            if (_texts.Count == 0)
                return string.Empty;

            // Fast path: single entry avoids StringBuilder overhead.
            string text;
            if (_texts.Count == 1)
            {
                text = _texts[0];
            }
            else
            {
                _sb.Clear();
                for (int i = 0; i < _texts.Count; i++)
                {
                    if (i > 0) _sb.Append(' ');
                    _sb.Append(_texts[i]);
                }
                text = _sb.ToString();
            }
            _texts.Clear();
            return text;
        }

        /// <summary>
        /// Returns a span over the buffered word data without copying.
        /// Valid only until <see cref="ClearWords"/> is called.
        /// </summary>
        internal ReadOnlySpan<VoskWord> GetWordsSpan()
        {
            return _words.Count > 0
                ? CollectionsMarshal.AsSpan(_words)
                : ReadOnlySpan<VoskWord>.Empty;
        }

        /// <summary>
        /// Clears the word list. Must be called after <see cref="GetWordsSpan"/>
        /// has been fully consumed.
        /// </summary>
        internal void ClearWords()
        {
            _words.Clear();
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
