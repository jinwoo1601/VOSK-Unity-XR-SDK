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

        // Deliberately zero (issue #65 §5.1), and kept as a named constant rather than
        // deleted so this set still reads as the whole scoring model in one place.
        //
        // A missed required literal is already charged once: it takes its place in the
        // denominator (see the unconditional credit in TryMatchScored's required-literal
        // branch) while contributing nothing to the numerator. Subtracting a penalty on top
        // charged it a SECOND time, so one dropped word cost 1.5/N of a ceiling fixed at 1.0
        // instead of 1/N. Because the cost is a fraction of the pattern's length, that fell
        // hardest on exactly the short patterns least able to absorb it: "time to target"
        // heard as "time target" scored 0.50 against a 0.60 gate and did not fire at all,
        // while a 7-element pattern shrugged off the identical single-word drop at 0.79.
        // Pattern length, not pattern quality, decided whether a command survived.
        //
        // Reduced rather than abolished: the cost stays proportional to pattern length, so a
        // two-element pattern missing half its evidence still scores 1/2 and is still
        // refused. "cease fire" heard as "fire" is genuinely ambiguous with the `fire`
        // command and firing it on half its evidence would be a worse failure than silence.
        //
        // RequiredSlotMissPenalty stays at -1.0: a missing required SLOT means the command's
        // argument is absent, which is materially different from a dropped function word.
        //
        // That -1.0 is no longer the only thing holding such a candidate down. It used to be:
        // the partial/pending branch is reached by scoring BELOW minScore and only for a command
        // that opted into allowPartialMatch (off by default), so once a slot-missing candidate
        // cleared the gate it simply fired with the argument absent — true on main at five
        // elements, and this change lifted one further band (eight elements, one dropped literal
        // alongside the missed slot) over it. TryEagerCommit refused that shape (issue #66) but
        // the ordinary flush path had no such condition, which is issue #73.
        //
        // The flush path now tests completeness directly, in the recogniser and independent of
        // score (VoxrCommandRecogniser.IsIncomplete). So the arithmetic here no longer has to
        // carry a correctness guarantee it was never able to make: -1.0 stays because a missing
        // argument IS weaker evidence and should score lower, not because the score is what
        // stops the command firing.
        const float RequiredLiteralMissPenalty = 0f;

        // Weight added to the score denominator per in-grammar word the sliding start skips
        // before a match begins (issue #31). At 1.0 the score becomes the fraction of the
        // utterance the pattern actually covers, so a lone one-element pattern found in the
        // tail of a longer utterance scores 0.5 instead of a full 1.0 and falls below the
        // default minScore. 0 restores the previous behaviour (skipped words cost nothing).
        internal const float DefaultSkippedWordPenalty = 1.0f;

        // Upper bound on optional elements in a single pattern for eager-commit analysis
        // (issue #25). ExpandOptionals enumerates 2^optionals concrete forms; past this the
        // expansion is unbounded/overflowing, so the whole parser abandons the analysis
        // rather than partially (and unsoundly) analyse it. Nothing then commits early —
        // every complete match degrades to HoldExtendable (issue #44). Pathological grammars
        // only, and reported at construction by WarnOnExcessiveOptionalExpansion.
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
        // never reach an eager entry point, so they never pay the precompute. Left null when
        // the grammar is too complex to analyse soundly (see MaxOptionalExpansion) — nothing
        // commits early then, and complete matches are held instead (issue #44).
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
            WarnOnDroppableRequiredLiteral(commands);
            WarnOnExcessiveOptionalExpansion(commands);
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

        // A common pattern-set shape that silently throws away a slot value the speaker did
        // say (issue #42): a bare pattern P and a longer pattern that extends it with one or
        // more required literals followed by a slot. Drop such a literal — short function
        // words are the most dropped tokens in practice — and the longer pattern loses that
        // element's credit while still counting it in its denominator, so it drops below the
        // perfectly-matching P, which wins and the spoken slot content is discarded with
        // nothing to signal it. Issue #65 §5.1 reduced that loss (a drop now costs 1/N rather
        // than 1.5/N) but cannot close this, and no penalty tuning can: P scores a clean 1.0
        // and nothing normalized to 1.0 can beat it. Marking the literal optional does — an
        // omitted optional leaves both sides of the ratio, so the longer pattern reaches 1.0
        // whether or not the literal was spoken and takes the consumed-span tie-break
        // (issue #41) over the bare form.
        //
        // The scan mirrors what ParseInternal actually compares, which is why it is this wide:
        //   - ACROSS COMMANDS, not just within one. Selection runs over every pattern of every
        //     command through a single IsBetterCandidate, so splitting the two phrasings across
        //     two intents reproduces the hazard exactly.
        //   - Over a RUN of required literals, not a single one. Dropping any one word in
        //     "decelerate by the {burn_level}" strands the value just as "by" alone does.
        //   - Over EXPANDED optional forms, like ComputeCanCommitEarly's prefix analysis, so a
        //     bare pattern that only becomes a prefix once its own optional is omitted still
        //     counts ("fire {?quantity} {weapon}" vs "fire {weapon} at {target}").
        // Widening cost was measured on the 11-intent/32-pattern demo grammar: 0 warnings for
        // every variant, and exactly the one expected warning on the pre-#42 grammar.
        //
        // The remedy is not free, and the message must not imply it is: an optional literal
        // scores OptionalLiteralScore on both sides rather than MatchScore, so any match that
        // is already imperfect scores strictly lower than the required form would —
        // (r-0.5)/(d-0.5) < r/d for r < d. It also stops anchoring the element after it, which
        // can then claim adjacent tokens the literal never introduced.
        static void WarnOnDroppableRequiredLiteral(VoxrCommandDefinition[] commands)
        {
            int patternCount = 0;
            for (int ci = 0; ci < commands.Length; ci++)
                patternCount += commands[ci].Patterns.Length;
            if (patternCount < 2)
                return;

            // Flatten (command, pattern) into one list so the scan can pair across commands,
            // expanding each pattern's optional elements once up front.
            var intents = new string[patternCount];
            var raw = new string[patternCount][];
            var forms = new List<string[]>[patternCount];
            for (int ci = 0, n = 0; ci < commands.Length; ci++)
            {
                var patterns = commands[ci].Patterns;
                for (int pi = 0; pi < patterns.Length; pi++, n++)
                {
                    intents[n] = commands[ci].Intent;
                    raw[n] = patterns[pi];
                    forms[n] = WarningForms(patterns[pi]);
                }
            }

            // One hazard can surface from several form pairs; report each only once.
            HashSet<string> reported = null;

            for (int b = 0; b < patternCount; b++)
            {
                for (int e = 0; e < patternCount; e++)
                {
                    if (e == b)
                        continue;

                    var bareForms = forms[b];
                    var extForms = forms[e];
                    for (int bf = 0; bf < bareForms.Count; bf++)
                    {
                        string[] bare = bareForms[bf];
                        if (bare.Length == 0)
                            continue;

                        for (int ef = 0; ef < extForms.Count; ef++)
                        {
                            string[] extended = extForms[ef];
                            if (extended.Length <= bare.Length)
                                continue;
                            if (!IsElementPrefix(bare, extended))
                                continue;

                            // Walk the literals the longer form adds. At least one must be
                            // required (an optional one is already droppable for free), and a
                            // slot must follow them — that slot is what gets stranded.
                            int k = bare.Length;
                            int requiredLiterals = 0;
                            string firstRequired = null;
                            while (k < extended.Length && ExtractSlotName(extended[k]) == null)
                            {
                                if (!IsOptionalLiteral(extended[k]))
                                {
                                    requiredLiterals++;
                                    if (firstRequired == null)
                                        firstRequired = extended[k];
                                }
                                k++;
                            }
                            if (requiredLiterals == 0 || k >= extended.Length)
                                continue;

                            string message = BuildDroppableLiteralWarning(
                                intents[b], raw[b], bare,
                                intents[e], raw[e], extended,
                                firstRequired, requiredLiterals, extended[k]);

                            reported = reported ?? new HashSet<string>(StringComparer.Ordinal);
                            if (reported.Add(message))
                                UnityEngine.Debug.LogWarning(message);
                        }
                    }
                }
            }
        }

        // Concrete forms of a pattern for the warning scan. Patterns with no optional elements
        // are their own single form, with no copy. The expansion is capped well below
        // MaxOptionalExpansion because this scan runs unconditionally in the ctor — and so on
        // every parser rebuild — where ComputeCanCommitEarly's expansion is lazy; past the cap
        // the pattern is compared raw, costing recall on that one pattern only.
        const int MaxWarningExpansion = 6;

        static List<string[]> WarningForms(string[] pattern)
        {
            int optionals = CountOptionalElements(pattern);
            if (optionals == 0 || optionals > MaxWarningExpansion)
                return new List<string[]>(1) { pattern };
            return ExpandOptionals(pattern);
        }

        static string BuildDroppableLiteralWarning(
            string bareIntent, string[] bareRaw, string[] bareForm,
            string extIntent, string[] extRaw, string[] extForm,
            string firstRequired, int requiredLiterals, string slot)
        {
            // Report the patterns as authored; note when a form differs, so an author looking
            // for the quoted text in their asset is not left hunting for something else.
            string bareText = string.Join(" ", bareRaw);
            if (bareForm.Length != bareRaw.Length)
                bareText += " (with its optional elements omitted)";
            string extText = string.Join(" ", extRaw);
            if (extForm.Length != extRaw.Length)
                extText += " (with its optional elements omitted)";

            string gap = requiredLiterals == 1
                ? $"the required literal \"{firstRequired}\""
                : $"required literals including \"{firstRequired}\"";
            string sameIntent = string.Equals(bareIntent, extIntent, StringComparison.Ordinal)
                ? $"Intent '{bareIntent}' has the pattern \"{bareText}\" and the longer "
                    + $"\"{extText}\""
                : $"Pattern \"{bareText}\" (intent '{bareIntent}') is a bare form of "
                    + $"\"{extText}\" (intent '{extIntent}')";

            return $"[VoxrCommandParser] {sameIntent}, which extends it with {gap} in front of "
                + $"slot \"{slot}\". If that literal is dropped, the longer pattern loses that "
                + "element's credit while the bare one still matches perfectly — so the bare one "
                + "wins and the slot value the speaker did say is discarded silently. Make the literal "
                + $"optional (\"?{firstRequired}\") so an otherwise-complete match reaches the same "
                + "score with or without the word and wins on consumed span. That trade is not "
                + "free: an optional literal also lowers the score of matches that are already "
                + "missing something, and stops anchoring the slot behind it, which can then "
                + "claim adjacent tokens.";
        }

        // The other hazard the eager-flush analysis carries (issue #44): one pattern past
        // MaxOptionalExpansion abandons the precompute for the WHOLE command set, so no
        // command in it commits early and every complete match is held instead. That is
        // knowable from the assets alone, so it is reported here at construction — naming the
        // pattern responsible — rather than only from ComputeCanCommitEarly, which runs
        // lazily on the first eager probe and so says nothing at all until a play session
        // that has eager flush enabled reaches one.
        static void WarnOnExcessiveOptionalExpansion(VoxrCommandDefinition[] commands)
        {
            for (int ci = 0; ci < commands.Length; ci++)
            {
                var patterns = commands[ci].Patterns;
                for (int pi = 0; pi < patterns.Length; pi++)
                {
                    int optionals = CountOptionalElements(patterns[pi]);
                    if (optionals <= MaxOptionalExpansion)
                        continue;

                    string message =
                        $"[VoxrCommandParser] Pattern \"{string.Join(" ", patterns[pi])}\" "
                        + $"(intent '{commands[ci].Intent}') has {optionals} optional elements, "
                        + $"more than the {MaxOptionalExpansion} the eager-flush analysis can "
                        + "expand (it enumerates 2^optionals concrete forms). That analysis is "
                        + "then abandoned for the whole command set, not just this pattern: with "
                        + "eagerFlushOnCompleteMatch on, no command commits early — every "
                        + "complete match is held for prefixHoldSeconds where it is set, and for "
                        + "the full bufferWindow where it is not. Reduce this pattern's optional "
                        + "elements to restore early commit.";
                    UnityEngine.Debug.LogWarning(message);
                }
            }
        }

        static bool IsElementPrefix(string[] prefix, string[] pattern)
        {
            for (int i = 0; i < prefix.Length; i++)
                if (!string.Equals(prefix[i], pattern[i], StringComparison.Ordinal))
                    return false;
            return true;
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
                int bestConsumedEndIdx = 0;
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

                            if (
                                IsBetterCandidate(
                                    matchResult,
                                    startIdx,
                                    bestScore,
                                    bestStartIdx,
                                    bestConsumedEndIdx,
                                    bestLiteralCount
                                )
                            )
                            {
                                bestScore = matchResult.Score;
                                bestRawScore = matchResult.RawScore;
                                bestDenominator = matchResult.Denominator;
                                bestLiteralCount = matchResult.LiteralCount;
                                bestCommandIdx = ci;
                                bestPatternIdx = pi;
                                bestStartIdx = startIdx;
                                bestEndIdx = matchResult.EndIdx;
                                bestConsumedEndIdx = matchResult.ConsumedEndIdx;
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
            var run = new List<string>();

            // Collect contiguous literal runs from patterns. A run is emitted as one
            // multi-word grammar entry so the decoder pays a single language-model
            // transition for the whole sequence instead of one per word — that bias is
            // what re-imposes word order and resists in-grammar substitutions
            // ("switch to navigation" over "switch two navigation").
            // A slot or an optional literal ends the run: neither is guaranteed to be
            // spoken, so the words either side of it are not reliably contiguous.
            foreach (var command in commands)
            {
                foreach (var pattern in command.Patterns)
                {
                    run.Clear();

                    foreach (string element in pattern)
                    {
                        if (ExtractSlotName(element) != null)
                        {
                            AddPhrase(uniqueWords, run);
                            run.Clear();
                            continue;
                        }

                        if (IsOptionalLiteral(element))
                        {
                            AddPhrase(uniqueWords, run);
                            run.Clear();
                            AddSurfaceForm(uniqueWords, element.Substring(1));
                            continue;
                        }

                        foreach (string w in element.Split(' '))
                        {
                            if (w.Length > 0)
                                run.Add(w);
                        }
                    }

                    AddPhrase(uniqueWords, run);
                }
            }

            // Collect slot values and alias keys as whole surface forms
            foreach (var slot in slots)
            {
                foreach (string value in slot.Values)
                    AddSurfaceForm(uniqueWords, value);

                if (slot.Aliases != null)
                {
                    foreach (var key in slot.Aliases.Keys)
                        AddSurfaceForm(uniqueWords, key);
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

        // Adds a word sequence as one phrase entry, plus every word individually.
        // The single words are kept deliberately: an utterance the VAD splits
        // mid-phrase still has to decode as fragments, and a phrase-only grammar
        // could not represent them. Keeping both makes the sequence constraint a
        // bias rather than a hard rule — the phrase entry is the cheaper path.
        static void AddPhrase(HashSet<string> entries, List<string> words)
        {
            if (words.Count == 0)
                return;

            if (words.Count > 1)
                entries.Add(string.Join(" ", words));

            foreach (string word in words)
                entries.Add(word);
        }

        // Adds a whitespace-separated surface form (slot value, alias key, optional
        // literal) as a phrase entry plus its individual words.
        static void AddSurfaceForm(HashSet<string> entries, string surfaceForm)
        {
            var words = new List<string>();

            foreach (string word in surfaceForm.Split(' '))
            {
                if (word.Length > 0)
                    words.Add(word);
            }

            AddPhrase(entries, words);
        }

        struct MatchResult
        {
            public float Score;
            public float RawScore;
            public float Denominator;
            public int LiteralCount;
            public int SlotCount;

            // Whether any REQUIRED slot in the pattern matched nothing — the command is
            // therefore missing an argument. Drives the eager gate's completeness condition
            // (issue #66).
            //
            // ParseInternal still ignores it, but NOT because a slot-missing candidate is
            // routed anywhere from here — an earlier version of this comment claimed that and
            // was wrong (issue #73). It ignores it because Parse is the reporting layer and
            // applies no threshold of its own: dropping such candidates here would also delete
            // the only input the allowPartialMatch/pending path has, since that path is fed
            // precisely by slot-missing candidates scoring below minScore. The flush path's
            // completeness condition therefore lives in the recogniser, which is where the
            // gate it belongs beside lives.
            public bool MissedRequiredSlot;

            // Whether any REQUIRED element sits after the last element that actually
            // matched — i.e. the pattern ran out of buffer still owing words, rather than
            // ending where the buffer ends. Neither index below can express this: a miss
            // consumes nothing, so EndIdx stops exactly where a pattern that genuinely
            // finished there would stop. Trailing [unk] only makes it harder to see, not
            // easier — the skip before each element carries EndIdx over filler that
            // ConsumedEndIdx never reaches, so the whole-buffer check passes more readily
            // still while the pattern is even further from complete. Drives the eager gate's second
            // completeness condition (issue #70); ParseInternal ignores it for the same
            // reporting-layer reason it ignores MissedRequiredSlot.
            //
            // Unlike that one, this flag has no flush-path counterpart, and deliberately so.
            // At the eager gate a required tail means the speaker may still be mid-utterance,
            // so refusing costs only latency. On the flush path the transcript is final: the
            // word is simply gone, and refusing means firing nothing — which is the class the
            // reduced literal miss cost (issue #65 §5.1) exists to rescue. That case is the
            // dropped discriminator, recorded in KNOWN_LIMITATIONS.md rather than guarded here.
            public bool HasUnmatchedRequiredTail;

            // Where the match stopped, including any [unk] skipped ahead of a trailing
            // element that matched nothing. Drives searchStart and the eager whole-buffer gate.
            public int EndIdx;

            // Where the last actually-matched element left off. Never counts trailing
            // filler, which is what makes it the honest span for tie-breaking.
            public int ConsumedEndIdx;

            // Required elements matched and missed (issue #65, DR-7). Optional elements count
            // toward neither side, which is NOT the denominator's rule: an omitted optional
            // leaves both sides of the ratio, but a MATCHED one credits both (+0.5 for a
            // literal, +1.0 for a slot). So this is a second, deliberately different ledger.
            //
            // Omitting an optional is not evidence against a candidate — that half is
            // uncontroversial. Matching one is not counted as evidence FOR it either, because
            // an optional the author marked skippable says nothing about whether the speaker
            // said the command the REQUIRED elements identify. The consequence is that DR-7
            // can refuse a candidate whose score would have passed; that needs more than 2.0
            // of matched-optional credit to bite, which no pattern in this package carries.
            public int MatchedRequired;
            public int MissedRequired;
        }

        // Candidate ordering, shared by ParseInternal and TryEagerCommit so the eager
        // verdict always names the pattern the subsequent flush will fire. Earliest start
        // wins, then highest score, then the longer consumed span, then literal count,
        // with registration order as the final deterministic fallback.
        //
        // The span term is issue #41: a tailed pattern and its bare sibling
        // ("intercept track {track} {burn_level}" / "intercept track {track}") both score
        // 1.0 with equal literal counts on an utterance carrying the tail, so without it
        // the winner was whichever the asset happened to list first. When the bare one
        // won, sequential extraction then matched the orphaned tail as a *second* command
        // — splitting one order in two with no warning.
        //
        // Span is compared on ConsumedEndIdx, not EndIdx, so a pattern cannot win by
        // absorbing trailing [unk] it never matched. Note the term sits ABOVE literal
        // count: it therefore also settles equal-score candidates whose literal counts
        // differ, which literal count used to decide on its own. That is a real behaviour
        // change beyond the order-dependent ties, not just a fallback for them.
        static bool IsBetterCandidate(
            in MatchResult candidate,
            int startIdx,
            float bestScore,
            int bestStartIdx,
            int bestConsumedEndIdx,
            int bestLiteralCount
        )
        {
            if (candidate.Score <= 0f)
                return false;

            // Admission: more evidence FOR the candidate than against it (issue #65, DR-7).
            //
            // Until §5.1 something like this was enforced by accident, though NOT this rule.
            // RequiredLiteralMissPenalty was -0.5, so the filter above discarded a candidate
            // once its misses reached TWICE its matches; this refuses at more misses than
            // matches, which is strictly stronger. The band between the two — two matched
            // against three missed, say — was admitted before and is refused now. That is
            // deliberate: in that band the old model was not merely lenient but incoherent,
            // since a fragment surviving there consumes the tokens ahead of a real command
            // and hands it a clean score, so ADDING debris to an utterance could raise the
            // command's score and flip it from rejected to fired.
            //
            // Zeroing the penalty removed even that accidental enforcement, and the effect was
            // NOT confined to extra low-scoring tail results:
            // the start-index key below outranks score, so a newly-admitted fragment can win
            // round 1 outright. It then consumes tokens, which moves the origin ParseInternal
            // charges skipped words from (issue #31) and takes a slot in the fixed result
            // buffer — so "alpha one weapons mode" fired mode_weapons at a full 1.00 where the
            // skipped-word charge had correctly held it to 0.50, and a genuine command later
            // in a multi-command utterance could be evicted entirely.
            //
            // Stated as a rule rather than left to the arithmetic, this says: a pattern that
            // missed more of its required elements than it matched is not a candidate. It is
            // deliberately a COUNT, not a score threshold — no knob, nothing to configure, and
            // independent of the coverage term that will later enter the score.
            if (candidate.MissedRequired > candidate.MatchedRequired)
                return false;
            if (bestScore <= 0f)
                return true;
            if (startIdx != bestStartIdx)
                return startIdx < bestStartIdx;
            if (candidate.Score != bestScore)
                return candidate.Score > bestScore;
            if (candidate.ConsumedEndIdx != bestConsumedEndIdx)
                return candidate.ConsumedEndIdx > bestConsumedEndIdx;
            return candidate.LiteralCount > bestLiteralCount;
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
            // Evidence for and against, counted over REQUIRED elements only (DR-7). These
            // feed the admission rule in IsBetterCandidate, not the score.
            int matchedRequired = 0;
            int missedRequired = 0;
            bool missedRequiredSlot = false;
            // Required elements that have missed since the last one that actually matched.
            // Reset by every match, so a non-zero value at the end means the pattern's TAIL
            // is what went unmatched (issue #70) — a medial miss is followed by a match and
            // clears it.
            int requiredAfterLastMatch = 0;
            // Where the last element that actually matched something left off. EndIdx alone
            // overstates the span: the [unk] skip below runs before every element, including
            // one that then matches nothing, so a trailing unmatched optional leaves EndIdx
            // past filler the pattern never consumed. Tie-breaking on that would let a
            // pattern win by absorbing noise (issue #41 review), so the comparison uses this
            // instead — EndIdx keeps its own meaning for searchStart and the eager gate.
            int consumedEndIdx = startIdx;

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
                        consumedEndIdx = tokenIdx;
                        rawScore += MatchScore;
                        denominator += MatchScore;
                        if (!isOptional)
                            matchedRequired++;
                        requiredAfterLastMatch = 0;
                    }
                    else if (!isOptional)
                    {
                        rawScore += RequiredSlotMissPenalty;
                        denominator += MatchScore;
                        missedRequired++;
                        missedRequiredSlot = true;
                        // Currently dominated: missedRequiredSlot refuses an eager commit one
                        // guard earlier, so this increment can never be the sole cause of a
                        // refusal. Kept so the field stays true to its name — "any required
                        // ELEMENT after the last match" — and so the tail condition does not
                        // quietly depend on the slot guard's existence or its placement.
                        requiredAfterLastMatch++;
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
                        consumedEndIdx = tokenIdx;
                        requiredAfterLastMatch = 0;
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
                        matchedRequired++;
                        tokenIdx++;
                        consumedEndIdx = tokenIdx;
                        requiredAfterLastMatch = 0;
                    }
                    else
                    {
                        // Adds nothing: the constant is zero on purpose (see its declaration,
                        // which carries the reasoning). The whole cost of the miss is the
                        // denominator credit taken above, which this element keeps whether or
                        // not it matched.
                        rawScore += RequiredLiteralMissPenalty;
                        missedRequired++;
                        requiredAfterLastMatch++;
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
                MissedRequiredSlot = missedRequiredSlot,
                HasUnmatchedRequiredTail = requiredAfterLastMatch > 0,
                EndIdx = tokenIdx,
                ConsumedEndIdx = consumedEndIdx,
                MatchedRequired = matchedRequired,
                MissedRequired = missedRequired,
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

        // Whether a parsed command left any REQUIRED slot of its matched pattern unfilled —
        // i.e. the command's argument is absent (issue #73). Answered from the finished
        // VoxrCommand rather than from MatchResult.MissedRequiredSlot because the two flush-path
        // consumers that need it (the recogniser's gate, the batch runner's) only ever see the
        // former; MatchResult is a parse-loop internal that ParseInternal discards field by
        // field at :618-627. The two agree by construction: a required slot that matched nothing
        // is exactly a required slot name the command carries no value for.
        //
        // Optional slots are excluded — an omitted optional is not a missing argument, which is
        // the same line #66 draws at the eager gate.
        //
        // Returns false when the pattern index is out of range, matching ComputeUnfilledSlots:
        // with no pattern to read we cannot tell, and the conservative answer is to leave the
        // caller's existing behaviour alone rather than refuse a command on a guess.
        internal static bool HasUnfilledRequiredSlot(VoxrCommand cmd, VoxrCommandDefinition def)
        {
            // Patterns is null only on a default(VoxrCommandDefinition) — the struct's zero
            // value, which a failed lookup yields. Checked first so the length test below cannot
            // dereference it.
            if (
                def.Patterns == null
                || cmd.MatchedPatternIndex < 0
                || cmd.MatchedPatternIndex >= def.Patterns.Length
            )
                return false;

            var pattern = def.Patterns[cmd.MatchedPatternIndex];
            for (int i = 0; i < pattern.Length; i++)
            {
                string slotName = ExtractSlotName(pattern[i]);
                if (slotName != null && !IsOptionalSlot(pattern[i]) && !cmd.HasSlot(slotName))
                    return true;
            }

            return false;
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
            int bestConsumedEndIdx = 0;
            bool bestMissedRequiredSlot = false;
            bool bestHasUnmatchedRequiredTail = false;

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

                        if (
                            IsBetterCandidate(
                                matchResult,
                                startIdx,
                                bestScore,
                                bestStartIdx,
                                bestConsumedEndIdx,
                                bestLiteralCount
                            )
                        )
                        {
                            bestScore = matchResult.Score;
                            bestLiteralCount = matchResult.LiteralCount;
                            bestCommandIdx = ci;
                            bestPatternIdx = pi;
                            bestStartIdx = startIdx;
                            bestConsumedEndIdx = matchResult.ConsumedEndIdx;
                            bestEndIdx = matchResult.EndIdx;
                            bestMissedRequiredSlot = matchResult.MissedRequiredSlot;
                            bestHasUnmatchedRequiredTail = matchResult.HasUnmatchedRequiredTail;
                        }
                    }
                }
            }

            if (bestCommandIdx < 0 || bestScore < minScore)
                return EagerCommitVerdict.None;

            // Note one condition that is NOT listed below because it has already run: the
            // admission rule in IsBetterCandidate (issue #65, DR-7) refuses any candidate
            // whose missed required elements outnumber its matched ones, so nothing that
            // sparse ever reaches these checks or the score gate above. The eager scan
            // inherits it by sharing the comparator with ParseInternal.
            //
            // Completeness: every required SLOT must actually have matched (issue #66).
            // Nothing else here asserts that. The score arithmetic only sinks such candidates
            // below minScore by coincidence — at five elements one missed slot lands on
            // exactly 0.60, which clears the default gate — and the end-of-buffer condition
            // below cannot catch it either: a miss consumes no RECOGNISED token, so it never
            // advances EndIdx past anything the pattern actually matched. (It can still carry
            // EndIdx over a trailing [unk] run, since the skip above runs before every element
            // including one that then matches nothing — which only makes that condition pass
            // more readily.) The buffer therefore looks fully spanned while the command is
            // still missing an argument, and committing fires it right before the words that
            // would have filled the slot arrive.
            //
            // Required LITERALS are not covered by this condition — a dropped function word
            // still leaves every argument present. They are covered by the tail condition
            // below instead, which is the case that actually matters for them.
            if (bestMissedRequiredSlot)
                return EagerCommitVerdict.None;

            // Completeness, part two: no required element may sit after the last element
            // that actually MATCHED (issue #70). The whole-buffer condition below cannot
            // express this — a miss consumes no token, so a pattern whose trailing elements
            // were never spoken leaves EndIdx (and ConsumedEndIdx) at the buffer end just
            // as a pattern that genuinely ended there does. That condition is then satisfied
            // vacuously by exactly the utterances still in progress.
            //
            // Left unguarded, ["switch", "to", "weapons"] commits on the buffer "switch to"
            // while the speaker is still saying "navigation" — and where a sibling pattern
            // shares the prefix, both score identically and registration order picks the
            // committed intent, so the wrong command fires, not merely an early one.
            //
            // This is a TAIL test rather than a ban on missed literals because a medial
            // miss is genuinely safe: "launch all missiles hotel one" against
            // ["launch", "{?quantity}", "{weapon}", "target", "{target}"] drops the "target"
            // function word, yet fills every slot and still lands its final element on the
            // last token of the buffer. Nothing the speaker says next was owed to it, so it
            // commits early exactly as before.
            if (bestHasUnmatchedRequiredTail)
                return EagerCommitVerdict.None;

            // The match must span the whole buffer from the first recognised token: anything
            // left over at the END (including trailing [unk]) means an in-progress tail that
            // more speech could still complete. A LEADING [unk] run carries no such ambiguity
            // — nothing arriving later extends the utterance leftward — so out-of-grammar
            // preamble ("Helm, coast") is skipped rather than blocking the commit (issue #43),
            // matching the sliding start that already absorbs it for free everywhere else.
            //
            // Only [unk] may precede the match. ParseInternal charges skipped *recognised*
            // words against the score after selection (issue #31) and this scan does not, so
            // relaxing this to tolerate any leading leftover would let the gate commit a
            // buffer the subsequent flush then scores below minScore. [unk] is exempt from
            // that penalty, which is what keeps the two scores identical here.
            int firstRecognisedIdx = 0;
            while (firstRecognisedIdx < tokens.Length && tokens[firstRecognisedIdx] == UnkToken)
                firstRecognisedIdx++;

            if (bestStartIdx != firstRecognisedIdx || bestEndIdx != tokens.Length)
                return EagerCommitVerdict.None;

            float confidence = ComputeConfidence(tokens, bestStartIdx, bestEndIdx, wordConfidence);
            if (confidence >= 0f && confidence < minConfidence)
                return EagerCommitVerdict.None;

            // _canCommitEarly is legitimately null when the grammar was too complex to
            // analyse (MaxOptionalExpansion). Nothing may commit early then — that verdict is
            // exactly what the missing analysis would have vetted — but everything above has
            // already established what HoldExtendable asserts: one complete, confident match
            // spanning the buffer. So report the hold rather than None (issue #44). It is the
            // conservative side either way — nothing commits early — and it costs the
            // un-analysable grammar the short prefixHoldSeconds wait instead of the full
            // bufferWindow. Grammars that leave prefixHoldSeconds at 0 hold the full window,
            // exactly as before.
            if (_canCommitEarly == null)
                return EagerCommitVerdict.HoldExtendable;

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
            // the limit, abandon the analysis for the whole parser (return null -> CanCommitEarly
            // reports false, TryEagerCommit holds) rather than partially analyse — an un-expanded
            // pattern used as the "longer" side would unsoundly let a real prefix through and
            // fire the wrong command. The author already heard about this at construction
            // (WarnOnExcessiveOptionalExpansion), so this path stays silent.
            for (int ci = 0; ci < _commands.Length; ci++)
            {
                var patterns = _commands[ci].Patterns;
                for (int pi = 0; pi < patterns.Length; pi++)
                {
                    if (CountOptionalElements(patterns[pi]) > MaxOptionalExpansion)
                        return null;
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
