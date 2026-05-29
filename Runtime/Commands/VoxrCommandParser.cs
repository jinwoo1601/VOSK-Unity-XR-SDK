// ============================================================================
// Purpose:  Pure C# pattern matcher: scores tokenized VOSK output against command patterns
// Layer:    Runtime.Commands
// Owns:     VoxrCommandParser (internal class)
// Depends:  VoxrSlotDefinition, VoxrCommandDefinition, VoxrSlotMatch, VoxrCommand, VoxrCommandResult, VoxrNumberParser
// ============================================================================
using System;
using System.Collections.Generic;

namespace VoXR.Commands
{
    internal class VoxrCommandParser
    {
        internal const string UnkToken = "[unk]";

        const float MatchScore = 1.0f;
        const float OptionalLiteralScore = 0.5f;
        const float RequiredSlotMissPenalty = -1.0f;
        const float RequiredLiteralMissPenalty = -0.5f;

        internal static readonly char[] SplitSeparator = { ' ' };

        readonly VoxrSlotDefinition[] _slots;
        readonly VoxrCommandDefinition[] _commands;

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
        readonly bool[][] _canCommitEarly;

        // Pooled StringBuilder for TryMatchNumberSequence.
        readonly System.Text.StringBuilder _numberSb = new System.Text.StringBuilder();

        struct SlotValueEntry
        {
            public string CanonicalValue;
            public string[] Words;
            public int WordCount;
        }

        public VoxrCommandParser(VoxrSlotDefinition[] slots, VoxrCommandDefinition[] commands)
        {
            if (slots == null) throw new ArgumentNullException(nameof(slots));
            if (commands == null) throw new ArgumentNullException(nameof(commands));

            _slots = slots;
            _commands = commands;

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

            _canCommitEarly = ComputeCanCommitEarly();

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

        // Whether a full match of commands[commandIndex].Patterns[patternIndex] may be
        // committed before bufferWindow elapses. Exposed for tests; the runtime path uses
        // the command/pattern index from TryEagerCommit's own scan.
        internal bool CanCommitEarly(int commandIndex, int patternIndex)
        {
            return _canCommitEarly != null
                && (uint)commandIndex < (uint)_canCommitEarly.Length
                && (uint)patternIndex < (uint)_canCommitEarly[commandIndex].Length
                && _canCommitEarly[commandIndex][patternIndex];
        }

        // Single-pass speculative check: does the buffered text already form one complete,
        // confident command that cannot be extended or completed by more speech? The
        // selection mirrors ParseInternal's inner loop (searchStart = 0) exactly so the
        // verdict matches the command the subsequent FlushBuffer will actually fire.
        internal bool TryEagerCommit(string[] tokens, Dictionary<string, float> wordConfidence,
            float minScore, float minConfidence)
        {
            if (tokens == null || tokens.Length == 0)
                return false;

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
                return false;

            // The match must span the whole buffer: anything left over (including trailing
            // [unk]) means an in-progress tail that more speech could still complete.
            if (bestStartIdx != 0 || bestEndIdx != tokens.Length)
                return false;

            float confidence = ComputeConfidence(tokens, bestStartIdx, bestEndIdx, wordConfidence);
            if (confidence >= 0f && confidence < minConfidence)
                return false;

            return _canCommitEarly[bestCommandIdx][bestPatternIdx];
        }

        bool[][] ComputeCanCommitEarly()
        {
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

        // True if any concrete form of this pattern is a compatible prefix of any concrete
        // form of a different pattern (so more speech could complete the longer command).
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
                            if (pForms[a].Length < qForms[b].Length
                                && IsCompatiblePrefix(pForms[a], qForms[b]))
                                return true;
                }
            }
            return false;
        }

        static bool IsCompatiblePrefix(string[] p, string[] q)
        {
            for (int i = 0; i < p.Length; i++)
                if (!TokensCompatible(p[i], q[i]))
                    return false;
            return true;
        }

        // Conservative: any slot-involving position is treated as compatible (slots may
        // overlap), so uncertainty blocks early commit. Two literals match only when equal
        // after stripping a leading optional '?'.
        static bool TokensCompatible(string a, string b)
        {
            if (ExtractSlotName(a) != null || ExtractSlotName(b) != null)
                return true;
            return string.Equals(StripOptionalLiteral(a), StripOptionalLiteral(b), StringComparison.Ordinal);
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
