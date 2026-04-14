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
    internal sealed class UtteranceBuffer
    {
        readonly List<string> _texts = new List<string>();
        VoskWord[] _wordBuf = new VoskWord[32];
        int _wordCount;
        readonly System.Text.StringBuilder _sb = new System.Text.StringBuilder();
        float _lastResultTime;

        internal bool IsActive { get; private set; }

        internal void Append(string text, VoskWord[] words, float currentTime)
        {
            _texts.Add(text);
            if (words != null && words.Length > 0)
            {
                int needed = _wordCount + words.Length;
                if (needed > _wordBuf.Length)
                    Array.Resize(ref _wordBuf, Math.Max(needed, _wordBuf.Length * 2));
                Array.Copy(words, 0, _wordBuf, _wordCount, words.Length);
                _wordCount += words.Length;
            }

            _lastResultTime = currentTime;
            IsActive = true;
        }

        internal bool ShouldFlush(float currentTime, float bufferWindow)
        {
            return currentTime - _lastResultTime >= bufferWindow;
        }

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

        internal ReadOnlySpan<VoskWord> GetWordsSpan()
        {
            return _wordCount > 0
                ? new ReadOnlySpan<VoskWord>(_wordBuf, 0, _wordCount)
                : ReadOnlySpan<VoskWord>.Empty;
        }

        internal void ClearWords()
        {
            _wordCount = 0;
        }

        internal void Reset()
        {
            _texts.Clear();
            _wordCount = 0;
            IsActive = false;
        }
    }
}
