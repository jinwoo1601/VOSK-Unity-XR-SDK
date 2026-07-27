// ============================================================================
// Purpose:  Pure C# pattern matcher: scores tokenized VOSK output against command patterns
// Layer:    Runtime.Commands
// Owns:     VoxrCommandParser (internal class), EagerCommitVerdict (internal enum)
// Depends:  VoxrSlotDefinition, VoxrCommandDefinition, VoxrSlotMatch, VoxrCommand, VoxrCommandResult, VoxrNumberParser
// ============================================================================
using System;
using System.Collections.Generic;

namespace VoXR.Commands
{
    // What the speculative eager-commit probe makes of the buffered speech.
    internal enum EagerCommitVerdict
    {
        // No complete, confident command spans the buffer — keep buffering normally.
        None,

        // The buffer forms a complete, confident command, but more speech could still
        // extend it into a longer one, so committing now risks firing the wrong command
        // (issue #32). The caller may wait a shorter hold for that continuation instead of
        // the full buffer window.
        HoldExtendable,

        // Complete, confident, and unextendable — safe to fire immediately.
        Commit,
    }

    internal class VoxrCommandParser
    {
        internal const string UnkToken = "[unk]";

        const float MatchScore = 1.0f;
        const float OptionalLiteralScore = 0.5f;
        const float RequiredSlotMissPenalty = -1.0f;
        const float RequiredLiteralMissPenalty = -0.5f;

        // Weight added to the score denominator per in-grammar word the sliding start skips
        // before a match begins (issue #31). At 1.0 the score becomes the fraction of the
        // utterance the pattern actually covers, so a lone one-element pattern found in the
        // tail of a longer utterance scores 0.5 instead of a full 1.0 and falls below the
        // default minScore. 0 restores the previous behaviour (skipped words cost nothing).
        internal const float DefaultSkippedWordPenalty = 1.0f;

        // Upper bound on optional elements in a single pattern for eager-commit analysis
        // (issue #25). ExpandOptionals enumerates 2^optionals concrete forms; past this the
        // expansion is unbounded/overflowing, so the whole parser disables eager commit
        // rather than partially (and unsoundly) analyse. Pathological grammars only.
        const int MaxOptionalExpansion = 12;

        internal static readonly char[] SplitSeparator = { ' ' };

        readonly VoxrSlotDefinition[] _slots;
        readonly VoxrCommandDefinition[] _commands;
        readonly float _skippedWordPenalty;

        // Per-slot lookup: first word -> list of (fullValue, wordCount), sorted longest first.
        readonly Dictionary<string, List<SlotValueEntry>>[] _slotLookups;

        // Slot name -> index into _slots
        readonly Dictionary<string, int> _slotIndex;

        readonly string[] _slotNames;

        // Cached stripped forms of optional literals (pattern element -> literal without '?')
        readonly Dictionary<string, string> _optionalLiteralCache;

        // Pre-computed slot name cache: pattern element (e.g. "{weapon}") -> slot name ("weapon").
        readonly Dictionary<string, string> _slotNameCache;

        // Pre-computed set of optional slot elements (e.g. "{?target}").
        readonly HashSet<string> _optionalSlotElements;

        // Pre-allocated slot match buffers — avoids per-call List allocations in TryMatchScored/Parse.
        readonly int _maxSlotsPerPattern;
        readonly VoxrSlotMatch[] _matchSlotBuf;   // TryMatchScored writes here
        readonly VoxrSlotMatch[] _bestSlotBuf;    // copy-on-best in Parse
        int _matchSlotCount;                      // set by TryMatchScored
#if UNITY_EDITOR
        readonly int[] _matchSlotStartBuf;
        readonly int[] _matchSlotEndBuf;
        readonly int[] _bestSlotStartBuf;
        readonly int[] _bestSlotEndBuf;
#endif

        // Pooled word-confidence dictionary — cleared and reused each utterance.
        readonly Dictionary<string, float> _wordConfidencePool =
            new Dictionary<string, float>(32, StringComparer.Ordinal);

        // Pre-allocated result buffer — sized to _commands.Length.
        readonly VoxrCommandResult[] _resultBuf;
        int _resultCount;

        // Per-(command, pattern) eager-commit eligibility (issue #25): true when a full
        // match of the pattern cannot be extended or completed by further speech — i.e. it
        // is not a prefix of any other pattern and its trailing element can't grow.
        // Computed lazily on first eager check (see EnsureCanCommitEarly): opted-out callers
        // never reach an eager entry point, so they never pay the precompute. May be left
        // null when the grammar is too complex to analyse soundly (see MaxOptionalExpansion).
        bool[][] _canCommitEarly;
        bool _canCommitEarlyComputed;

        // Pooled StringBuilder for TryMatchNumberSequence.
        readonly System.Text.StringBuilder _numberSb = new System.Text.StringBuilder();

        struct SlotValueEntry
        {
            public string CanonicalValue;
            public string[] Words;
            public int WordCount;
        }

        public VoxrCommandParser(VoxrSlotDefinition[] slots, VoxrCommandDefinition[] commands,
            float skippedWordPenalty = DefaultSkippedWordPenalty)
        {
            if (slots == null) throw new ArgumentNullException(nameof(slots));
            if (commands == null) throw new ArgumentNullException(nameof(commands));

            _slots = slots;
            _commands = commands;
            _skippedWordPenalty = skippedWordPenalty > 0f ? skippedWordPenalty : 0f;

            // Build slot name -> index mapping
            _slotIndex = new Dictionary<string, int>(slots.Length, StringComparer.Ordinal);
            _slotNames = new string[slots.Length];
            for (int i = 0; i < slots.Length; i++)
            {
                _slotIndex[slots[i].Name] = i;
                _slotNames[i] = slots[i].Name;
            }

            // Build per-slot first-word lookup (canonical values + aliases)
            _slotLookups = new Dictionary<string, List<SlotValueEntry>>[slots.Length];
            for (int i = 0; i < slots.Length; i++)
            {
                var lookup = new Dictionary<string, List<SlotValueEntry>>(StringComparer.Ordinal);

                // Add canonical values
                foreach (string value in slots[i].Values)
                    AddSlotEntry(lookup, value, value);

                // Add aliases (map to canonical value)
                if (slots[i].Aliases != null)
                {
                    foreach (var kvp in slots[i].Aliases)
                        AddSlotEntry(lookup, kvp.Key, kvp.Value);
                }

                // Sort each list by word count descending (longest match first)
                foreach (var list in lookup.Values)
                    list.Sort((a, b) => b.WordCount.CompareTo(a.WordCount));

                _slotLookups[i] = lookup;
            }

            // Validate slot references and run definition-time validation
            foreach (var command in commands)
            {
                foreach (var pattern in command.Patterns)
                {
                    foreach (string element in pattern)
                    {
                        string slotName = ExtractSlotName(element);
                        if (slotName != null && !_slotIndex.ContainsKey(slotName))
                        {
                            throw new ArgumentException(
                                $"Pattern for intent '{command.Intent}' references undefined slot '{slotName}'.");
                        }
                    }
                }
            }

            // Cache optional literal stripped forms
            _optionalLiteralCache = new Dictionary<string, string>(StringComparer.Ordinal);
            _slotNameCache = new Dictionary<string, string>(StringComparer.Ordinal);
            _optionalSlotElements = new HashSet<string>(StringComparer.Ordinal);
            foreach (var command in commands)
            {
                foreach (var pattern in command.Patterns)
                {
                    foreach (string element in pattern)
                    {
                        if (IsOptionalLiteral(element) && !_optionalLiteralCache.ContainsKey(element))
                            _optionalLiteralCache[element] = element.Substring(1);

                        string slotName = ExtractSlotName(element);
                        if (slotName != null)
                        {
                            if (!_slotNameCache.ContainsKey(element))
                                _slotNameCache[element] = slotName;
                            if (IsOptionalSlot(element))
                                _optionalSlotElements.Add(element);
                        }
                    }
                }
            }

            // Compute max slots per pattern to size reusable buffers.
            int maxSlots = 0;
            foreach (var command in commands)
            {
                foreach (var pattern in command.Patterns)
                {
                    int count = 0;
                    foreach (string element in pattern)
                        if (ExtractSlotName(element) != null) count++;
                    if (count > maxSlots) maxSlots = count;
                }
            }
            _maxSlotsPerPattern = maxSlots > 0 ? maxSlots : 1;
            _matchSlotBuf = new VoxrSlotMatch[_maxSlotsPerPattern];
            _bestSlotBuf = new VoxrSlotMatch[_maxSlotsPerPattern];
#if UNITY_EDITOR
            _matchSlotStartBuf = new int[_maxSlotsPerPattern];
            _matchSlotEndBuf = new int[_maxSlotsPerPattern];
            _bestSlotStartBuf = new int[_maxSlotsPerPattern];
            _bestSlotEndBuf = new int[_maxSlotsPerPattern];
#endif

            _resultBuf = new VoxrCommandResult[Math.Max(commands.Length, 1)];

            // _canCommitEarly is computed lazily on the first eager check (issue #25), so
            // callers who leave eager flush off never pay the O(2^optionals)+O(forms^2)
            // precompute on Configure/RebuildParser/NotifySlotChanged.

            RunValidationWarnings(slots);
        }

        static void AddSlotEntry(Dictionary<string, List<SlotValueEntry>> lookup,
            string surfaceForm, string canonicalValue)
        {
            string[] words = surfaceForm.Split(' ');
            string firstWord = words[0];

            if (!lookup.TryGetValue(firstWord, out var list))
            {
                list = new List<SlotValueEntry>();
                lookup[firstWord] = list;
            }

            list.Add(new SlotValueEntry
            {
                CanonicalValue = canonicalValue,
                Words = words,
                WordCount = words.Length
            });
        }

        static void RunValidationWarnings(VoxrSlotDefinition[] slots)
        {
            foreach (var slot in slots)
            {
                foreach (string value in slot.Values)
                {
                    if (value != value.ToLowerInvariant())
                    {
                        UnityEngine.Debug.LogWarning(
                            $"[VoxrCommandParser] Slot '{slot.Name}' value \"{value}\" contains uppercase characters. " +
                            "VOSK outputs lowercase — this value will never match.");
                    }

                    foreach (char c in value)
                    {
                        if (char.IsPunctuation(c))
                        {
                            UnityEngine.Debug.LogWarning(
                                $"[VoxrCommandParser] Slot '{slot.Name}' value \"{value}\" contains punctuation. " +
                                "VOSK strips punctuation — this may not match as expected.");
                            break;
                        }
                    }

                    if (value.Length == 1)
                    {
                        UnityEngine.Debug.LogWarning(
                            $"[VoxrCommandParser] Slot '{slot.Name}' has single-character value \"{value}\". " +
                            "Consider using an alias instead (e.g. \"a\" → \"one\").");
                    }
                }

                if (slot.Aliases != null)
                {
                    foreach (var key in slot.Aliases.Keys)
                    {
                        if (key.Length == 1)
                        {
                            UnityEngine.Debug.LogWarning(
                                $"[VoxrCommandParser] Slot '{slot.Name}' has single-character alias \"{key}\". " +
                                "Short words may be unreliably recognized by VOSK.");
                        }
                    }
                }
            }
        }

        public VoxrCommandResult[] Parse(string text, VoxrWord[] words)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
#if UNITY_EDITOR
                LastParseDiagnostics = Array.Empty<ParseDiagnosticEntry>();
#endif
                return Array.Empty<VoxrCommandResult>();
            }

            string[] tokens = text.Split(SplitSeparator, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
#if UNITY_EDITOR
                LastParseDiagnostics = Array.Empty<ParseDiagnosticEntry>();
#endif
                return Array.Empty<VoxrCommandResult>();
            }

            var wordConfidence = InstanceBuildWordConfidence(words);
            int count = ParseInternal(tokens, text, wordConfidence);

            if (count == 0)
                return Array.Empty<VoxrCommandResult>();

            // Public API returns a fresh copy — callers own the array indefinitely.
            var result = new VoxrCommandResult[count];
            Array.Copy(_resultBuf, result, count);
            return result;
        }

        internal int ParseInternal(string[] tokens, string text,
            Dictionary<string, float> wordConfidence)
        {
            _resultCount = 0;

            if (tokens.Length == 0)
            {
#if UNITY_EDITOR
                LastParseDiagnostics = Array.Empty<ParseDiagnosticEntry>();
#endif
                return 0;
            }

            int searchStart = 0;
#if UNITY_EDITOR
            var diagnosticEntries = new List<ParseDiagnosticEntry>();
#endif

            while (searchStart < tokens.Length)
            {
                float bestScore = float.MinValue;
                float bestRawScore = 0f;
                float bestDenominator = 0f;
                int bestLiteralCount = -1;
                int bestCommandIdx = -1;
                int bestPatternIdx = -1;
                int bestStartIdx = int.MaxValue;
                int bestEndIdx = 0;
                int bestSlotCount = 0;

                for (int ci = 0; ci < _commands.Length; ci++)
                {
                    var patterns = _commands[ci].Patterns;
                    for (int pi = 0; pi < patterns.Length; pi++)
                    {
                        for (int startIdx = searchStart; startIdx < tokens.Length; startIdx++)
                        {
                            if (tokens[startIdx] == UnkToken)
                                continue;

                            var matchResult = TryMatchScored(tokens, startIdx, patterns[pi]);

                            if (matchResult.Score > 0f &&
                                (bestScore <= 0f ||
                                 startIdx < bestStartIdx ||
                                 (startIdx == bestStartIdx &&
                                  (matchResult.Score > bestScore ||
                                   (matchResult.Score == bestScore && matchResult.LiteralCount > bestLiteralCount)))))
                            {
                                bestScore = matchResult.Score;
                                bestRawScore = matchResult.RawScore;
                                bestDenominator = matchResult.Denominator;
                                bestLiteralCount = matchResult.LiteralCount;
                                bestCommandIdx = ci;
                                bestPatternIdx = pi;
                                bestStartIdx = startIdx;
                                bestEndIdx = matchResult.EndIdx;
                                bestSlotCount = matchResult.SlotCount;
                                // Copy current match buffer into best buffer
                                if (matchResult.SlotCount > 0)
                                {
                                    Array.Copy(_matchSlotBuf, _bestSlotBuf, matchResult.SlotCount);
#if UNITY_EDITOR
                                    Array.Copy(_matchSlotStartBuf, _bestSlotStartBuf, matchResult.SlotCount);
                                    Array.Copy(_matchSlotEndBuf, _bestSlotEndBuf, matchResult.SlotCount);
#endif
                                }
                            }
                        }
                    }
                }

                if (bestCommandIdx < 0 || bestScore <= 0f)
                    break;

                // Safety: prevent infinite loop if a match consumes no tokens
                if (bestEndIdx <= searchStart)
                    break;

                // Charge the winner for the in-grammar words the sliding start walked past
                // (issue #31). Skipped words cost nothing on their own, so a short pattern
                // found in the tail of a stray utterance ("thrusters report" matching the
                // one-word "report" command) used to score a full 1.0 and fire. Adding them
                // to the denominator makes the score the fraction of the utterance the
                // pattern covers, which only bites patterns short enough to be swallowed
                // whole. [unk] runs are excluded — tolerating out-of-grammar preamble and
                // hesitation is what the sliding start is for. The penalty is applied after
                // selection so it filters via minScore without changing which pattern wins,
                // and it measures from searchStart, so a second command in a multi-command
                // utterance starts clean.
                if (_skippedWordPenalty > 0f && bestStartIdx > searchStart)
                {
                    int skipped = CountRecognisedTokens(tokens, searchStart, bestStartIdx);
                    if (skipped > 0)
                        bestScore = bestRawScore
                            / (bestDenominator + skipped * _skippedWordPenalty);
                }

                float confidence = ComputeConfidence(tokens, bestStartIdx, bestEndIdx, wordConfidence);

                VoxrSlotMatch[] slotsArray;
                if (bestSlotCount > 0)
                {
                    slotsArray = new VoxrSlotMatch[bestSlotCount];
                    Array.Copy(_bestSlotBuf, slotsArray, bestSlotCount);
                }
                else
                {
                    slotsArray = Array.Empty<VoxrSlotMatch>();
                }

                var command = new VoxrCommand(
                    _commands[bestCommandIdx].Intent,
                    slotsArray,
                    confidence,
                    bestScore,
                    text,
                    _slotNames,
                    bestPatternIdx);

                if (_resultCount >= _resultBuf.Length)
                    break; // Buffer full — stop extracting.
                _resultBuf[_resultCount++] = new VoxrCommandResult(command);
#if UNITY_EDITOR
                int[] diagStartWords = null;
                int[] diagEndWords = null;
                if (bestSlotCount > 0)
                {
                    diagStartWords = new int[bestSlotCount];
                    diagEndWords = new int[bestSlotCount];
                    Array.Copy(_bestSlotStartBuf, diagStartWords, bestSlotCount);
                    Array.Copy(_bestSlotEndBuf, diagEndWords, bestSlotCount);
                }
                diagnosticEntries.Add(new ParseDiagnosticEntry
                {
                    PatternString = string.Join(" ", _commands[bestCommandIdx].Patterns[bestPatternIdx]),
                    SlotStartWords = diagStartWords,
                    SlotEndWords = diagEndWords,
                });
#endif
                searchStart = bestEndIdx;
            }

#if UNITY_EDITOR
            LastParseDiagnostics = diagnosticEntries.Count > 0
                ? diagnosticEntries.ToArray()
                : Array.Empty<ParseDiagnosticEntry>();
#endif
            return _resultCount;
        }

        internal VoxrCommandResult[] ResultBuffer => _resultBuf;

        public VoxrCommandResult[] Parse(string text)
        {
            return Parse(text, Array.Empty<VoxrWord>());
        }

        public string GenerateGrammarJson() => GenerateGrammarJson(_slots, _commands);

        internal static string GenerateGrammarJson(VoxrSlotDefinition[] slots,
            VoxrCommandDefinition[] commands, string[] additionalWords = null)
        {
            var uniqueWords = new HashSet<string>(StringComparer.Ordinal);

            // Collect words from pattern literals (including optional literals stripped of ?)
            foreach (var command in commands)
            {
                foreach (var pattern in command.Patterns)
                {
                    foreach (string element in pattern)
                    {
                        if (ExtractSlotName(element) != null)
                            continue;

                        string word = element;
                        if (IsOptionalLiteral(element))
                            word = element.Substring(1);

                        foreach (string w in word.Split(' '))
                        {
                            if (w.Length > 0)
                                uniqueWords.Add(w);
                        }
                    }
                }
            }

            // Collect words from slot values
            foreach (var slot in slots)
            {
                foreach (string value in slot.Values)
                {
                    foreach (string word in value.Split(' '))
                    {
                        if (word.Length > 0)
                            uniqueWords.Add(word);
                    }
                }

                // Collect words from alias keys
                if (slot.Aliases != null)
                {
                    foreach (var key in slot.Aliases.Keys)
                    {
                        foreach (string word in key.Split(' '))
                        {
                            if (word.Length > 0)
                                uniqueWords.Add(word);
                        }
                    }
                }
            }

            // Add digit vocabulary when any NumberSequence slot exists
            foreach (var slot in slots)
            {
                if (slot.Type == VoxrSlotType.NumberSequence)
                {
                    foreach (string word in VoxrNumberParser.DigitVocabulary)
                        uniqueWords.Add(word);
                    break;
                }
            }

            // Add caller-supplied words (e.g. confirm/cancel vocabulary)
            if (additionalWords != null)
            {
                foreach (string word in additionalWords)
                {
                    if (!string.IsNullOrEmpty(word))
                        uniqueWords.Add(word);
                }
            }

            uniqueWords.Add(UnkToken);

            // Build JSON array
            var sorted = new List<string>(uniqueWords);
            sorted.Sort(StringComparer.Ordinal);

            var sb = new System.Text.StringBuilder();
            sb.Append('[');
            for (int i = 0; i < sorted.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append('"');
                sb.Append(sorted[i]);
                sb.Append('"');
            }
            sb.Append(']');

            return sb.ToString();
        }

        struct MatchResult
        {
            public float Score;
            public float RawScore;
            public float Denominator;
            public int LiteralCount;
            public int SlotCount;
            public int EndIdx;
        }

#if UNITY_EDITOR
        internal struct ParseDiagnosticEntry
        {
            public string PatternString;
            public int[] SlotStartWords;
            public int[] SlotEndWords;
        }

        internal ParseDiagnosticEntry[] LastParseDiagnostics;
#endif

        MatchResult TryMatchScored(string[] tokens, int startIdx, string[] pattern)
        {
            int tokenIdx = startIdx;
            float rawScore = 0f;
            // Dynamic denominator: required elements always count toward it, but optional
            // elements only count when actually matched. Omitted optionals then drop out of
            // both numerator and denominator, so taking advantage of optionality is never
            // penalized and a perfect match always normalizes to 1.0.
            float denominator = 0f;
            int literalCount = 0;
            int slotCount = 0;

            for (int patIdx = 0; patIdx < pattern.Length; patIdx++)
            {
                string element = pattern[patIdx];

                while (tokenIdx < tokens.Length && tokens[tokenIdx] == UnkToken)
                    tokenIdx++;

                bool isSlot = _slotNameCache.TryGetValue(element, out string slotName);
                if (isSlot)
                {
                    bool isOptional = _optionalSlotElements.Contains(element);

                    if (!_slotIndex.TryGetValue(slotName, out int slotIdx))
                        return default;

                    string matchedValue;
                    int consumed;
                    if (_slots[slotIdx].Type == VoxrSlotType.NumberSequence)
                        matchedValue = TryMatchNumberSequence(tokens, tokenIdx,
                            _slots[slotIdx].MinWords, _slots[slotIdx].MaxWords, out consumed);
                    else
                        matchedValue = TryMatchSlot(tokens, tokenIdx, slotIdx, out consumed);

                    if (matchedValue != null)
                    {
#if UNITY_EDITOR
                        _matchSlotStartBuf[slotCount] = tokenIdx;
                        _matchSlotEndBuf[slotCount] = tokenIdx + consumed;
#endif
                        _matchSlotBuf[slotCount++] = new VoxrSlotMatch(slotName, matchedValue);
                        tokenIdx += consumed;
                        rawScore += MatchScore;
                        denominator += MatchScore;
                    }
                    else if (!isOptional)
                    {
                        rawScore += RequiredSlotMissPenalty;
                        denominator += MatchScore;
                    }
                    // Unmatched optional slot: contributes nothing to score or denominator.
                }
                else if (IsOptionalLiteral(element))
                {
                    if (tokenIdx < tokens.Length &&
                        string.Equals(tokens[tokenIdx], _optionalLiteralCache[element], StringComparison.Ordinal))
                    {
                        rawScore += OptionalLiteralScore;
                        denominator += OptionalLiteralScore;
                        literalCount++;
                        tokenIdx++;
                    }
                    // Unmatched optional literal: contributes nothing to score or denominator.
                }
                else
                {
                    denominator += MatchScore;
                    if (tokenIdx < tokens.Length &&
                        string.Equals(tokens[tokenIdx], element, StringComparison.Ordinal))
                    {
                        rawScore += MatchScore;
                        literalCount++;
                        tokenIdx++;
                    }
                    else
                    {
                        rawScore += RequiredLiteralMissPenalty;
                    }
                }
            }

            _matchSlotCount = slotCount;
            float normalizedScore = denominator > 0f ? rawScore / denominator : 0f;

            return new MatchResult
            {
                Score = normalizedScore,
                RawScore = rawScore,
                Denominator = denominator,
                LiteralCount = literalCount,
                SlotCount = slotCount,
                EndIdx = tokenIdx,
            };
        }

        string TryMatchSlot(string[] tokens, int startIdx, int slotIdx, out int consumed)
        {
            consumed = 0;

            while (startIdx < tokens.Length && tokens[startIdx] == UnkToken)
                startIdx++;

            if (startIdx >= tokens.Length)
                return null;

            string firstWord = tokens[startIdx];
            var lookup = _slotLookups[slotIdx];

            if (!lookup.TryGetValue(firstWord, out var entries))
                return null;

            // Try longest match first
            foreach (var entry in entries)
            {
                if (startIdx + entry.WordCount > tokens.Length)
                    continue;

                bool match = true;
                string[] valueWords = entry.Words;
                for (int w = 0; w < entry.WordCount; w++)
                {
                    if (!string.Equals(tokens[startIdx + w], valueWords[w], StringComparison.Ordinal))
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    consumed = entry.WordCount;
                    return entry.CanonicalValue;
                }
            }

            return null;
        }

        string TryMatchNumberSequence(string[] tokens, int startIdx, int minWords, int maxWords, out int consumed)
        {
            consumed = 0;
            int idx = startIdx;

            while (idx < tokens.Length && tokens[idx] == UnkToken)
                idx++;

            int matchStart = idx;
            int count = 0;
            while (count < maxWords && idx < tokens.Length
                && VoxrNumberParser.DigitVocabulary.Contains(tokens[idx]))
            {
                count++;
                idx++;
            }

            if (count < minWords)
                return null;

            consumed = idx - startIdx;

            if (count == 1)
                return tokens[matchStart];

            _numberSb.Clear();
            for (int i = 0; i < count; i++)
            {
                if (i > 0) _numberSb.Append(' ');
                _numberSb.Append(tokens[matchStart + i]);
            }
            return _numberSb.ToString();
        }

        internal string TryMatchSlotByName(string[] tokens, int startIdx,
            string slotName, out int consumed)
        {
            consumed = 0;

            if (!_slotIndex.TryGetValue(slotName, out int slotIdx))
                return null;

            if (_slots[slotIdx].Type == VoxrSlotType.NumberSequence)
                return TryMatchNumberSequence(tokens, startIdx,
                    _slots[slotIdx].MinWords, _slots[slotIdx].MaxWords, out consumed);

            return TryMatchSlot(tokens, startIdx, slotIdx, out consumed);
        }

        internal static Dictionary<string, float> BuildWordConfidence(VoxrWord[] words)
        {
            if (words == null || words.Length == 0)
                return null;

            var d = new Dictionary<string, float>(words.Length, StringComparer.Ordinal);
            foreach (var w in words)
                if (!string.IsNullOrEmpty(w.Text) && !d.ContainsKey(w.Text))
                    d[w.Text] = w.Confidence;
            return d;
        }

        internal Dictionary<string, float> InstanceBuildWordConfidence(VoxrWord[] words)
        {
            if (words == null || words.Length == 0)
                return null;
            return InstanceBuildWordConfidence((ReadOnlySpan<VoxrWord>)words);
        }

        internal Dictionary<string, float> InstanceBuildWordConfidence(ReadOnlySpan<VoxrWord> words)
        {
            if (words.Length == 0)
                return null;

            _wordConfidencePool.Clear();
            foreach (var w in words)
                if (!string.IsNullOrEmpty(w.Text) && !_wordConfidencePool.ContainsKey(w.Text))
                    _wordConfidencePool[w.Text] = w.Confidence;
            return _wordConfidencePool;
        }

        // Tokens in [startIdx, endIdx) that VOSK resolved to a grammar word, i.e. excluding
        // the [unk] filler the sliding start is meant to skip for free.
        internal static int CountRecognisedTokens(string[] tokens, int startIdx, int endIdx)
        {
            int count = 0;
            for (int i = startIdx; i < endIdx; i++)
                if (tokens[i] != UnkToken)
                    count++;
            return count;
        }

        internal static float ComputeConfidence(string[] tokens, int startIdx, int endIdx,
            Dictionary<string, float> wordConfidence)
        {
            if (wordConfidence == null || wordConfidence.Count == 0)
                return -1f;

            float minConf = float.MaxValue;
            bool anyMatch = false;

            for (int i = startIdx; i < endIdx; i++)
            {
                if (tokens[i] == UnkToken)
                    continue;

                if (wordConfidence.TryGetValue(tokens[i], out float conf))
                {
                    anyMatch = true;
                    if (conf < minConf)
                        minConf = conf;
                }
            }

            return anyMatch ? minConf : -1f;
        }

        internal static string ExtractSlotName(string element)
        {
            if (element.Length < 3 || element[0] != '{' || element[element.Length - 1] != '}')
                return null;

            string inner = element.Substring(1, element.Length - 2);
            if (inner.Length > 0 && inner[0] == '?')
                return inner.Substring(1);

            return inner;
        }

        internal static bool IsOptionalSlot(string element)
        {
            return element.Length >= 4 && element[0] == '{' && element[1] == '?'
                && element[element.Length - 1] == '}';
        }

        static bool IsOptionalLiteral(string element)
        {
            return element.Length >= 2 && element[0] == '?' && element[1] != '{';
        }

        internal float ScoreFollowUp(string intent, int patternIdx,
            IReadOnlyList<VoxrSlotMatch> filledSlots)
        {
            // Find the command by intent
            string[] pattern = null;
            for (int ci = 0; ci < _commands.Length; ci++)
            {
                if (string.Equals(_commands[ci].Intent, intent, StringComparison.Ordinal))
                {
                    var patterns = _commands[ci].Patterns;
                    if (patternIdx >= 0 && patternIdx < patterns.Length)
                        pattern = patterns[patternIdx];
                    break;
                }
            }

            if (pattern == null || pattern.Length == 0)
                return 1f; // Fallback — don't block the command

            // Dynamic denominator, mirroring TryMatchScored: required elements always count,
            // optional elements only when satisfied, so unfilled optionals don't penalize.
            float rawScore = 0f;
            float denominator = 0f;
            for (int i = 0; i < pattern.Length; i++)
            {
                string element = pattern[i];
                string slotName = ExtractSlotName(element);

                if (slotName != null)
                {
                    bool filled = false;
                    for (int s = 0; s < filledSlots.Count; s++)
                    {
                        if (string.Equals(filledSlots[s].Name, slotName, StringComparison.Ordinal))
                        {
                            filled = true;
                            break;
                        }
                    }

                    if (filled)
                    {
                        rawScore += MatchScore;
                        denominator += MatchScore;
                    }
                    else if (!IsOptionalSlot(element))
                    {
                        rawScore += RequiredSlotMissPenalty;
                        denominator += MatchScore;
                    }
                    // Unfilled optional slots contribute 0 to both numerator and denominator.
                }
                else if (IsOptionalLiteral(element))
                {
                    // Optional literals may or may not have been spoken — give no credit and
                    // leave them out of the denominator (treated as omitted optionals).
                }
                else
                {
                    // Required literal — the original parse matched initial literals,
                    // and follow-up implies the remaining ones. Credit them.
                    rawScore += MatchScore;
                    denominator += MatchScore;
                }
            }

            return denominator > 0f ? rawScore / denominator : 0f;
        }

        // -------- Eager-flush support (issue #25) --------

        // Computes _canCommitEarly on first use and caches it. Both eager entry points
        // (CanCommitEarly and TryEagerCommit) call this, so the precompute only runs for
        // callers that actually probe eager commit; eager-flush-off callers never reach
        // here. A rebuilt parser is a fresh instance, so the table is recomputed naturally.
        void EnsureCanCommitEarly()
        {
            if (_canCommitEarlyComputed)
                return;
            _canCommitEarly = ComputeCanCommitEarly();
            _canCommitEarlyComputed = true;
        }

        // Whether a full match of commands[commandIndex].Patterns[patternIndex] may be
        // committed before bufferWindow elapses. Exposed for tests; the runtime path uses
        // the command/pattern index from TryEagerCommit's own scan.
        internal bool CanCommitEarly(int commandIndex, int patternIndex)
        {
            EnsureCanCommitEarly();
            return _canCommitEarly != null
                && (uint)commandIndex < (uint)_canCommitEarly.Length
                && (uint)patternIndex < (uint)_canCommitEarly[commandIndex].Length
                && _canCommitEarly[commandIndex][patternIndex];
        }

        // Single-pass speculative check: does the buffered text already form one complete,
        // confident command, and if so can more speech still extend it? The selection
        // mirrors ParseInternal's inner loop (searchStart = 0) exactly so the verdict
        // matches the command the subsequent FlushBuffer will actually fire.
        internal EagerCommitVerdict TryEagerCommit(string[] tokens,
            Dictionary<string, float> wordConfidence, float minScore, float minConfidence)
        {
            if (tokens == null || tokens.Length == 0)
                return EagerCommitVerdict.None;

            EnsureCanCommitEarly();

            float bestScore = float.MinValue;
            int bestLiteralCount = -1;
            int bestCommandIdx = -1;
            int bestPatternIdx = -1;
            int bestStartIdx = int.MaxValue;
            int bestEndIdx = 0;

            for (int ci = 0; ci < _commands.Length; ci++)
            {
                var patterns = _commands[ci].Patterns;
                for (int pi = 0; pi < patterns.Length; pi++)
                {
                    for (int startIdx = 0; startIdx < tokens.Length; startIdx++)
                    {
                        if (tokens[startIdx] == UnkToken)
                            continue;

                        var matchResult = TryMatchScored(tokens, startIdx, patterns[pi]);

                        if (matchResult.Score > 0f &&
                            (bestScore <= 0f ||
                             startIdx < bestStartIdx ||
                             (startIdx == bestStartIdx &&
                              (matchResult.Score > bestScore ||
                               (matchResult.Score == bestScore && matchResult.LiteralCount > bestLiteralCount)))))
                        {
                            bestScore = matchResult.Score;
                            bestLiteralCount = matchResult.LiteralCount;
                            bestCommandIdx = ci;
                            bestPatternIdx = pi;
                            bestStartIdx = startIdx;
                            bestEndIdx = matchResult.EndIdx;
                        }
                    }
                }
            }

            if (bestCommandIdx < 0 || bestScore < minScore)
                return EagerCommitVerdict.None;

            // The match must span the whole buffer: anything left over (including trailing
            // [unk]) means an in-progress tail that more speech could still complete.
            if (bestStartIdx != 0 || bestEndIdx != tokens.Length)
                return EagerCommitVerdict.None;

            float confidence = ComputeConfidence(tokens, bestStartIdx, bestEndIdx, wordConfidence);
            if (confidence >= 0f && confidence < minConfidence)
                return EagerCommitVerdict.None;

            // _canCommitEarly is legitimately null when the grammar was too complex to
            // analyse (MaxOptionalExpansion). HoldExtendable is a product of that analysis,
            // so without it report None and leave the full window in force rather than
            // shorten the wait on a pattern nothing has vetted.
            if (_canCommitEarly == null)
                return EagerCommitVerdict.None;

            // Guarded accessor: the indices come from a separate scan, so route through the
            // bounds-checked path rather than indexing raw.
            return CanCommitEarly(bestCommandIdx, bestPatternIdx)
                ? EagerCommitVerdict.Commit
                : EagerCommitVerdict.HoldExtendable;
        }

        bool[][] ComputeCanCommitEarly()
        {
            // Guard pathological grammars: ExpandOptionals enumerates 2^optionals forms, which
            // overflows/allocates unboundedly past MaxOptionalExpansion. If ANY pattern is over
            // the limit, disable eager commit for the whole parser (return null -> CanCommitEarly
            // reports false) rather than partially analyse — an un-expanded pattern used as the
            // "longer" side would unsoundly let a real prefix through and fire the wrong command.
            for (int ci = 0; ci < _commands.Length; ci++)
            {
                var patterns = _commands[ci].Patterns;
                for (int pi = 0; pi < patterns.Length; pi++)
                {
                    if (CountOptionalElements(patterns[pi]) > MaxOptionalExpansion)
                    {
                        UnityEngine.Debug.LogWarning(
                            $"[VoXR] Pattern for intent '{_commands[ci].Intent}' has more than " +
                            $"{MaxOptionalExpansion} optional elements; eager flush is disabled for " +
                            "this command set. Reduce optional tokens to re-enable it.");
                        return null;
                    }
                }
            }

            // Pre-expand every pattern over its optional elements once: a pattern with an
            // optional element matches with that element present OR absent, so prefix
            // analysis must consider every concrete form on both sides.
            var expanded = new List<string[]>[_commands.Length][];
            for (int ci = 0; ci < _commands.Length; ci++)
            {
                var patterns = _commands[ci].Patterns;
                expanded[ci] = new List<string[]>[patterns.Length];
                for (int pi = 0; pi < patterns.Length; pi++)
                    expanded[ci][pi] = ExpandOptionals(patterns[pi]);
            }

            var result = new bool[_commands.Length][];
            for (int ci = 0; ci < _commands.Length; ci++)
            {
                var patterns = _commands[ci].Patterns;
                var flags = new bool[patterns.Length];
                for (int pi = 0; pi < patterns.Length; pi++)
                    flags[pi] = IsTerminalPattern(patterns[pi])
                        && !IsPrefixOfAnyOtherPattern(ci, pi, expanded);
                result[ci] = flags;
            }
            return result;
        }

        // Number of optional elements ({?slot} or ?literal) in a pattern; ExpandOptionals
        // produces 2^this concrete forms.
        static int CountOptionalElements(string[] pattern)
        {
            int count = 0;
            for (int i = 0; i < pattern.Length; i++)
                if (IsOptionalLiteral(pattern[i]) || IsOptionalSlot(pattern[i]))
                    count++;
            return count;
        }

        // All concrete token sequences for a pattern, including/excluding each optional
        // element ({?slot} or ?literal). A pattern with no optionals yields one form.
        static List<string[]> ExpandOptionals(string[] pattern)
        {
            var optionalIdx = new List<int>();
            for (int i = 0; i < pattern.Length; i++)
                if (IsOptionalLiteral(pattern[i]) || IsOptionalSlot(pattern[i]))
                    optionalIdx.Add(i);

            int combos = 1 << optionalIdx.Count;
            var forms = new List<string[]>(combos);
            var form = new List<string>(pattern.Length);
            for (int mask = 0; mask < combos; mask++)
            {
                form.Clear();
                for (int i = 0; i < pattern.Length; i++)
                {
                    int optPos = optionalIdx.IndexOf(i);
                    if (optPos >= 0 && (mask & (1 << optPos)) == 0)
                        continue; // this optional element is omitted in this combo.
                    form.Add(pattern[i]);
                }
                forms.Add(form.ToArray());
            }
            return forms;
        }

        // A pattern is "terminal" when its final element cannot absorb or be followed by
        // more speech within the same command.
        bool IsTerminalPattern(string[] pattern)
        {
            if (pattern.Length == 0)
                return false;

            string last = pattern[pattern.Length - 1];

            // A trailing optional element could still be filled by later speech.
            if (IsOptionalLiteral(last) || IsOptionalSlot(last))
                return false;

            string slotName = ExtractSlotName(last);
            if (slotName == null)
                return true; // required literal — fixed.

            if (!_slotIndex.TryGetValue(slotName, out int slotIdx))
                return false; // unknown slot (ctor validates) — stay safe.

            if (_slots[slotIdx].Type == VoxrSlotType.NumberSequence)
                return _slots[slotIdx].MinWords == _slots[slotIdx].MaxWords; // fixed width.

            // Enumerated: terminal only if no surface form (value or alias key) is a word-
            // prefix of another, so a matched value can't grow into a longer one.
            return !HasWordPrefixAmbiguity(slotIdx);
        }

        bool HasWordPrefixAmbiguity(int slotIdx)
        {
            // Surface forms (values + alias keys), already split into words by AddSlotEntry.
            var forms = new List<string[]>();
            foreach (var list in _slotLookups[slotIdx].Values)
                foreach (var entry in list)
                    forms.Add(entry.Words);

            for (int i = 0; i < forms.Count; i++)
                for (int j = 0; j < forms.Count; j++)
                {
                    if (i == j) continue;
                    if (IsWordPrefix(forms[i], forms[j]))
                        return true;
                }
            return false;
        }

        // True if any concrete form of this pattern is a prefix of any concrete form of a
        // different pattern (so more speech could complete the longer command). Two notions
        // of "prefix" are checked: an element-count prefix (P has fewer elements than Q and
        // they line up), and a word-level prefix where P and Q have the same element count
        // but a slot in P can match a word-sequence that Q extends (BoundaryWordPrefixHazard).
        bool IsPrefixOfAnyOtherPattern(int ci, int pi, List<string[]>[][] expanded)
        {
            var pForms = expanded[ci][pi];
            for (int cj = 0; cj < _commands.Length; cj++)
            {
                var qPatterns = expanded[cj];
                for (int pj = 0; pj < qPatterns.Length; pj++)
                {
                    if (cj == ci && pj == pi) continue;
                    var qForms = qPatterns[pj];
                    for (int a = 0; a < pForms.Count; a++)
                        for (int b = 0; b < qForms.Count; b++)
                            if ((pForms[a].Length < qForms[b].Length
                                    && IsCompatiblePrefix(pForms[a], qForms[b]))
                                || BoundaryWordPrefixHazard(pForms[a], qForms[b]))
                                return true;
                }
            }
            return false;
        }

        // Word-level prefix hazard the element-count check above misses (issue #25). When
        // pForm's final element is reached after a run of literals identical to qForm's, and
        // at that element qForm can produce a word-sequence that strictly extends one of
        // pForm's, a full match of P is a word-prefix of a full match of Q — so firing P
        // early could ship the wrong command. The classic miss is two equal-element-count
        // patterns: "go {dir=north}" vs "go {place=north pole}" (enumerated), or
        // "dial {n3}" vs "dial {n5}" (number sequence). Cardinal rule: err toward a hazard.
        bool BoundaryWordPrefixHazard(string[] pForm, string[] qForm)
        {
            int k = pForm.Length - 1;          // P's final element — the divergence point.
            if (k < 0 || qForm.Length < k + 1) // Q must have an element aligned with it.
                return false;

            // Everything before the divergence must be identical literals so both sides have
            // consumed the same words up to k and the slot analysis at k is word-aligned.
            // (A slot in the shared run could realign words; that case is left to the
            // conservative element-count check and is not analysed word-precisely here.)
            if (SharedLiteralPrefixLen(pForm, qForm) != k)
                return false;

            string pSlot = ExtractSlotName(pForm[k]);
            string qSlot = ExtractSlotName(qForm[k]);

            if (pSlot == null)
            {
                // P ends in a literal: only a slot on Q's side can grow past it. (A literal
                // qForm[k] is either equal — making the shared run longer than k — or differs,
                // so it cannot extend P's word.)
                return qSlot != null && SlotCanExtendWord(qSlot, StripOptionalLiteral(pForm[k]));
            }

            if (qSlot == null)
                return false; // P slot vs Q literal: Q can extend only via trailing elements,
                              // which the element-count check already covers.

            // Both sides are slots at the divergence: a value (or fixed-width number run) of
            // P's slot must be a strict word-prefix of a value (or wider run) of Q's slot.
            return SlotValueIsWordPrefixOfSlot(pSlot, qSlot);
        }

        // Length of the leading run where p and q are identical literals (no slots), so the
        // two forms produce exactly the same words over that run.
        static int SharedLiteralPrefixLen(string[] p, string[] q)
        {
            int n = Math.Min(p.Length, q.Length);
            int i = 0;
            for (; i < n; i++)
            {
                if (ExtractSlotName(p[i]) != null || ExtractSlotName(q[i]) != null)
                    break;
                if (!string.Equals(StripOptionalLiteral(p[i]), StripOptionalLiteral(q[i]),
                        StringComparison.Ordinal))
                    break;
            }
            return i;
        }

        // True if slotName can produce a surface form that begins with firstWord and is
        // longer than one word, so it strictly extends a command ending in that literal.
        // A number sequence is treated as able to start with any word (NUMBER is a wildcard),
        // so any multi-word run extends.
        bool SlotCanExtendWord(string slotName, string firstWord)
        {
            if (!_slotIndex.TryGetValue(slotName, out int slotIdx))
                return true; // unknown slot — stay safe.

            if (_slots[slotIdx].Type == VoxrSlotType.NumberSequence)
                return _slots[slotIdx].MaxWords > 1;

            foreach (var list in _slotLookups[slotIdx].Values)
                foreach (var entry in list)
                    if (entry.Words.Length > 1 &&
                        string.Equals(entry.Words[0], firstWord, StringComparison.Ordinal))
                        return true;
            return false;
        }

        // True if some realization of slot pSlotName is a strict word-prefix of some
        // realization of slot qSlotName. Number-sequence runs match any word (NUMBER is a
        // wildcard), so they compare by width; enumerated slots compare surface forms.
        bool SlotValueIsWordPrefixOfSlot(string pSlotName, string qSlotName)
        {
            if (!_slotIndex.TryGetValue(pSlotName, out int pIdx) ||
                !_slotIndex.TryGetValue(qSlotName, out int qIdx))
                return true; // unknown slot — stay safe.

            bool pNum = _slots[pIdx].Type == VoxrSlotType.NumberSequence;
            bool qNum = _slots[qIdx].Type == VoxrSlotType.NumberSequence;

            if (pNum && qNum)
                // P's shortest run can be a strict prefix of a wider Q run.
                return _slots[qIdx].MaxWords > _slots[pIdx].MinWords;

            if (pNum)
                // A min-width number run is a strict prefix of any longer Q value.
                return AnySlotSurfaceFormLongerThan(qIdx, _slots[pIdx].MinWords);

            if (qNum)
                // Any P value is a strict prefix of a wider number run.
                return AnySlotSurfaceFormShorterThan(pIdx, _slots[qIdx].MaxWords);

            foreach (var pList in _slotLookups[pIdx].Values)
                foreach (var pEntry in pList)
                    foreach (var qList in _slotLookups[qIdx].Values)
                        foreach (var qEntry in qList)
                            if (IsWordPrefix(pEntry.Words, qEntry.Words))
                                return true;
            return false;
        }

        bool AnySlotSurfaceFormLongerThan(int slotIdx, int wordCount)
        {
            foreach (var list in _slotLookups[slotIdx].Values)
                foreach (var entry in list)
                    if (entry.Words.Length > wordCount)
                        return true;
            return false;
        }

        bool AnySlotSurfaceFormShorterThan(int slotIdx, int wordCount)
        {
            foreach (var list in _slotLookups[slotIdx].Values)
                foreach (var entry in list)
                    if (entry.Words.Length < wordCount)
                        return true;
            return false;
        }

        bool IsCompatiblePrefix(string[] p, string[] q)
        {
            // The element-wise walk assumes p[i] and q[i] cover the same words, which only
            // holds while both sides have consumed identical literals. The first element past
            // that run is still word-aligned, so a slot there can be judged against its own
            // vocabulary (issue #33); beyond it a slot may have consumed a different number of
            // words on each side, so the alignment — and with it the vocabulary check — is gone.
            int alignedIdx = SharedLiteralPrefixLen(p, q);

            for (int i = 0; i < p.Length; i++)
                if (!TokensCompatible(p[i], q[i], i == alignedIdx))
                    return false;
            return true;
        }

        // Two literals match only when equal after stripping a leading optional '?'. Slot
        // positions are conservatively compatible — slot-vs-slot because two vocabularies may
        // overlap, slot-vs-literal because a misaligned slot may not be facing that word at
        // all. At a word-aligned position (valueAware) a slot facing a literal is compatible
        // only when some surface form of the slot really does start with that word (issue
        // #33); without that test a lone-slot pattern counts as a potential prefix of every
        // longer pattern in the grammar and can never commit early.
        bool TokensCompatible(string a, string b, bool valueAware)
        {
            string aSlot = ExtractSlotName(a);
            string bSlot = ExtractSlotName(b);

            if (aSlot != null && bSlot != null)
                return true;
            if (aSlot != null)
                return !valueAware || SlotCanStartWithWord(aSlot, StripOptionalLiteral(b));
            if (bSlot != null)
                return !valueAware || SlotCanStartWithWord(bSlot, StripOptionalLiteral(a));

            return string.Equals(StripOptionalLiteral(a), StripOptionalLiteral(b), StringComparison.Ordinal);
        }

        // True if some surface form of the slot begins with word, so the slot could account
        // for a command carrying that literal at the same position. A number sequence matches
        // digit words only. Unknown slots stay safe.
        bool SlotCanStartWithWord(string slotName, string word)
        {
            if (!_slotIndex.TryGetValue(slotName, out int slotIdx))
                return true; // unknown slot (ctor validates) — stay safe.

            if (_slots[slotIdx].Type == VoxrSlotType.NumberSequence)
                return VoxrNumberParser.DigitVocabulary.Contains(word);

            // _slotLookups is keyed by each surface form's first word.
            return _slotLookups[slotIdx].ContainsKey(word);
        }

        static string StripOptionalLiteral(string element)
        {
            return IsOptionalLiteral(element) ? element.Substring(1) : element;
        }

        static bool IsWordPrefix(string[] shorter, string[] longer)
        {
            if (shorter.Length >= longer.Length)
                return false;
            for (int i = 0; i < shorter.Length; i++)
                if (!string.Equals(shorter[i], longer[i], StringComparison.Ordinal))
                    return false;
            return true;
        }
    }
}
