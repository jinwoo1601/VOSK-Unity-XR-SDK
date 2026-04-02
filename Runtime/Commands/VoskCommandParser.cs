using System;
using System.Collections.Generic;

namespace VoskXR.Commands
{
    /// <summary>
    /// Pure C# command parser. Matches tokenized VOSK output against registered
    /// command patterns using greedy left-to-right matching.
    /// </summary>
    internal class VoskCommandParser
    {
        static readonly char[] SplitSeparator = { ' ' };

        readonly VoskSlotDefinition[] _slots;
        readonly VoskCommandDefinition[] _commands;

        // Per-slot lookup: first word -> list of (fullValue, wordCount), sorted longest first.
        readonly Dictionary<string, List<SlotValueEntry>>[] _slotLookups;

        // Slot name -> index into _slots
        readonly Dictionary<string, int> _slotIndex;

        struct SlotValueEntry
        {
            public string FullValue;
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
            for (int i = 0; i < slots.Length; i++)
                _slotIndex[slots[i].Name] = i;

            // Build per-slot first-word lookup
            _slotLookups = new Dictionary<string, List<SlotValueEntry>>[slots.Length];
            for (int i = 0; i < slots.Length; i++)
            {
                var lookup = new Dictionary<string, List<SlotValueEntry>>(StringComparer.Ordinal);
                foreach (string value in slots[i].Values)
                {
                    string[] words = value.Split(' ');
                    string firstWord = words[0];

                    if (!lookup.TryGetValue(firstWord, out var list))
                    {
                        list = new List<SlotValueEntry>();
                        lookup[firstWord] = list;
                    }

                    list.Add(new SlotValueEntry { FullValue = value, Words = words, WordCount = words.Length });
                }

                // Sort each list by word count descending (longest match first)
                foreach (var list in lookup.Values)
                    list.Sort((a, b) => b.WordCount.CompareTo(a.WordCount));

                _slotLookups[i] = lookup;
            }

            // Validate all slot references in patterns
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
        }

        /// <summary>
        /// Parses input text against all registered command patterns.
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

            int bestScore = int.MinValue;
            int bestLiteralCount = -1;
            int bestCommandIdx = -1;
            List<VoskSlotMatch> bestSlots = null;

            for (int ci = 0; ci < _commands.Length; ci++)
            {
                var patterns = _commands[ci].Patterns;
                for (int pi = 0; pi < patterns.Length; pi++)
                {
                    var matchResult = TryMatch(tokens, patterns[pi]);
                    if (!matchResult.Success)
                        continue;

                    int score = matchResult.Consumed - (tokens.Length - matchResult.Consumed);
                    int literalCount = matchResult.LiteralCount;

                    if (score > bestScore ||
                        (score == bestScore && literalCount > bestLiteralCount))
                    {
                        bestScore = score;
                        bestLiteralCount = literalCount;
                        bestCommandIdx = ci;
                        bestSlots = matchResult.Slots;
                    }
                }
            }

            if (bestCommandIdx < 0)
                return new VoskCommandResult(text);

            // Compute confidence: minimum word confidence across all matched tokens
            float confidence = ComputeConfidence(tokens, bestSlots, wordConfidence);

            var slotsArray = bestSlots != null && bestSlots.Count > 0
                ? bestSlots.ToArray()
                : Array.Empty<VoskSlotMatch>();

            var command = new VoskCommand(
                _commands[bestCommandIdx].Intent,
                slotsArray,
                confidence,
                text);

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
        /// literals and slot values, plus [unk] for off-vocabulary fallback.
        /// </summary>
        public string GenerateGrammarJson()
        {
            var uniqueWords = new HashSet<string>(StringComparer.Ordinal);

            // Collect words from pattern literals
            foreach (var command in _commands)
            {
                foreach (var pattern in command.Patterns)
                {
                    foreach (string element in pattern)
                    {
                        if (ExtractSlotName(element) == null)
                        {
                            // Literal token — could be multi-word in theory, split to be safe
                            foreach (string word in element.Split(' '))
                            {
                                if (word.Length > 0)
                                    uniqueWords.Add(word);
                            }
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
            public bool Success;
            public int Consumed;
            public int LiteralCount;
            public List<VoskSlotMatch> Slots;
        }

        MatchResult TryMatch(string[] tokens, string[] pattern)
        {
            int tokenIdx = 0;
            int literalCount = 0;
            List<VoskSlotMatch> slots = null;

            for (int patIdx = 0; patIdx < pattern.Length; patIdx++)
            {
                string element = pattern[patIdx];

                // Skip [unk] tokens in input before processing each pattern element
                while (tokenIdx < tokens.Length && tokens[tokenIdx] == "[unk]")
                    tokenIdx++;

                string slotName = ExtractSlotName(element);
                bool isOptional = IsOptionalSlot(element);

                if (slotName != null)
                {
                    // Slot reference
                    if (!_slotIndex.TryGetValue(slotName, out int slotIdx))
                        return default; // Should not happen (validated at construction)

                    string matchedValue = TryMatchSlot(tokens, tokenIdx, slotIdx, out int consumed);
                    if (matchedValue != null)
                    {
                        if (slots == null)
                            slots = new List<VoskSlotMatch>();
                        slots.Add(new VoskSlotMatch(slotName, matchedValue));
                        tokenIdx += consumed;
                    }
                    else if (!isOptional)
                    {
                        return default; // Required slot failed
                    }
                    // Optional slot not matched — skip pattern element, don't advance token
                }
                else
                {
                    // Literal token (top-of-loop already skipped [unk] tokens)
                    if (tokenIdx >= tokens.Length)
                        return default;

                    if (!string.Equals(tokens[tokenIdx], element, StringComparison.Ordinal))
                        return default;

                    literalCount++;
                    tokenIdx++;
                }
            }

            return new MatchResult
            {
                Success = true,
                Consumed = tokenIdx,
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
                    // Skip [unk] tokens in the middle — not applicable for slot matching
                    // (slot values must match contiguously)
                    if (!string.Equals(tokens[startIdx + w], valueWords[w], StringComparison.Ordinal))
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    consumed = entry.WordCount;
                    return entry.FullValue;
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
        /// Handles both {slot} (required) and {?slot} (optional).
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
    }
}
