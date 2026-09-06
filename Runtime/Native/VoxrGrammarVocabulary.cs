// ============================================================================
// Purpose:  Authoring-time check that every grammar word exists in the loaded VOSK model
// Layer:    Runtime.Native (UNITY_EDITOR_WIN only)
// Owns:     VoxrGrammarVocabulary (internal static class)
// Depends:  VoxrNative
// ============================================================================
#if UNITY_EDITOR_WIN
using System;
using System.Collections.Generic;

namespace VoXR.Native
{
    // A grammar word the model does not know is dropped silently by the decoder, so any
    // phrase that needs it can never be recognised. Nothing surfaces that today: the
    // Kaldi-side warning is suppressed by vosk_set_log_level(-1). This turns it into a
    // Console warning naming the offending word.
    internal static class VoxrGrammarVocabulary
    {
        // The package's own out-of-vocabulary token, emitted unconditionally into every
        // generated grammar by VoxrCommandParser. It is skipped explicitly rather than
        // relied upon to be in the model: it happens to be present in
        // vosk-model-small-en-us-0.15 (id 152208), but it is not author vocabulary, so a
        // future model lacking it must not warn about a word the author never wrote.
        const string UnkToken = "[unk]";

        // The generated grammar is a flat JSON array of double-quoted entries joined by
        // ", " and built with no escaping pass (VoxrCommandParser.GenerateGrammarJson), so
        // scanning for quote pairs is sufficient and needs no JSON parser. Entries are a
        // mix of single words and space-joined multi-word phrases, e.g.
        //   ["[unk]", "close distance", "close", "distance"]
        //
        // Returns the distinct words in first-seen order; deterministic output matters to
        // the tests. Tolerant of null/empty/malformed input — this is an advisory path and
        // must never throw into a grammar swap.
        internal static List<string> ExtractWords(string grammarJson)
        {
            var words = new List<string>();
            if (string.IsNullOrWhiteSpace(grammarJson))
                return words;

            var seen = new HashSet<string>(StringComparer.Ordinal);

            int i = 0;
            while (true)
            {
                int open = grammarJson.IndexOf('"', i);
                if (open < 0)
                    break;
                int close = grammarJson.IndexOf('"', open + 1);
                if (close < 0)
                    break;

                string entry = grammarJson.Substring(open + 1, close - open - 1);
                foreach (string word in entry.Split(' '))
                {
                    if (word.Length == 0 || word == UnkToken)
                        continue;
                    if (seen.Add(word))
                        words.Add(word);
                }

                i = close + 1;
            }

            return words;
        }

        // Seam: takes the vocabulary test as a predicate so the warning behaviour is
        // testable without a model handle. ExtractWords already de-duplicates, so each
        // unknown word warns at most once per call.
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        internal static void WarnOnUnknownWords(string grammarJson, Func<string, bool> isKnown)
        {
            if (isKnown == null)
                return;

            foreach (string word in ExtractWords(grammarJson))
            {
                if (isKnown(word))
                    continue;

                UnityEngine.Debug.LogWarning(
                    $"[VoxrGrammarVocabulary] Grammar word \"{word}\" is not in the loaded VOSK "
                        + "model's vocabulary. The decoder drops it, so any phrase that needs it "
                        + "can never be recognised. Remedies: spell it out as separate letters "
                        + "(e.g. \"c q b\"), or add a phonetic alias for it. See "
                        + "KNOWN_LIMITATIONS.md, \"Abbreviations and letter sequences map to "
                        + "[unk]\"."
                );
            }
        }

        // Production overload. vosk_model_find_word returns the word's symbol id, or -1 when
        // the model does not know it. The upstream header documents no return contract at
        // all; -1 is measured against the vendored vosk-model-small-en-us-0.15 (known words
        // return positive ids — "fire" 146905, "cease" 66827 — while "cqb", "railgun" and a
        // nonsense string all return -1). Note it is NOT 0: 0 is a valid symbol id.
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        internal static void WarnOnUnknownWords(string grammarJson, IntPtr model)
        {
            if (model == IntPtr.Zero)
                return;

            WarnOnUnknownWords(
                grammarJson,
                word => VoxrNative.vosk_model_find_word(model, word) != -1
            );
        }
    }
}
#endif
