using System;
using System.Collections.Generic;

namespace VoskXR.Commands
{
    /// <summary>
    /// Pure C# command parser. Matches tokenized VOSK output against registered
    /// command patterns using scored matching with sliding start position.
    /// </summary>
    internal class VoskCommandParser
    {
        static readonly char[] SplitSeparator = { ' ' };

        readonly VoskSlotDefinition[] _slots;
        readonly VoskCommandDefinition[] _commands;

        // Per-slot lookup: first word -> list of (fullValue, wordCount), sorted longest first.
        // Includes both canonical values and alias entries.
        readonly Dictionary<string, List<SlotValueEntry>>[] _slotLookups;

        // Slot name -> index into _slots
        readonly Dictionary<string, int> _slotIndex;

        // Registered slot names for debug validation
        readonly string[] _slotNames;

        struct SlotValueEntry
        {
            public string CanonicalValue;
            public string[] Words;
            public int WordCount;
        }

        public VoskCommandParser(VoskSlotDefinition[] slots, VoskCommandDefinition[] commands)
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

            // Definition-time validation warnings
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

        static void RunValidationWarnings(VoskSlotDefinition[] slots)
        {
            foreach (var slot in slots)
            {
                foreach (string value in slot.Values)
                {
                    if (value != value.ToLowerInvariant())
                    {
                        UnityEngine.Debug.LogWarning(
                            $"[VoskCommandParser] Slot '{slot.Name}' value \"{value}\" contains uppercase characters. " +
                            "VOSK outputs lowercase — this value will never match.");
                    }

                    foreach (char c in value)
                    {
                        if (char.IsPunctuation(c))
                        {
                            UnityEngine.Debug.LogWarning(
                                $"[VoskCommandParser] Slot '{slot.Name}' value \"{value}\" contains punctuation. " +
                                "VOSK strips punctuation — this may not match as expected.");
                            break;
                        }
                    }

                    if (value.Length == 1)
                    {
                        UnityEngine.Debug.LogWarning(
                            $"[VoskCommandParser] Slot '{slot.Name}' has single-character value \"{value}\". " +
                            "Consider using an alias instead (e.g. \"a\" → \"one\").");
                    }
                }
            }
        }

        /// <summary>
        /// Parses input text against all registered command patterns using scored matching
        /// with sliding start position.
        /// </summary>
        public VoskCommandResult Parse(string text, VoskWord[] words)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new VoskCommandResult(text ?? string.Empty);

            string[] tokens = text.Split(SplitSeparator, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                return new VoskCommandResult(text);

            Dictionary<string, float> wordConfidence = null;
            if (words != null && words.Length > 0)
            {
                wordConfidence = new Dictionary<string, float>(words.Length, StringComparer.Ordinal);
                foreach (var w in words)
                {
                    if (!string.IsNullOrEmpty(w.Text) && !wordConfidence.ContainsKey(w.Text))
                        wordConfidence[w.Text] = w.Confidence;
                }
            }

            float bestScore = float.MinValue;
            int bestLiteralCount = -1;
            int bestCommandIdx = -1;
            List<VoskSlotMatch> bestSlots = null;

            for (int ci = 0; ci < _commands.Length; ci++)
            {
                var patterns = _commands[ci].Patterns;
                for (int pi = 0; pi < patterns.Length; pi++)
                {
                    // Sliding start: try matching from every token position
                    for (int startIdx = 0; startIdx < tokens.Length; startIdx++)
                    {
                        // Skip [unk] tokens as start positions
                        if (tokens[startIdx] == "[unk]")
                            continue;

                        var matchResult = TryMatchScored(tokens, startIdx, patterns[pi]);

                        if (matchResult.Score > bestScore ||
                            (matchResult.Score == bestScore && matchResult.LiteralCount > bestLiteralCount))
                        {
                            bestScore = matchResult.Score;
                            bestLiteralCount = matchResult.LiteralCount;
                            bestCommandIdx = ci;
                            bestSlots = matchResult.Slots;
                        }
                    }
                }
            }

            if (bestCommandIdx < 0 || bestScore <= 0f)
                return new VoskCommandResult(text);

            float confidence = ComputeConfidence(tokens, bestSlots, wordConfidence);

            var slotsArray = bestSlots != null && bestSlots.Count > 0
                ? bestSlots.ToArray()
                : Array.Empty<VoskSlotMatch>();

            var command = new VoskCommand(
                _commands[bestCommandIdx].Intent,
                slotsArray,
                confidence,
                bestScore,
                text,
                _slotNames);

            return new VoskCommandResult(command);
        }

        /// <summary>
        /// Parses input text without word confidence data.
        /// </summary>
        public VoskCommandResult Parse(string text)
        {
            return Parse(text, Array.Empty<VoskWord>());
        }

        /// <summary>
        /// Generates a VOSK grammar JSON array containing all words from pattern
        /// literals, slot values, aliases, and optional literals, plus [unk].
        /// </summary>
        public string GenerateGrammarJson()
        {
            var uniqueWords = new HashSet<string>(StringComparer.Ordinal);

            // Collect words from pattern literals (including optional literals stripped of ?)
            foreach (var command in _commands)
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
            foreach (var slot in _slots)
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

            // Always include [unk]
            uniqueWords.Add("[unk]");

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
            public List<VoskSlotMatch> Slots;
        }

        /// <summary>
        /// Scored matching from a given start position in the token array.
        /// Returns normalized score (0.0–1.0) based on matched elements.
        /// </summary>
        MatchResult TryMatchScored(string[] tokens, int startIdx, string[] pattern)
        {
            int tokenIdx = startIdx;
            float rawScore = 0f;
            int patternLength = pattern.Length;
            int literalCount = 0;
            List<VoskSlotMatch> slots = null;

            for (int patIdx = 0; patIdx < pattern.Length; patIdx++)
            {
                string element = pattern[patIdx];

                // Skip [unk] tokens in input
                while (tokenIdx < tokens.Length && tokens[tokenIdx] == "[unk]")
                    tokenIdx++;

                string slotName = ExtractSlotName(element);
                if (slotName != null)
                {
                    bool isOptional = IsOptionalSlot(element);

                    if (!_slotIndex.TryGetValue(slotName, out int slotIdx))
                        return default;

                    string matchedValue = TryMatchSlot(tokens, tokenIdx, slotIdx, out int consumed);
                    if (matchedValue != null)
                    {
                        if (slots == null)
                            slots = new List<VoskSlotMatch>();
                        slots.Add(new VoskSlotMatch(slotName, matchedValue));
                        tokenIdx += consumed;
                        rawScore += 1.0f;
                    }
                    else if (isOptional)
                    {
                        // Optional slot not matched — no penalty
                    }
                    else
                    {
                        rawScore -= 1.0f; // Heavy penalty for missing required slot
                    }
                }
                else if (IsOptionalLiteral(element))
                {
                    string literal = element.Substring(1);
                    if (tokenIdx < tokens.Length &&
                        string.Equals(tokens[tokenIdx], literal, StringComparison.Ordinal))
                    {
                        rawScore += 0.5f;
                        literalCount++;
                        tokenIdx++;
                    }
                    // Absent optional literal — score 0, skip
                }
                else
                {
                    // Required literal
                    if (tokenIdx < tokens.Length &&
                        string.Equals(tokens[tokenIdx], element, StringComparison.Ordinal))
                    {
                        rawScore += 1.0f;
                        literalCount++;
                        tokenIdx++;
                    }
                    else
                    {
                        rawScore -= 0.5f;
                    }
                }
            }

            float normalizedScore = patternLength > 0 ? rawScore / patternLength : 0f;

            return new MatchResult
            {
                Score = normalizedScore,
                LiteralCount = literalCount,
                Slots = slots
            };
        }

        string TryMatchSlot(string[] tokens, int startIdx, int slotIdx, out int consumed)
        {
            consumed = 0;

            // Skip [unk] tokens
            while (startIdx < tokens.Length && tokens[startIdx] == "[unk]")
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

        float ComputeConfidence(string[] tokens, List<VoskSlotMatch> slots,
            Dictionary<string, float> wordConfidence)
        {
            if (wordConfidence == null || wordConfidence.Count == 0)
                return 0f;

            float minConf = float.MaxValue;
            bool anyMatch = false;

            foreach (string token in tokens)
            {
                if (token == "[unk]")
                    continue;

                if (wordConfidence.TryGetValue(token, out float conf))
                {
                    anyMatch = true;
                    if (conf < minConf)
                        minConf = conf;
                }
            }

            return anyMatch ? minConf : 0f;
        }

        /// <summary>
        /// Extracts the slot name from a pattern element. Returns null for literals.
        /// Handles {slot} (required), {?slot} (optional), and skips ?literal.
        /// </summary>
        static string ExtractSlotName(string element)
        {
            if (element.Length < 3 || element[0] != '{' || element[element.Length - 1] != '}')
                return null;

            string inner = element.Substring(1, element.Length - 2);
            if (inner.Length > 0 && inner[0] == '?')
                return inner.Substring(1);

            return inner;
        }

        /// <summary>
        /// Returns true if the pattern element is an optional slot reference ({?slot}).
        /// </summary>
        static bool IsOptionalSlot(string element)
        {
            return element.Length >= 4 && element[0] == '{' && element[1] == '?'
                && element[element.Length - 1] == '}';
        }

        /// <summary>
        /// Returns true if the pattern element is an optional literal (?word).
        /// Distinguished from {?slot} by not starting with '{'.
        /// </summary>
        static bool IsOptionalLiteral(string element)
        {
            return element.Length >= 2 && element[0] == '?' && element[1] != '{';
        }
    }
}
