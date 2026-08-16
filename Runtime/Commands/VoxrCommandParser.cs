// ============================================================================
// Purpose:  Pure C# pattern matcher: scores tokenized VOSK output against command patterns
// Layer:    Runtime.Commands
// Owns:     VoxrCommandParser (internal class), EagerCommitVerdict (internal enum), SiblingMember, SiblingSet (internal readonly structs), TiedSiblingRival, TiedSiblingRecord (internal structs)
// Depends:  VoxrSlotDefinition, VoxrCommandDefinition, VoxrSlotMatch, VoxrCommand, VoxrCommandResult, VoxrNumberParser, VoxrFollowUpVocabulary
// ============================================================================
using System;
using System.Collections.Generic;
using System.Text;

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

    // One pattern's membership of a sibling set: which pattern it is, and the discriminating
    // literal it carries at the position the set differs on.
    internal readonly struct SiblingMember
    {
        public readonly int CommandIndex;
        public readonly int PatternIndex;
        public readonly string Intent;
        public readonly string Value;

        // Where the discriminating literal sits in the pattern the AUTHOR wrote, which is not
        // in general where it sits in the set's frame (issue #91). The frame is one EXPANSION's
        // shape, so a member whose form omitted an optional element carries the value at a
        // later authored position than the frame's DiscriminatorIndex names — and the warning
        // quotes the authored pattern while printing the frame's number, so it can point at a
        // different word than the one it is about.
        public readonly int AuthoredDiscriminatorIndex;

        public SiblingMember(
            int commandIndex,
            int patternIndex,
            string intent,
            string value,
            int authoredDiscriminatorIndex
        )
        {
            CommandIndex = commandIndex;
            PatternIndex = patternIndex;
            Intent = intent;
            Value = value;
            AuthoredDiscriminatorIndex = authoredDiscriminatorIndex;
        }
    }

    // One sibling rival that tied the flush's winner: which pattern it was, the word that tells
    // it apart from the winner, and enough of its own match to fire it if the speaker picks it.
    internal struct TiedSiblingRival
    {
        public int CommandIndex;
        public int PatternIndex;

        // This rival's discriminating value — what the speaker says to choose it (issue #74
        // DR-4: the discriminating values ARE the choice vocabulary).
        public string Value;

        public int SlotCount;

        // Its own, not the winner's: the tie compares ConsumedEndIdx, not EndIdx, so two tied
        // candidates can differ by trailing [unk] neither consumed.
        public int EndIdx;
    }

    // What the flush loop learned about the winner's sibling rivals in one extraction round.
    // Parallel to VoxrCommandResult in the result buffer: index i of one describes index i of
    // the other.
    //
    // NOT a field on VoxrCommandResult, which is a PUBLIC readonly struct — widening it would
    // be a public API change nobody asked for.
    //
    // Sibling-only, in both the Editor and a player, and that is forced rather than preferred:
    // DR-4 makes the discriminating values the choice vocabulary, and a non-sibling tie has no
    // discriminating values — that is what "not siblings" means. Recording one would produce a
    // question the speaker cannot answer. Design §5.3's promise that non-sibling ties stay
    // "visible to the editor diagnostic" is the half this design never builds; issue #95
    // carries it, and states that closing it needs a record separating "a rival tied" from
    // "a SIBLING rival tied" rather than a relaxed gate.
    internal struct TiedSiblingRecord
    {
        // 0 when nothing tied. Reset at the top of every extraction round — a stale record
        // surviving into a round that found no tie is exactly the failure item 2's review
        // caught when these locals were hoisted out of the extraction loop.
        public int RivalCount;

        // The ONE sibling set this question is about. A pattern can belong to several sets —
        // ["a","b","c"] is a sibling of ["a","b","d"] at position 2 and of ["a","x","c"] at
        // position 1 — so without fixing the set, an n-ary rival list could mix a rival that
        // differs at position 2 with one that differs at position 1: two different questions,
        // two different winner values, presented as one choice list.
        public int SetId;

        // The winner's own value in that set.
        public string WinnerValue;

        // Shared by every tied candidate, because CompareCandidate returns Tied only when
        // startIdx == bestStartIdx. Recorded rather than derived because ComputeConfidence takes
        // a SPAN, so an alternative's confidence needs both ends and EndIdx alone is not enough.
        public int StartIdx;

        // More rivals tied than the cap holds, so the choices offered are the first N. Surfaced
        // to the integrator; the author was told at construction, where they can still act.
        public bool Truncated;
    }

    // Patterns that are element-wise equal but for one position, where each holds a required
    // literal (issue #74 design DR-1). Frame carries the shared elements, normalized, with
    // null at DiscriminatorIndex.
    internal readonly struct SiblingSet
    {
        public readonly int DiscriminatorIndex;
        public readonly string[] Frame;
        public readonly SiblingMember[] Members;

        public SiblingSet(int discriminatorIndex, string[] frame, SiblingMember[] members)
        {
            DiscriminatorIndex = discriminatorIndex;
            Frame = frame;
            Members = members;
        }
    }

    internal class VoxrCommandParser
    {
        internal const string UnkToken = "[unk]";

        const float MatchScore = 1.0f;
        const float OptionalLiteralScore = 0.5f;
        const float RequiredSlotMissPenalty = -1.0f;

        // Mirrors VoxrCommandRecogniser's serialized default. Used only by the construction-
        // time sibling scan, which has to judge whether a tie could clear the gate but is not
        // handed the configured threshold. Kept in sync by
        // SiblingWarning_ReachabilityGateTracksTheRecogniserDefault.
        const float DefaultMinScore = 0.6f;

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

        // Weight added to the score denominator per in-grammar token a match leaves
        // unexplained: those the sliding start skipped to reach it (issue #31) and those left
        // orphaned after it (issue #65 §5.2). At 1.0 a one-element pattern found in the tail
        // of a longer utterance scores 0.5 and falls below the default minScore.
        //
        // Close to "the fraction of the utterance the pattern accounts for", but not exactly:
        // trailing tokens that could begin another pattern are not charged, so "cease fire"
        // keeps a full 1.0 in "cease fire launch missiles target hotel one" while accounting
        // for two tokens of seven. That amnesty is what makes multi-command utterances work.
        //
        // Renamed from skippedWordPenalty by DR-4, because one weight now governs both sides
        // and the old name described only the leading half. The rename also stops hiding a
        // coupling: setting this to 0 restores pre-#31 behaviour AND silently switches off
        // the issue #42 fix, which "coverage weight" at least admits to.
        internal const float DefaultCoverageWeight = 1.0f;

        // Kept one minor version as DR-4 prescribes. Inert in practice: the type is internal
        // and InternalsVisibleTo names only first-party assemblies, so there is no external
        // call site for the warning to reach.
        [Obsolete(
            "Renamed to DefaultCoverageWeight — the weight governs orphaned trailing tokens "
                + "as well as leading skipped ones (issue #65 §5.2)."
        )]
        internal const float DefaultSkippedWordPenalty = DefaultCoverageWeight;

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
        readonly float _coverageWeight;

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

        // Grammar-derived answer to "could any active pattern begin a match at this token?"
        // (issue #65 §5.2). Coverage charges a candidate for the in-grammar tokens it leaves
        // unexplained AFTER its match, and that count stops at the first token which could
        // begin some other pattern. Stopping there is what keeps sequential extraction
        // intact: "cease fire launch missiles target hotel one" must charge cease_fire
        // nothing, not five, because the launch is a command in its own right.
        //
        // Derived from the registered patterns ALONE, never from which candidates survived
        // admission. Defining it over admitted candidates is the tempting shortcut — the
        // selection loop already has them in hand — but it would couple coverage to the
        // admission rule: rejecting one candidate would withdraw a pattern's claim on a
        // token, turn that token into an orphan, and lower a DIFFERENT candidate's score.
        //
        // Built here in the constructor rather than alongside the _canCommitEarly analysis,
        // which is lazy and reached only from the eager entry points. Those are never
        // touched at the shipped default (eagerFlushOnCompleteMatch = false), so caches
        // built there would be empty on an ordinary parse, every trailing token would be
        // charged, and sequential extraction would break exactly as above. Invalidation is
        // automatic: an active-set change rebuilds the whole parser.
        readonly HashSet<string> _startLiterals;
        readonly int[] _startSlots;

        // Per-utterance coverage tables, rebuilt by BuildCoverageTables at the top of every
        // parse and reused across that parse's extraction rounds. Grown on demand and never
        // shrunk. Like _matchSlotBuf / _resultBuf they are allocated off the parse path, so
        // coverage adds no per-utterance allocation (those two are sized once instead).
        //
        //   _recognisedPrefix[i]  — non-[unk] tokens in [0, i), so the words a round's sliding
        //                           start walked past are one subtraction instead of a scan.
        //   _orphanRun[i]         — tokens left unexplained in the run starting at i.
        //   _forcedOrphanRun[i]   — the same, under Amendment A3, for a candidate that
        //                           mis-predicted the token at i (see BuildCoverageTables).
        //
        // All are functions of the token array and the grammar alone and are complete before
        // any candidate is scored, so no candidate's score depends on evaluation order.
        int[] _recognisedPrefix;
        int[] _orphanRun;
        int[] _forcedOrphanRun;

        // The array the tables above were built for, so the coupling between them can be
        // asserted rather than merely described. Not used for scoring.
        string[] _coverageTokens;

        // Pre-allocated slot match buffers — avoids per-call List allocations in TryMatchScored/Parse.
        readonly int _maxSlotsPerPattern;
        readonly VoxrSlotMatch[] _matchSlotBuf;   // TryMatchScored writes here
        readonly VoxrSlotMatch[] _bestSlotBuf; // copy-on-best in Parse
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

        // Sibling-set lookup keyed by [commandIdx][patternIdx], which is the key design §5.1
        // specifies and the reason SiblingMember carries those indices. Null at a pattern that
        // belongs to no set — the common case; most patterns of the demo grammar are in none.
        //
        // The leaf is an ARRAY because one pattern can belong to several sets: ["a","b","c"] is
        // a sibling of ["a","b","d"] at position 2 and of ["a","x","c"] at position 1, and a
        // scalar id would silently keep one hazard and lose the other.
        //
        // Lazy for the reason _canCommitEarly is (see the note at the end of the constructor):
        // a parser rebuilt on every slot change but never asked for an eager verdict should not
        // pay for this. In the Editor the construction-time warning consumes the same lookup,
        // so there it is built at construction and built ONCE — not once per consumer, which is
        // what DR-2 asks for.
        SiblingMembership[][][] _siblingMemberships;

        // Per pattern: were its optional expansions truncated by MaxWarningExpansion? If so its
        // sibling relations are unknown rather than absent, and AreSiblingRivals refuses on a
        // cross-intent tie rather than claiming there is no hazard.
        bool[][] _siblingFormsTruncated;
        bool _siblingLookupComputed;

        // Read only by the construction-time warning, which is [Conditional("UNITY_EDITOR")].
        // Declared unconditionally because that attribute elides the CALL, not the method body,
        // which still has to compile in a player — but ASSIGNED only in the Editor, so a player
        // carries one null reference here rather than every frame and member array the grammar
        // produced, for the parser's lifetime, with nothing able to read them.
        //
        // That asymmetry is exactly what CS0649 exists to report, and here it is a false
        // positive: the only reader's only call site is elided in the same configuration that
        // drops the assignment. Suppressed at the declaration rather than left to print into
        // every consumer's player build — this package ships as source and compiles inside
        // their projects, so its warnings become theirs.
#pragma warning disable CS0649
        List<SiblingSet> _siblingSets;
#pragma warning restore CS0649

        // How many sibling rivals one choice list can hold, and so how large the preallocated
        // rival buffers are. Chosen against MEASURED set sizes, not guessed: on the shipped demo
        // grammar the largest sibling set spans four distinct discriminating values
        // ("close"/"set"/"make"/"open" distance {range} target {target}), which needs three
        // rivals; every cross-intent set there spans two. The largest fixture across Tests~ is
        // three. Four leaves a set clear of the largest measured shape with room to spare, at a
        // cost of _resultBuf.Length * 4 rival records and the matching slot slab.
        //
        // A set with more members than this offers the first four choices and sets Truncated —
        // never a silent cap. The author is told at construction (WarnOnSiblingDiscriminator),
        // where set sizes are already known and they can still act on it.
        internal const int MaxDisambiguationRivals = 4;

        // Whether the flush loop records which sibling rival tied the winner. The Editor always
        // records — the parse diagnostic reads it — while a player records only when the
        // recogniser was configured with disambiguateSiblingTies, because with the flag off
        // nothing reads it and DR-7 promises the opt-in costs a flag-off player nothing.
        //
        // readonly and set at construction, so the branch it guards predicts perfectly.
        readonly bool _recordSiblingTies;

        // Parallel to _resultBuf: index i describes the result at index i. Rival detail lives in
        // flat preallocated arrays rather than inside the struct so nothing allocates per round.
        TiedSiblingRecord[] _tiedSiblingBuf;
        TiedSiblingRival[] _tiedRivalBuf; // _resultBuf.Length * MaxDisambiguationRivals
        VoxrSlotMatch[] _rivalSlotBuf; // ...that, times _maxSlotsPerPattern

        // The cancel words the recogniser will actually match against, resolved once here so
        // the construction-time collision report tests the same array TryHandleConfirmCancel
        // will. Never null: an unset or empty override falls back to DefaultCancel.
        readonly string[] _effectiveCancelVocabulary;

        // Whether that array came from the caller rather than the default. Read only by the
        // collision message, which has to name the right source and offer the remedy that still
        // applies — "override cancelVocabulary" is not advice for an author who already did.
        readonly bool _cancelVocabularyIsOverridden;

        // Pooled StringBuilder for TryMatchNumberSequence.
        readonly System.Text.StringBuilder _numberSb = new System.Text.StringBuilder();

        struct SlotValueEntry
        {
            public string CanonicalValue;
            public string[] Words;
            public int WordCount;
        }

        // additionalGrammarWords are words the caller also put in the DECODER's grammar but
        // that appear in no pattern — in practice the confirm/cancel follow-up vocabulary
        // (VoxrCommandRecogniser.GetFollowUpGrammarWords). They matter to coverage because the
        // decoder returns them as real tokens rather than [unk], so without them the trailing
        // term charges a speaker for saying "disengage, yes": nothing can begin a match at
        // "yes", so it reads as an orphan and sinks a command that used to fire. They can
        // legitimately begin something — a follow-up — so they terminate an orphan run exactly
        // as a pattern start does. Null is correct for a caller that registered no follow-up
        // vocabulary with the decoder either.
        //
        // effectiveCancelVocabulary is the caller's configured cancel words, so the
        // construction-time collision report is computed against the vocabulary that will
        // actually run rather than against DefaultCancel (item 1's architecture §4.1, deferred
        // to issue #74 item 3). Optional, and null/empty falls back to the default exactly as
        // PendingCommandHandler.TryHandleConfirmCancel does — the report and the behaviour it
        // predicts read from one rule. It is a CONSTRUCTOR parameter rather than a settable
        // property because WarnOnSiblingDiscriminator runs inside this constructor, so a
        // property would be assigned after the warning it governs had already been emitted.
        //
        // recordSiblingTies is the recogniser's disambiguateSiblingTies. Also a constructor
        // parameter, and for a sharper reason: VoxrCommandRecogniser builds a NEW parser on
        // Configure and RebuildParser, so a post-construction setter is one call site away from
        // a silently flag-off parser after a rebuild.
        public VoxrCommandParser(VoxrSlotDefinition[] slots, VoxrCommandDefinition[] commands,
            float coverageWeight = DefaultCoverageWeight,
            string[] additionalGrammarWords = null,
            string[] effectiveCancelVocabulary = null,
            bool recordSiblingTies = false
        )
        {
            if (slots == null) throw new ArgumentNullException(nameof(slots));
            if (commands == null) throw new ArgumentNullException(nameof(commands));

            _slots = slots;
            _commands = commands;
#if UNITY_EDITOR
            // The Editor records unconditionally: ParseDiagnosticEntry's sibling fields read the
            // same record, and item 2's five diagnostic tests assert them with no flag set.
            _recordSiblingTies = true;
#else
            _recordSiblingTies = recordSiblingTies;
#endif
            _cancelVocabularyIsOverridden =
                effectiveCancelVocabulary != null && effectiveCancelVocabulary.Length > 0;
            _effectiveCancelVocabulary = _cancelVocabularyIsOverridden
                ? effectiveCancelVocabulary
                : VoxrFollowUpVocabulary.DefaultCancel;
            // Rejects negatives and NaN, and also non-finite positives: at +infinity a
            // candidate with nothing unexplained computes 0 * infinity = NaN, and NaN slips
            // through every `<= 0f` floor because those comparisons are false for NaN.
            _coverageWeight =
                coverageWeight > 0f && !float.IsInfinity(coverageWeight) ? coverageWeight : 0f;

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

            // Which literals and slots can be the FIRST thing a pattern matches. The walk
            // takes each element from index 0 and continues past it only while that element
            // is OPTIONAL — an omitted optional lets the element behind it legitimately begin
            // the match — stopping at and including the first required one.
            _startLiterals = new HashSet<string>(StringComparer.Ordinal);
            var startSlots = new List<int>();
            foreach (var command in commands)
            {
                foreach (var pattern in command.Patterns)
                {
                    foreach (string element in pattern)
                    {
                        bool optional;
                        if (_slotNameCache.TryGetValue(element, out string slotName))
                        {
                            optional = _optionalSlotElements.Contains(element);
                            // Slot references were validated above, so this cannot miss.
                            int slotIdx = _slotIndex[slotName];
                            if (!startSlots.Contains(slotIdx))
                                startSlots.Add(slotIdx);
                        }
                        else if (IsOptionalLiteral(element))
                        {
                            optional = true;
                            // The STRIPPED form: "?mark" can only ever match the token "mark",
                            // and storing the raw element would add a string no utterance can
                            // contain, silently weakening the predicate.
                            _startLiterals.Add(_optionalLiteralCache[element]);
                        }
                        else
                        {
                            optional = false;
                            _startLiterals.Add(element);
                        }

                        if (!optional)
                            break;
                    }
                }
            }
            // Words the decoder can return that begin no pattern but do begin a follow-up.
            // Folded in here rather than kept separate because the predicate's question is
            // "could anything the grammar knows about start here?", and the answer for a
            // confirm/cancel word is yes.
            if (additionalGrammarWords != null)
            {
                foreach (string word in additionalGrammarWords)
                {
                    if (!string.IsNullOrEmpty(word))
                        _startLiterals.Add(word);
                }
            }

            _startSlots = startSlots.ToArray();

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

            // Parallel to _resultBuf, and preallocated with it: the flush loop is the innermost
            // path over every (command, pattern, startIdx) triple, so recording a rival must not
            // allocate there. Sized unconditionally rather than under _recordSiblingTies purely
            // for constructor simplicity — the recording site's first conjunct is already
            // _recordSiblingTies, so nothing can reach these when they are unallocated, and no
            // hot-path null check would be needed either way.
            _tiedSiblingBuf = new TiedSiblingRecord[_resultBuf.Length];
            _tiedRivalBuf = new TiedSiblingRival[_resultBuf.Length * MaxDisambiguationRivals];
            _rivalSlotBuf = new VoxrSlotMatch[
                _resultBuf.Length * MaxDisambiguationRivals * _maxSlotsPerPattern
            ];

            // _canCommitEarly is computed lazily on the first eager check (issue #25), so
            // callers who leave eager flush off never pay the O(2^optionals)+O(forms^2)
            // precompute on Configure/RebuildParser/NotifySlotChanged.

            // OUTSIDE any Editor gate, and that placement is the whole feature working or not.
            // AreSiblingRivals reads _siblingMemberships, which only EnsureSiblingLookup builds;
            // its other callers are WarnOnSiblingDiscriminator ([Conditional("UNITY_EDITOR")],
            // so elided in a player) and TryEagerCommit (reached only when
            // eagerFlushOnCompleteMatch is set, and it defaults to false). Reachable only from
            // the Editor, a shipped player would never assign _siblingMemberships, every
            // AreSiblingRivals call would short-circuit on its null check, no rival would ever
            // be recorded, and the flush would fire the first-registered sibling exactly as
            // before — the feature working in the Editor and doing nothing in a shipped game.
            //
            // No Unity test can catch that: Unity always defines UNITY_EDITOR, so EditMode and
            // PlayMode pass either way. The A/B rig is the only build here in the player
            // configuration, which is why the flag's gating is asserted there.
            //
            // Built at CONSTRUCTION rather than on the first parse. The laziness elsewhere
            // exists so a parser rebuilt on every slot change but never asked for an eager
            // verdict does not pay the precompute — but with recordSiblingTies set, every parse
            // asks, so deferring it only moves an O(commands x forms) build with a Dictionary
            // and per-bucket lists out of construction and into the first utterance's latency
            // window. Guarded by _recordSiblingTies, so item 2's cost decision survives intact
            // for a flag-off player: it still never builds the lookup.
            if (_recordSiblingTies)
                EnsureSiblingLookup();

            RunValidationWarnings(slots);
            WarnOnDroppableRequiredLiteral(commands);
            WarnOnExcessiveOptionalExpansion(commands);
            // Appended last on purpose: VoxrCommandRecogniserInjectionTests queues ORDERED log
            // expectations across the two scans above, so a new scan between them would break
            // the queue rather than merely add to it.
            WarnOnSiblingDiscriminator();
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
        // than 1.5/N) but could not close it, and no penalty tuning could: P scored a clean
        // 1.0 and nothing normalized to 1.0 can beat it. Marking the literal optional does —
        // an omitted optional leaves both sides of the ratio, so the longer pattern reaches
        // 1.0 whether or not the literal was spoken and takes the consumed-span tie-break
        // (issue #41) over the bare form.
        //
        // §5.2 CLOSED THE COMMON CASE, so the paragraph above is now the history of this
        // detector rather than its reason. Coverage charges P for what it leaves unexplained,
        // which is exactly the stranded slot value: bare "decelerate" on "decelerate hard
        // burn" falls from 1.0 to 1/(1+2) and loses to the longer pattern's 2/3.
        //
        // The hazard is REDUCED, not eliminated, and the residue is what this still warns
        // about. An orphan run stops at the first token that could begin some other pattern,
        // so when the stranded value's first word happens to start one, P is charged nothing
        // and strands the value exactly as before — register ["hard","stop"] beside the pair
        // above and "hard" becomes such a token.
        //
        // Deliberately NOT narrowed to that residue, though _startLiterals/_startSlots could
        // now answer it. The remedy is the better authoring either way: an optional literal
        // reaches 1.0 rather than 2/3, so it wins by more, and it also wins in the residual
        // case where coverage alone does not.
        //
        // The scan mirrors what ParseInternal actually compares, which is why it is this wide:
        //   - ACROSS COMMANDS, not just within one. Selection runs over every pattern of every
        //     command through a single CompareCandidate, so splitting the two phrasings across
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
        //
        // EDITOR-ONLY as of issue #81. §5.2 demoted this from a hazard every such grammar
        // carries to a residue most of them do not, while it went on firing on every parser
        // construction — in shipped builds, and again on every SetActiveSets rebuild (see the
        // "Validation warnings re-emit on every active-set switch" limitation, which this
        // compounded). A warning that loud on grammars that now behave correctly is the kind
        // that gets globally suppressed, taking the residual case with it. The TRIGGER stays at
        // full breadth, for three reasons. The remedy is the better authoring either way and
        // does reach the residue coverage alone does not — though it is not the only edit that
        // does: removing the literal outright reaches it too, scoring the same 1.0 and taking
        // the same span tie-break, at the cost of the phrasing. A narrowing here could consult
        // only _startLiterals/_startSlots, which answer CanStartPattern; the orphan run
        // actually terminates on the strictly wider IsAdmissibleStart (issue #82), so a
        // construction-time trigger would go silent on every position the cheap test declines.
        // And at coverageWeight 0 the hazard is not residual at all but reverts wholesale.
        // What changes is only that this is authoring guidance now, delivered where authoring
        // happens. The two passes beside it are untouched — they report outright authoring
        // mistakes, not a shape that is usually fine.
        //
        // Conditional rather than #if so the call site and the helpers below stay one piece of
        // code; the tests that LogAssert.Expect this message therefore pin it in editor Play
        // Mode, where this package's Runtime suite runs, and not in a built player.
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
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

        // Concrete forms of a pattern for the sibling scan. Patterns with no optional elements
        // are their own single form, with no copy. The expansion is capped well below
        // MaxOptionalExpansion because in the Editor this scan runs from the ctor — and so on
        // every parser rebuild a session makes — where ComputeCanCommitEarly's expansion is
        // lazy; past the cap the pattern is compared raw.
        //
        // "Costing recall on that one pattern only" is how this read while the only consumer
        // was a Debug.LogWarning. Since issue #74 item 2 the same forms feed a RUNTIME gate, so
        // a truncated pattern is one whose sibling relations are unknown rather than absent —
        // which is why EnsureSiblingLookup records the truncation per pattern and
        // AreSiblingRivals refuses on a cross-intent tie involving one, instead of reporting no
        // hazard from an analysis it never performed.
        const int MaxWarningExpansion = 6;

        // Whether this pattern's optionals were too many to expand, so WarningForms handed back
        // the raw decorated pattern instead of its readings. The single definition of that
        // fact: EnsureSiblingLookup consults it to decide whether a pattern's sibling relations
        // are KNOWN, and a second spelling of the same comparison could drift from this one
        // without anything failing.
        static bool ExpansionTruncated(string[] pattern) =>
            CountOptionalElements(pattern) > MaxWarningExpansion;

        static List<string[]> WarningForms(string[] pattern)
        {
            int optionals = CountOptionalElements(pattern);
            if (optionals == 0 || ExpansionTruncated(pattern))
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
                + "element's credit while the bare one still matches perfectly. The bare one is "
                + "now charged for the words it leaves unexplained (issue #65 §5.2), so it "
                + "usually loses that exchange — but not when the stranded value's first word "
                + "could itself begin some pattern, which stops the charge and hands the bare "
                + "form the win with the value the speaker did say discarded silently. Make the "
                + $"literal optional (\"?{firstRequired}\") to close the case that remains: an "
                + "otherwise-complete match then reaches the same score with or without the word "
                + "and wins on consumed span. That trade is not free: an optional literal also "
                + "lowers the score of matches that are already missing something, and stops "
                + "anchoring the slot behind it, which can then claim adjacent tokens.";
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

        // The other construction-time hazard two patterns can carry (issue #74): they are
        // identical but for one required literal, so when the recogniser drops that one word
        // the surviving evidence fits both EXACTLY equally — same start, same score, same
        // consumed span, same literal count. Selection exhausts every key it has and falls
        // through to its last, the order the patterns were registered in. The word that would
        // have decided is precisely the word that went missing, so no scorer can recover the
        // intent; the evidence is not weak but absent.
        //
        // This is a different shape from the one the scan above reports, and deliberately not
        // merged with it. That one needs a strictly longer pattern, an element-prefix relation
        // and a SLOT stranded behind an added literal; it detects a dropped argument. This one
        // detects a coin-flipped intent, and the #74 shape satisfies none of those three.
        //
        // Neither can occur in an authored element, so the bucket key below is unambiguous
        // about element boundaries, form length, and which position is wildcarded. A printable
        // separator would key ["switch to","weapons"] at position 1 and ["switch","to",
        // "weapons"] at position 2 to the same string, pairing forms of DIFFERENT lengths.
        const char SiblingKeySeparator = '\u0001';
        const char SiblingKeyWildcard = '\u0002';

        // Frame equality folds {?slot} onto {slot} and NOTHING ELSE, because that is the only
        // decoration a match is indifferent to: a matched slot credits MatchScore whether it
        // was optional or required (see the slot branch of TryMatchScored), so two forms that
        // differ only there really do tie.
        //
        // An optional LITERAL is deliberately NOT folded, even though it consumes the same
        // token, because it does not score the same: it credits OptionalLiteralScore to both
        // sides rather than MatchScore, and (r-0.5)/(d-0.5) < r/d for r < d — the arithmetic
        // the #42 warning already spells out above. So ["switch","?to","weapons"] against
        // ["switch","to","navigation"] scores 1.5/2.5 = 0.60 against 2/3 = 0.667 on the
        // transcript "switch to": the scores differ, selection separates them on its first
        // key, and there is no tie to fall through to registration order. Folding them would
        // report a hazard that cannot occur, which is exactly what the empty-frame and
        // same-intent gates below exist to prevent.
        static string NormalizeElement(string element)
        {
            string slot = ExtractSlotName(element);
            return slot != null ? "{" + slot + "}" : element;
        }

        // Discriminator eligibility, on the RAW element — here the "?" is load-bearing. An
        // optional discriminating word means the author already declared the pattern matches
        // with or without it, which makes the two forms duplicates rather than siblings.
        //
        // Testing element[0] rather than IsOptionalLiteral also declines a malformed "?{slot}"
        // and a bare "?". The matcher does NOT agree — it charges both as required literals —
        // so a grammar containing one really can tie, unreported. Declined anyway: such an
        // element can never match a token, GenerateGrammarJson ships it to the decoder verbatim,
        // and a message saying "if that word is dropped" about a word that cannot be spoken
        // would be nonsense. The tie is the least of that grammar's problems.
        //
        // The emptiness test is belt-and-braces: asset authoring splits with
        // RemoveEmptyEntries, so an empty element only reaches here from code.
        static bool IsRequiredLiteral(string element) =>
            !string.IsNullOrEmpty(element) && element[0] != '?' && ExtractSlotName(element) == null;

        // Does this element credit MatchedRequired when it matches? Optional elements of either
        // kind do not — TryMatchScored increments the counter only under `if (!isOptional)`,
        // and MatchedRequired is not score bookkeeping but the DR-7 admission key.
        static bool CreditsRequired(string element) =>
            !IsOptionalLiteral(element) && !IsOptionalSlot(element);

        // Whether anything OUTSIDE the discriminator would credit MatchedRequired for this
        // form. If nothing does, the candidate misses one required element (the discriminator)
        // and matches none, so `MissedRequired > MatchedRequired` refuses it in
        // CompareCandidate before any comparison key is reached — it can never be the rival
        // in a tie, whatever it scores.
        //
        // This is per FORM, not per set, because the two sides can differ here even when their
        // normalized frames agree: {?ship} and {ship} are the same frame element and score the
        // same, but only the required one credits MatchedRequired. Without this the scan warns
        // that "the wrong intent can fire" about a pair where one side is refused outright and
        // the other wins alone, or where both are refused and nothing fires at all.
        static bool HasRequiredElementOutside(string[] form, int discriminator)
        {
            for (int i = 0; i < form.Length; i++)
                if (i != discriminator && CreditsRequired(form[i]))
                    return true;
            return false;
        }

        // Maps a discriminator's index in an expanded FORM back to its index in the pattern the
        // author wrote (issue #91). Exact by counting, not by matching text — the obvious
        // implementation, a two-pointer walk comparing element strings, is ambiguous on a
        // pattern carrying two identical optionals (["a","?x","?x","b"]), which ExpandOptionals
        // reaches because it enumerates 2^optionals subsets without deduplicating.
        //
        // Two facts make counting exact:
        //   1. ExpandOptionals walks the authored pattern in order and only ever OMITS optional
        //      positions, so every surviving element keeps its string and its relative order.
        //   2. The only caller passes a `formIndex` where IsRequiredLiteral holds, and a
        //      required element is never one of the omitted ones.
        // So the form's non-optional elements ARE the pattern's non-optional elements, in the
        // same order: the discriminator is the (r+1)-th of them in the form, hence the (r+1)-th
        // in the pattern.
        //
        // When WarningForms hands back the raw pattern — no optionals, or past
        // MaxWarningExpansion — form and pattern are the same array and this returns formIndex,
        // as it must.
        static int AuthoredIndexOfFormElement(string[] pattern, string[] form, int formIndex)
        {
            int rank = 0;
            for (int i = 0; i < formIndex; i++)
                if (CreditsRequired(form[i]))
                    rank++;

            for (int i = 0; i < pattern.Length; i++)
            {
                if (!CreditsRequired(pattern[i]))
                    continue;
                if (rank == 0)
                    return i;
                rank--;
            }

            // Unreachable while (1) and (2) hold. Falling back to the form's own index keeps a
            // future violation to a mis-numbered warning rather than an out-of-range read.
            return formIndex;
        }

        // What a candidate scores once the discriminator is dropped. Every element keeps its
        // usual weight — a matched slot credits MatchScore whether or not it is optional, and
        // only an optional literal credits OptionalLiteralScore — so the frame's total weight
        // is the denominator, and losing the discriminator costs exactly MatchScore off the
        // numerator. Coverage is zero in the tying case: the transcript is what the frame
        // matched, with nothing left over on either side.
        static float ScoreAfterDroppingDiscriminator(string[] frame)
        {
            float denominator = MatchScore; // the discriminator's own weight, a required literal
            for (int i = 0; i < frame.Length; i++)
            {
                if (frame[i] == null)
                    continue;
                denominator += IsOptionalLiteral(frame[i]) ? OptionalLiteralScore : MatchScore;
            }
            return (denominator - MatchScore) / denominator;
        }

        // The sole definition of "sibling" (design DR-2): equal length, equal at every position
        // but one, a required literal in each at that position — at ANY position, not only the
        // last, since a medial discriminator is the more dangerous case and is unguarded on
        // both the eager and flush paths (design §2.8, confirmed by VoxrEagerCommitTests).
        //
        // What comes back is REPORTABLE sets, not every pair the relation admits. Three cases
        // are dropped because no consumer could act on them, not merely because the warning
        // does not want them: a pattern paired with its own other expansion, a set whose
        // members carry the same discriminating value, and a frame with nothing left in it. A
        // consumer that wants same-intent sets does get those — that filter lives in the
        // warning, not here.
        //
        // Buckets each (form, position) by the frame it leaves behind, so two forms share a
        // bucket exactly when they are siblings and an n-way set is collected in one pass
        // rather than assembled from pairs.
        //
        // Two passes with different costs, and the second is the one that grows: collecting is
        // O(forms x length^2), but collapsing calls IndexOfSameMembers per surviving bucket,
        // which rescans the sets found so far — O(surviving x sets x members), quadratic in the
        // number of distinct hazards. The constant is small enough that a hash index measured
        // no better, but a consumer promoting this to a hot path should not inherit the
        // assumption that it is linear in forms.
        internal static List<SiblingSet> FindSiblingSets(VoxrCommandDefinition[] commands)
        {
            var sets = new List<SiblingSet>();
            if (commands == null)
                return sets;

            var buckets = new Dictionary<string, SiblingBucket>(StringComparer.Ordinal);
            // Dictionary iteration order is unspecified, and warnings have to come out the same
            // way every run or the tests that assert on them go flaky.
            var order = new List<SiblingBucket>();

            var key = new StringBuilder();
            string[] normalized = null;

            for (int ci = 0; ci < commands.Length; ci++)
            {
                var patterns = commands[ci].Patterns;
                for (int pi = 0; pi < patterns.Length; pi++)
                {
                    var forms = WarningForms(patterns[pi]);
                    for (int fi = 0; fi < forms.Count; fi++)
                    {
                        string[] form = forms[fi];

                        // A one-element form leaves nothing behind once its only element is
                        // wildcarded, and a frame with no remainder can never tie (see the
                        // emission loop). Skipping it here means no such bucket is ever built.
                        if (form.Length < 2)
                            continue;

                        // Normalization depends only on the element, not on which position is
                        // being wildcarded, so it is hoisted out of the d loop and reused. The
                        // scratch array grows to the longest form and is then shared by all.
                        if (normalized == null || normalized.Length < form.Length)
                            normalized = new string[form.Length];
                        for (int i = 0; i < form.Length; i++)
                            normalized[i] = NormalizeElement(form[i]);

                        for (int d = 0; d < form.Length; d++)
                        {
                            if (!IsRequiredLiteral(form[d]))
                                continue;

                            // A form the admission rule would refuse is not a rival, so it is
                            // not collected at all. Dropping it here rather than at emission
                            // matters: a set can be left with one real member and one refused
                            // one, which is not a tie and must not be reported as one.
                            if (!HasRequiredElementOutside(form, d))
                                continue;

                            key.Length = 0;
                            for (int i = 0; i < form.Length; i++)
                            {
                                if (i > 0)
                                    key.Append(SiblingKeySeparator);
                                if (i == d)
                                    key.Append(SiblingKeyWildcard);
                                else
                                    key.Append(normalized[i]);
                            }

                            string k = key.ToString();
                            if (!buckets.TryGetValue(k, out var bucket))
                            {
                                // The form and the position are enough to rebuild the frame, and
                                // most buckets never reach two members — building the frame here
                                // would allocate a string[] per bucket that nothing ever reads.
                                bucket = new SiblingBucket(form, d);
                                buckets[k] = bucket;
                                order.Add(bucket);
                            }

                            bucket.Members.Add(
                                new SiblingMember(
                                    ci,
                                    pi,
                                    commands[ci].Intent,
                                    form[d],
                                    AuthoredIndexOfFormElement(patterns[pi], form, d)
                                )
                            );
                        }
                    }
                }
            }

            for (int o = 0; o < order.Count; o++)
            {
                var members = order[o].Members;
                if (members.Count < 2)
                    continue;

                // EVERY pattern carrying the hazard is kept, and the set is emitted when at
                // least two distinct discriminating values remain (issue #90).
                //
                // This used to keep one member per distinct value, which under-reported: with
                // a:["mode","on"], b:["mode","on"], c:["mode","off"] the emitted set was
                // {a,c} and b was never named, even though b<->c is exactly the hazard a<->c
                // is. An author fixing what the warning named left the other half live. Worse,
                // where the dropped member was the only one carrying a second intent, the
                // survivors shared one intent and the same-intent filter then suppressed the
                // set entirely — an under-report becoming no report at all, and invisible to
                // the runtime lookup that consumes these sets.
                //
                // What keeps author-duplicated patterns out (item 1's requirements F8) is no
                // this gate but the "distinct values" test below plus the per-PAIR test in
                // AreSiblingRivals: two members sharing a value are duplicates of each other,
                // not siblings, so they never form a rival pair.
                //
                // The exact-duplicate guard below is NOT the per-(command, pattern) arm that
                // was removed at PR #92's review, and it is here for a different reason. That
                // one guarded a pattern being its own sibling through a shift aligning a
                // required literal against a different one — a mechanism the code no longer
                // has, since frame comparison stopped folding optional literals. This one
                // absorbs a pattern reaching one bucket TWICE: ExpandOptionals enumerates
                // 2^optionals subsets without deduplicating, so ["a","?x","?x","b"] yields the
                // form ["a","?x","b"] from two different masks, and both land here with the
                // same (command, pattern, value). The old value gate hid that; keeping members
                // exposes it, and the warning would name one pattern twice.
                var kept = new List<SiblingMember>();
                for (int i = 0; i < members.Count; i++)
                {
                    bool duplicate = false;
                    for (int j = 0; j < kept.Count; j++)
                    {
                        if (
                            kept[j].CommandIndex == members[i].CommandIndex
                            && kept[j].PatternIndex == members[i].PatternIndex
                        )
                        {
                            duplicate = true;
                            break;
                        }
                    }
                    if (!duplicate)
                        kept.Add(members[i]);
                }

                if (DistinctValueCount(kept) < 2)
                    continue;

                // One hazard can surface under several frames: a pattern pair carrying an
                // optional element fills one bucket per expansion, with the same members each
                // time. Collapse those here rather than in each consumer — a sibling set is a
                // hazard, not a frame.
                //
                // The survivor is the one with the MOST elements, which is the reading closest
                // to what the author wrote. Keeping the first-seen instead would report the
                // discriminator's position within a form that silently dropped an optional,
                // so an author counting elements in their own pattern would land on the wrong
                // word.
                //
                // The comparison runs on `kept` and the form's length, so the frame and the
                // member array are built only for the set actually stored — on a grammar where
                // one hazard surfaces under many frames, all but one of those allocations
                // would otherwise be made and immediately dropped.
                int existing = IndexOfSameMembers(sets, kept);
                if (existing < 0)
                {
                    sets.Add(
                        new SiblingSet(
                            order[o].Discriminator,
                            order[o].BuildFrame(),
                            kept.ToArray()
                        )
                    );
                }
                else if (order[o].Form.Length > sets[existing].Frame.Length)
                {
                    sets[existing] = new SiblingSet(
                        order[o].Discriminator,
                        order[o].BuildFrame(),
                        kept.ToArray()
                    );
                }
            }

            return sets;
        }

        // One frame's worth of collection state, held by reference so the collecting loop can
        // append to it through the dictionary and the emission loop can walk it in first-seen
        // order. Keeps the form rather than the built frame: only a bucket that survives the
        // member gates needs one, and on a large grammar most do not.
        sealed class SiblingBucket
        {
            public readonly string[] Form;
            public readonly int Discriminator;
            public readonly List<SiblingMember> Members = new List<SiblingMember>();

            public SiblingBucket(string[] form, int discriminator)
            {
                Form = form;
                Discriminator = discriminator;
            }

            public string[] BuildFrame()
            {
                var frame = new string[Form.Length];
                for (int i = 0; i < Form.Length; i++)
                    frame[i] = i == Discriminator ? null : NormalizeElement(Form[i]);
                return frame;
            }
        }

        // A set is only a hazard if the members disagree about the discriminating word. Two
        // patterns reaching the same literal are duplicates of each other — an authoring error
        // this design leaves alone (item 1's requirements F8) — so a bucket whose members carry
        // value has nothing to tie over.
        static int DistinctValueCount(List<SiblingMember> members)
        {
            int distinct = 0;
            for (int i = 0; i < members.Count; i++)
            {
                bool seenEarlier = false;
                for (int j = 0; j < i; j++)
                {
                    if (string.Equals(members[j].Value, members[i].Value, StringComparison.Ordinal))
                    {
                        seenEarlier = true;
                        break;
                    }
                }
                if (!seenEarlier)
                    distinct++;
            }
            return distinct;
        }

        static int IndexOfSameMembers(List<SiblingSet> sets, List<SiblingMember> candidate)
        {
            for (int s = 0; s < sets.Count; s++)
            {
                var members = sets[s].Members;
                if (members.Length != candidate.Count)
                    continue;

                bool same = true;
                for (int m = 0; m < members.Length; m++)
                {
                    if (
                        members[m].CommandIndex != candidate[m].CommandIndex
                        || members[m].PatternIndex != candidate[m].PatternIndex
                        || !string.Equals(
                            members[m].Value,
                            candidate[m].Value,
                            StringComparison.Ordinal
                        )
                    )
                    {
                        same = false;
                        break;
                    }
                }
                if (same)
                    return s;
            }
            return -1;
        }

        // Conditional rather than #if for the same reason the scan above is: the call site and
        // the builders below stay one piece of code, and the tests that LogAssert.Expect these
        // messages pin them in editor Play Mode, where this package's Runtime suite runs, and
        // not in a built player.
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        // Takes no commands parameter, unlike the scans above: the sets come from
        // EnsureSiblingLookup, whose member indices are into _commands, and resolving those
        // indices against a different array passed by a future caller would silently mis-quote
        // patterns or throw inside the message builder. One array, one source of truth.
        void WarnOnSiblingDiscriminator()
        {
            // Consumes the shared lookup instead of calling FindSiblingSets again. DR-2 asks
            // for ONE sibling-set computation consumed by the warning and by selection, and two
            // calls per parser would be two — a cost no measurement here would have caught,
            // since the A/B rig defines no UNITY_EDITOR and this [Conditional] caller elides
            // there entirely. In a player that is exactly what happens: this call disappears
            // and the lookup is instead built on the first eager check.
            EnsureSiblingLookup();
            var sets = _siblingSets;

            for (int s = 0; s < sets.Count; s++)
            {
                var set = sets[s];

                // Only a CROSS-intent set is worth an author's attention. Within one intent the
                // wrong intent cannot fire — the same command is dispatched either way, and the
                // "tie" is between two phrasings the author deliberately made equivalent.
                //
                // Measured over the shipped demo grammar (design §7.3): every cross-intent set
                // was a genuine hazard — "stop firing"/"resume firing", "enable all"/"disable
                // all" name opposite actions — and every same-intent set was ordinary synonym
                // authoring with no remedy short of using fewer synonyms. Reporting those would
                // have made this scan noise on the package's own sample grammar, which is the
                // lesson issue #81 just paid for on the scan above. Ruled by the human at that
                // measurement, 2026-08-15; the counts are pinned by
                // SiblingSets_DemoGrammar_VolumeAndOrderAreStable.
                //
                // The filter is HERE and not in FindSiblingSets on purpose: the relation stays
                // exactly as DR-1 defines it, so a later consumer that does care about a
                // same-intent tie still sees one.
                // ...and only if the tie is REACHABLE. Losing one required element out of a
                // frame worth D leaves (D-1)/D, so a two-element pattern drops to 0.5 — under
                // the shipped minScore default, which rejects BOTH siblings. Nothing fires,
                // rather than the wrong thing, and this repo already pins that twice
                // (MissedLiteral_TwoElementPattern_StillRejected and its recogniser-level
                // counterpart _DoesNotFire). Warning "the wrong intent can fire" there is
                // false, and the remedy it offers is inert when no command fires at all.
                //
                // Judged against the DEFAULT, because the parser constructor is handed only
                // (slots, commands, coverageWeight, additionalGrammarWords) and cannot see the
                // recogniser's configured minScore. An author who lowers it below (D-1)/D
                // makes these ties live and gets no warning — a real limitation, stated in
                // KNOWN_LIMITATIONS rather than papered over. Erring the other way would put
                // a knowingly false claim in front of every author who did not touch the knob.
                if (
                    !IsSingleIntent(set)
                    && ScoreAfterDroppingDiscriminator(set.Frame) >= DefaultMinScore
                )
                    UnityEngine.Debug.LogWarning(BuildSiblingWarning(set));

                // The collision report is narrowed differently from the filter above, and the
                // difference is still deliberate. That filter rests on the wrong-INTENT harm at
                // the SET level; this report is about one discriminating value being unreachable
                // as an answer, which is a per-PAIR question — item 2 established that a set can
                // be cross-intent overall while a particular pair inside it shares an intent.
                //
                // Item 1 left "whether a same-intent tie is ever routed to the speaker" open for
                // the later items, and reported every collision rather than guess on their
                // behalf. Issue #74 item 3 decided it: same-intent ties are never routed, because
                // the same command is dispatched either way. So a value is reachable as an answer
                // only if some co-member carries BOTH a different value and a different intent —
                // IsAnswerableRival, shared with the runtime gate rather than copied. Reporting a
                // collision on a value the runtime would never ask about is a knowingly false
                // advisory, the class issue #81 spent a whole feature reversing.
                //
                // Tested against the EFFECTIVE cancel vocabulary, so an author who already
                // resolved the collision by overriding cancelVocabulary is not told about it
                // again.
                //
                // Reported once per colliding VALUE, not once per member. Since issue #90 a
                // value can be carried by several patterns, and the remedy is the same advice
                // however many patterns spell it, so repeating it would be noise. The dedup
                // counts only members that would themselves be reported: a value carried by an
                // unanswerable member first and an answerable one later is a real collision, and
                // suppressing it against the earlier member would lose it.
                for (int m = 0; m < set.Members.Length; m++)
                {
                    if (Array.IndexOf(_effectiveCancelVocabulary, set.Members[m].Value) < 0)
                        continue;

                    if (!HasAnswerableCoMember(set, m))
                        continue;

                    bool alreadyReported = false;
                    for (int e = 0; e < m; e++)
                    {
                        if (
                            string.Equals(
                                set.Members[e].Value,
                                set.Members[m].Value,
                                StringComparison.Ordinal
                            ) && HasAnswerableCoMember(set, e)
                        )
                        {
                            alreadyReported = true;
                            break;
                        }
                    }
                    if (alreadyReported)
                        continue;

                    UnityEngine.Debug.LogWarning(
                        BuildCancelCollisionWarning(set.Members[m], _cancelVocabularyIsOverridden)
                    );
                }
            }
        }

        // Callers normally pass at least two parts: a reported set clears the two-distinct-value
        // gate in FindSiblingSets, and the same-intent filter leaves at least two intents. The
        // pattern-text list is the one that can still collapse to one — see inside.
        static string JoinWith(List<string> parts, string conjunction)
        {
            // A single part needs no conjunction, and since issue #90 that is reachable: the
            // three lists this renders are deduplicated for display, and two members carrying
            // different discriminating values can still render identical pattern TEXT when an
            // element holds more than one word ("a b" "c" and "a" "b" "c" both join to "a b c").
            // Degenerate authoring, but it used to be structurally impossible and now is not.
            // Zero is NOT handled: every caller appends at least its first member.
            if (parts.Count == 1)
                return parts[0];

            var sb = new StringBuilder();
            for (int i = 0; i < parts.Count - 1; i++)
            {
                if (i > 0)
                    sb.Append(", ");
                sb.Append(parts[i]);
            }
            return sb.Append(' ')
                .Append(conjunction)
                .Append(' ')
                .Append(parts[parts.Count - 1])
                .ToString();
        }

        // internal so the warning-volume test measures the shipped filter instead of keeping
        // its own copy of the rule — the split it pins is the evidence DR-7's default-on
        // ruling rests on.
        internal static bool IsSingleIntent(SiblingSet set)
        {
            for (int i = 1; i < set.Members.Length; i++)
                if (
                    !string.Equals(
                        set.Members[i].Intent,
                        set.Members[0].Intent,
                        StringComparison.Ordinal
                    )
                )
                    return false;
            return true;
        }

        // Could the speaker ever be asked to choose B over A, and pick B out by saying its
        // word? Both halves are required:
        //
        //   different VALUE — otherwise there is nothing to say that distinguishes them. Two
        //   members reaching the same literal are duplicates of each other, not siblings (item
        //   1's requirements F8).
        //
        //   different INTENT — otherwise the answer changes nothing. The same command is
        //   dispatched either way, so asking costs the speaker a round trip to choose between
        //   identical outcomes. Item 1 left "whether a same-intent tie is ever routed to the
        //   speaker" open for the later items; issue #74 item 3 decided it: never routed.
        //
        // Deliberately a predicate over four strings rather than a method on SiblingMember. Its
        // two consumers hold the same facts in different structures — the Editor report walks
        // SiblingMembers, the runtime gate walks SiblingMemberships and _commands — and
        // _siblingSets is Editor-assigned only, so a shared rule expressed over SiblingMember
        // would be a null dereference the moment the runtime path reached it in a player.
        static bool IsAnswerableRival(
            string valueA,
            string intentA,
            string valueB,
            string intentB
        ) =>
            !string.Equals(valueA, valueB, StringComparison.Ordinal)
            && !string.Equals(intentA, intentB, StringComparison.Ordinal);

        // The set-local form of the question above: is this member's discriminating value ever
        // something the speaker could be asked to say? Per-PAIR, not per-set, because item 2
        // established that a set can be cross-intent overall while a particular pair inside it
        // shares an intent — one command contributing two patterns alongside a third from
        // another.
        static bool HasAnswerableCoMember(SiblingSet set, int m)
        {
            var members = set.Members;
            for (int o = 0; o < members.Length; o++)
                if (
                    o != m
                    && IsAnswerableRival(
                        members[m].Value,
                        members[m].Intent,
                        members[o].Value,
                        members[o].Intent
                    )
                )
                    return true;
            return false;
        }

        // DR-1 applied to the two patterns' REQUIRED elements — their all-optionals-omitted
        // readings — walked in place so nothing is materialised on the selection path.
        //
        // No normalization is needed and that is not an oversight: the only decoration frame
        // comparison folds is {?slot} onto {slot}, and an optional slot is not a required
        // element, so among the elements this walks there is nothing to fold.
        //
        // Used only when one side's expansion was truncated (see AreSiblingRivals). Equal
        // required-length, differing at exactly one position, a required literal in both there.
        static bool RequiredElementsAreSiblings(string[] a, string[] b)
        {
            int i = 0,
                j = 0,
                diffA = -1,
                diffB = -1,
                differences = 0;

            while (true)
            {
                while (i < a.Length && !CreditsRequired(a[i]))
                    i++;
                while (j < b.Length && !CreditsRequired(b[j]))
                    j++;
                if (i >= a.Length || j >= b.Length)
                    break;

                if (!string.Equals(a[i], b[j], StringComparison.Ordinal))
                {
                    if (++differences > 1)
                        return false;
                    diffA = i;
                    diffB = j;
                }
                i++;
                j++;
            }

            // Both sides must have run out together, or the required lengths differ and DR-1's
            // equal-length rule already excludes them.
            while (i < a.Length && !CreditsRequired(a[i]))
                i++;
            while (j < b.Length && !CreditsRequired(b[j]))
                j++;
            if (i != a.Length || j != b.Length)
                return false;

            // Zero differences means duplicates, not siblings — item 1's requirements F8
            // class, and the case a blind "true" got wrong.
            if (differences != 1)
                return false;

            return IsRequiredLiteral(a[diffA]) && IsRequiredLiteral(b[diffB]);
        }

        // One pattern's membership of one sibling set. The value rides along so the pair test
        // below never has to walk SiblingSet.Members looking for a matching (command, pattern).
        readonly struct SiblingMembership
        {
            public readonly int SetId;
            public readonly string Value;

            public SiblingMembership(int setId, string value)
            {
                SetId = setId;
                Value = value;
            }
        }

        // Builds the runtime lookup on first use and caches it, exactly as EnsureCanCommitEarly
        // does for the extendability precompute.
        void EnsureSiblingLookup()
        {
            if (_siblingLookupComputed)
                return;

            var sets = FindSiblingSets(_commands);
#if UNITY_EDITOR
            // Retained only for the construction-time warning, which is the sole reader and is
            // itself Editor-only. A player would otherwise hold every frame and member array
            // for the parser's lifetime with nothing to read them.
            _siblingSets = sets;
#endif

            var building = new List<SiblingMembership>[_commands.Length][];
            _siblingFormsTruncated = new bool[_commands.Length][];
            for (int ci = 0; ci < _commands.Length; ci++)
            {
                var patterns = _commands[ci].Patterns;
                building[ci] = new List<SiblingMembership>[patterns.Length];
                _siblingFormsTruncated[ci] = new bool[patterns.Length];

                // Past MaxWarningExpansion, WarningForms hands back the raw decorated pattern
                // instead of its expansions, so this pattern was only ever compared in its
                // all-optionals-present reading and its sibling relations are UNKNOWN rather
                // than absent. Recorded per pattern so AreSiblingRivals can refuse to claim
                // otherwise — see the conservative arm there.
                for (int pi = 0; pi < patterns.Length; pi++)
                    _siblingFormsTruncated[ci][pi] = ExpansionTruncated(patterns[pi]);
            }

            for (int s = 0; s < sets.Count; s++)
            {
                // A set whose members all share an intent contains no cross-intent pair, and
                // AreSiblingRivals requires one — so it can be skipped wholesale. This is a
                // cheap pre-filter, NOT the rule: a set that survives it can still hold
                // same-intent pairs, which is why the intent test below is per-pair.
                if (IsSingleIntent(sets[s]))
                    continue;

                var members = sets[s].Members;
                for (int m = 0; m < members.Length; m++)
                {
                    int ci = members[m].CommandIndex;
                    int pi = members[m].PatternIndex;
                    if ((uint)ci >= (uint)building.Length || (uint)pi >= (uint)building[ci].Length)
                        continue;

                    if (building[ci][pi] == null)
                        building[ci][pi] = new List<SiblingMembership>();
                    building[ci][pi].Add(new SiblingMembership(s, members[m].Value));
                }
            }

            _siblingMemberships = new SiblingMembership[_commands.Length][][];
            for (int ci = 0; ci < _commands.Length; ci++)
            {
                _siblingMemberships[ci] = new SiblingMembership[building[ci].Length][];
                for (int pi = 0; pi < building[ci].Length; pi++)
                    _siblingMemberships[ci][pi] = building[ci][pi]?.ToArray();
            }

            // Flag last, matching EnsureCanCommitEarly: a throw mid-build then retries rather
            // than latching a half-built lookup.
            _siblingLookupComputed = true;
        }

        // Whether two candidates that tied at selection are siblings whose discriminator went
        // missing — the coin flip issue #74 is about. All three tests are load-bearing:
        //
        //   share a set   the sibling relation itself, so non-sibling ties (authoring errors,
        //                 not speech ambiguity) stay out of the runtime path (design §5.3)
        //   differ in value   two members carrying the same literal are duplicates of each
        //                 other, not siblings — item 1's requirements F8 leaves those alone
        //   differ in intent  the same command dispatches either way otherwise, so refusing
        //                 would buy latency for nothing
        //
        // The intent test is per PAIR, not per set. Since issue #90 retains duplicate-valued
        // members, a set can be cross-intent overall while a particular tied pair inside it
        // shares an intent — one command contributing two patterns alongside a third from
        // another. A set-level test alone would refuse on that pair.
        //
        // Allocation-free: both operands are prebuilt arrays, and the common case exits on the
        // first null. Only ever called on a Tied comparison, which is rare.
        //
        // The eager path's shape, kept as a wrapper so its CONDITION is untouched (requirements
        // F17 forbids changing the eager condition; it does not require the helper beneath it
        // to stay byte-identical). TryEagerCommit only ever asks WHETHER a sibling tie exists,
        // to return None — it never needs the words.
        bool AreSiblingRivals(int ci1, int pi1, int ci2, int pi2) =>
            TryFindSiblingRival(ci1, pi1, ci2, pi2, out _, out _, out _);

        // The same question, plus the two strings the choice vocabulary is made of: the winner's
        // value in the shared set and the rival's. AreSiblingRivals computed both and threw them
        // away, and without them DR-4 cannot be built — the discriminating values ARE the
        // choices.
        //
        // Under expansion truncation it still answers the BOOL — the eager gate depends on that
        // refusal — but leaves setId at -1, so a caller that needs to NAME the rival can tell
        // the two apart. When either pattern was past MaxWarningExpansion the
        // fallback is RequiredElementsAreSiblings, which answers a bool about all-optionals-
        // omitted readings and knows no set, so it can confirm a tie but cannot name the words.
        // That over-approximation is right for REFUSING on the eager path — the direction
        // ComputeCanCommitEarly already fails in — but a refusal needs no vocabulary and a
        // question does. So a truncated tie records no rival and the flush fires the winner,
        // exactly as it would with the flag off. Safe direction, and stated rather than found.
        bool TryFindSiblingRival(
            int ci1,
            int pi1,
            int ci2,
            int pi2,
            out int setId,
            out string winnerValue,
            out string rivalValue
        )
        {
            setId = -1;
            winnerValue = null;
            rivalValue = null;

            if (_siblingMemberships == null)
                return false;
            if (
                (uint)ci1 >= (uint)_siblingMemberships.Length
                || (uint)ci2 >= (uint)_siblingMemberships.Length
            )
                return false;
            if (
                (uint)pi1 >= (uint)_siblingMemberships[ci1].Length
                || (uint)pi2 >= (uint)_siblingMemberships[ci2].Length
            )
                return false;

            // Same command is necessarily the same intent; two distinct commands may still
            // share one, so compare the strings rather than the indices alone.
            if (
                ci1 == ci2
                || string.Equals(
                    _commands[ci1].Intent,
                    _commands[ci2].Intent,
                    StringComparison.Ordinal
                )
            )
                return false;

            // Truncated-analysis arm. Past MaxWarningExpansion a pattern was never expanded
            // over its optionals, so the frames it would have shared with a rival do not exist
            // and the lookup above cannot tell "has no sibling" from "was never analysed".
            // Without something here the two caps disagreed: ComputeCanCommitEarly abandons only
            // past MaxOptionalExpansion, so a pattern with 7-12 optionals kept a live eager
            // commit while its sibling relations silently went unanalysed, and the gate
            // committed on precisely the coin flip this feature exists to refuse.
            //
            // It does NOT answer "true" blindly. An earlier version did, and that refused on
            // pairs provably not siblings — two patterns whose discriminator is the SAME word
            // are duplicates, which requirements F10 keeps out of the runtime path entirely.
            // Instead, fall back to the one reading that survives truncation: DR-1 requires the
            // discriminator to be a required literal, and required elements appear in EVERY
            // expansion, so comparing the two patterns' required elements is exactly asking
            // whether their all-optionals-omitted readings are siblings. That reading is real —
            // matching handles optionals natively and never consults WarningForms.
            //
            // Partial, and deliberately so: a sibling relation existing only in a MID expansion
            // is still missed. This narrows the over-approximation to something provable rather
            // than closing the hole completely.
            //
            // This arm answers the BOOL and leaves setId at -1, and the asymmetry is the point.
            // RequiredElementsAreSiblings knows no set, so it cannot name the winner's value or
            // the rival's — and a question needs those words while a refusal does not. So the
            // eager gate keeps refusing here exactly as item 2 built it (F17, and the two tests
            // TryEagerCommit_SiblingAnalysisTruncatedByTheExpansionCap_RefusesAnyway and
            // _TruncatedPatternAsTheRival_RefusesToo pin it), while the flush's recorder — which
            // requires a set id — records nothing and fires the winner, as it would with the
            // flag off. Safe direction on both paths, from one call.
            if (_siblingFormsTruncated[ci1][pi1] || _siblingFormsTruncated[ci2][pi2])
                return RequiredElementsAreSiblings(
                    _commands[ci1].Patterns[pi1],
                    _commands[ci2].Patterns[pi2]
                );

            var a = _siblingMemberships[ci1][pi1];
            var b = _siblingMemberships[ci2][pi2];
            if (a == null || b == null)
                return false;

            // Same rule as the Editor collision report's narrowing, one definition (F13). The
            // intent half is already decided above — this pair reached here only by carrying
            // two different intents — so IsAnswerableRival reduces to the value test here, and
            // is called anyway so the two consumers cannot drift apart.
            string intentA = _commands[ci1].Intent;
            string intentB = _commands[ci2].Intent;
            for (int i = 0; i < a.Length; i++)
            for (int j = 0; j < b.Length; j++)
                if (
                    a[i].SetId == b[j].SetId
                    && IsAnswerableRival(a[i].Value, intentA, b[j].Value, intentB)
                )
                {
                    setId = a[i].SetId;
                    winnerValue = a[i].Value;
                    rivalValue = b[j].Value;
                    return true;
                }

            return false;
        }

        // Instance, and reading _commands directly, for the reason WarnOnSiblingDiscriminator
        // above gives for dropping its own parameter: the member indices in `set` are indices
        // into _commands, and this is the message builder that dereferences them. A caller
        // handing in a different array would mis-quote patterns or throw here.
        string BuildSiblingWarning(SiblingSet set)
        {
            var members = set.Members;

            // Report the patterns as authored, noting when a form omitted optionals, so an
            // author can find the text in their asset — the frame this set was keyed on is
            // normalized, and normalized text is not what they wrote.
            // Deduplicated for the same reason the intent and value lists below are, and it
            // became necessary for the same reason: since issue #90 two members can be distinct
            // patterns of distinct intents carrying identical authored text. Printing it twice
            // would have the message assert that patterns "differ only at element N" while two
            // of the strings it just listed differ at no element at all.
            //
            // Whether one element number can speak for the whole set (issue #91). The frame's
            // DiscriminatorIndex is an index into ONE expansion's shape, so a member whose form
            // omitted an optional carries its value at a later authored position — and it is
            // the AUTHORED pattern this message quotes. When the members agree, one number is
            // still right for all of them and the message keeps its shorter, established
            // wording; when they disagree, no single number can be right and each quoted
            // pattern carries its own.
            bool sharedIndex = true;
            for (int i = 1; i < members.Length; i++)
                if (members[i].AuthoredDiscriminatorIndex != members[0].AuthoredDiscriminatorIndex)
                {
                    sharedIndex = false;
                    break;
                }

            var patternTexts = new List<string>(members.Length);
            for (int i = 0; i < members.Length; i++)
            {
                string[] raw = _commands[members[i].CommandIndex].Patterns[members[i].PatternIndex];
                string text = "\"" + string.Join(" ", raw) + "\"";
                if (raw.Length != set.Frame.Length)
                    text += " (with its optional elements omitted)";
                // Authors count elements from one.
                if (!sharedIndex)
                    text += $" at element {members[i].AuthoredDiscriminatorIndex + 1}";
                if (!patternTexts.Contains(text))
                    patternTexts.Add(text);
            }

            // One intent can contribute several patterns to a set — "cease fire" and "hold
            // fire" both tie with "resume fire" — so name each intent once rather than once
            // per pattern.
            var intents = new List<string>(members.Length);
            for (int i = 0; i < members.Length; i++)
            {
                string quoted = "'" + members[i].Intent + "'";
                if (!intents.Contains(quoted))
                    intents.Add(quoted);
            }

            // Values are deduplicated for the same reason, and it became necessary when the
            // emission gate stopped dropping duplicate-valued members (issue #90): two patterns
            // reaching the same literal are both named as patterns, but the CHOICE the speaker
            // faces is between distinct words, so rendering ("on", "on" or "off") would be
            // wrong about the question rather than merely repetitive.
            var values = new List<string>(members.Length);
            for (int i = 0; i < members.Length; i++)
            {
                string quoted = "\"" + members[i].Value + "\"";
                if (!values.Contains(quoted))
                    values.Add(quoted);
            }

            string differ = sharedIndex
                ? $"that differ only at element {members[0].AuthoredDiscriminatorIndex + 1}"
                : "that differ only at that element";

            // The cap, reported at construction where the set sizes are already known and the
            // author can still act — and NOT merely flagged at parse time, which is the silent
            // cap wearing a boolean (requirements F19 forbids one). Runtime disambiguation
            // offers the winner plus MaxDisambiguationRivals alternatives, so a set spanning
            // more distinct values than that cannot put them all in one question.
            //
            // Emitted only from this message, so it inherits the same-intent and reachability
            // gates: a same-intent set is never routed to the speaker at all, and telling an
            // author their synonym list is too long to ask about would be advice about a
            // question that is never asked.
            string capNote =
                values.Count > 1 + MaxDisambiguationRivals
                    ? $" This set spans {values.Count} discriminating values and runtime "
                        + $"disambiguation offers at most {1 + MaxDisambiguationRivals} choices, "
                        + "so the rest cannot be answered in one word — the speaker would have "
                        + "to say the whole command again."
                    : string.Empty;

            return $"[VoxrCommandParser] Intents {JoinWith(intents, "and")} have "
                + $"patterns {JoinWith(patternTexts, "and")} {differ} "
                + $"({JoinWith(values, "or")}). If that word is "
                + "dropped, these patterns match the remainder equally — same score, same "
                + "consumed span, same literal count — and selection falls through to "
                + "registration order, so the wrong intent can fire. Make them differ in more "
                + "than one element — the only fix that removes the tie rather than moving it "
                + "— mark the more destructive one requiresConfirmation, or enable "
                + "disambiguateSiblingTies on VoxrCommandRecogniser to ask the speaker which "
                + $"was meant.{capNote}";
        }

        // Takes the MEMBER's authored index, not the set's DiscriminatorIndex: this message
        // names one member and quotes its value, so the number has to index that member's own
        // pattern (issue #91, the same defect as above and one message over).
        static string BuildCancelCollisionWarning(SiblingMember member, bool vocabularyOverridden)
        {
            // Stated as a future consequence rather than current behaviour: this version asks
            // the speaker to choose between siblings only when disambiguateSiblingTies is on,
            // and it is off by default, so on most grammars there is no answer to swallow yet.
            // Reported now because the remedy is to rename a grammar literal, and that is
            // cheaper to do while the grammar is being written than after.
            //
            // Naming the right vocabulary and the remedy that still applies matters, not just
            // reads better: once the report is computed against a configured override, "the
            // DEFAULT cancel vocabulary" is false and "override cancelVocabulary" is advice the
            // author has already taken.
            string source = vocabularyOverridden
                ? "also in the cancel vocabulary configured on VoxrCommandRecogniser"
                : "also in the default cancel vocabulary";
            string remedy = vocabularyOverridden
                ? "Rename the literal, or drop that word from cancelVocabulary."
                : "Rename the literal, or override cancelVocabulary on VoxrCommandRecogniser.";

            return $"[VoxrCommandParser] Intent '{member.Intent}' carries the discriminating "
                + $"value \"{member.Value}\" at element {member.AuthoredDiscriminatorIndex + 1}, "
                + $"which is {source}. Follow-up handling checks cancel "
                + "before anything else, so if this ambiguity is ever routed to the speaker to "
                + "resolve, answering with that word would cancel rather than choose that "
                + $"option. {remedy}";
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

            BuildCoverageTables(tokens);

            int searchStart = 0;
#if UNITY_EDITOR
            var diagnosticEntries = new List<ParseDiagnosticEntry>();
#endif

            while (searchStart < tokens.Length)
            {
                // Buffer full — stop BEFORE the scan, not after it. The scan indexes the rival
                // buffers by _resultCount (see the Tied branch below), and those are sized
                // _resultBuf.Length * MaxDisambiguationRivals, so letting one more round run
                // with a full result buffer writes exactly one slab past their end. Until issue
                // #74 item 3 the round's tie state was two plain locals and the extra round was
                // harmless, which is why this test used to sit at the bottom of the body.
                //
                // Nothing is lost by moving it: the extra round's only effects were its own
                // locals, and it broke without appending either way.
                if (_resultCount >= _resultBuf.Length)
                    break;

                float bestScore = float.MinValue;
                int bestLiteralCount = -1;
                int bestCommandIdx = -1;
                int bestPatternIdx = -1;
                int bestStartIdx = int.MaxValue;
                int bestEndIdx = 0;
                int bestConsumedEndIdx = 0;
                int bestSlotCount = 0;

                // Declared HERE, with the round's other best* locals, and not outside the loop:
                // selection restarts per extraction round, so a rival recorded in round 1 must
                // not survive into round 2. Hoisting these out compiles and passes any
                // single-command test — item 2's review caught exactly that, and left the reset
                // itself unpinned (its GAP-3). This feature moves them, so it pins it.
                //
                // No longer #if UNITY_EDITOR: since issue #74 item 3 the RUNTIME reads this to
                // decide whether to ask the speaker which intent was meant.
                int tiedRivalCount = 0;
                int tiedSetId = -1;
                string tiedWinnerValue = null;
                bool tiedTruncated = false;

                // The Editor diagnostic's exemplar, tracked separately from the offerable
                // rivals above, and the reason is the truncated-analysis arm. There, a pair
                // provably ties as siblings but cannot be NAMED — TryFindSiblingRival answers
                // the bool and leaves setId at -1 — so it is correctly refused as a choice and
                // would, if the diagnostic read the choice list, silently stop being reported.
                //
                // That is a real loss: the diagnostic exists to answer "was the winner decided
                // by a coin flip, and against whom?", and a truncated tie IS a coin flip. Two
                // ints keep it answering exactly what it answered before issue #74 item 3.
                int diagRivalCommandIdx = -1;
                int diagRivalPatternIdx = -1;

                // A rival that ties provably but cannot be NAMED (its expansion was truncated,
                // so the pair test answers the bool with no set id). Tracked separately from
                // tiedTruncated because whether it counts as truncation depends on something not
                // yet known when it is seen: if the round ends up asking no question at all, the
                // speaker re-utters anyway and nothing was lost — but if some OTHER rival makes
                // a question happen, this one is an answer the speaker could have given and will
                // not be offered. Resolved where the record is written, so the arrival order of
                // the two rivals does not change the answer.
                bool sawUnnameableRival = false;

                for (int ci = 0; ci < _commands.Length; ci++)
                {
                    var patterns = _commands[ci].Patterns;
                    for (int pi = 0; pi < patterns.Length; pi++)
                    {
                        for (int startIdx = searchStart; startIdx < tokens.Length; startIdx++)
                        {
                            if (tokens[startIdx] == UnkToken)
                                continue;

                            var matchResult = TryMatchScored(
                                tokens,
                                startIdx,
                                patterns[pi],
                                searchStart
                            );

                            var order = CompareCandidate(
                                matchResult,
                                startIdx,
                                bestScore,
                                bestStartIdx,
                                bestConsumedEndIdx,
                                bestLiteralCount
                            );

                            if (order == CandidateOrder.Better)
                            {
                                bestScore = matchResult.Score;
                                bestLiteralCount = matchResult.LiteralCount;
                                bestCommandIdx = ci;
                                bestPatternIdx = pi;
                                bestStartIdx = startIdx;
                                bestEndIdx = matchResult.EndIdx;
                                bestConsumedEndIdx = matchResult.ConsumedEndIdx;
                                bestSlotCount = matchResult.SlotCount;

                                // Clear-on-adopt: a new incumbent has its own rivals, and the
                                // old one's are about a candidate that no longer wins. This rule
                                // is shared with the eager path's copy and must stay so.
                                tiedRivalCount = 0;
                                tiedSetId = -1;
                                tiedWinnerValue = null;
                                tiedTruncated = false;
                                diagRivalCommandIdx = -1;
                                diagRivalPatternIdx = -1;
                                sawUnnameableRival = false;

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
                            // Every tied sibling rival, up to the cap — not just the first. Item
                            // 2's first-rival rule was right for an Editor diagnostic naming *a*
                            // rival, and is undersized for a CHOICE vocabulary, which needs every
                            // answer the speaker might give (design §5.1: a sibling set
                            // "generalises to n-ary sets"; the discriminating values ARE the
                            // choices). On a three-way set with the discriminator elided all
                            // three candidates tie, and item 2 saw rivals two and three and
                            // discarded them.
                            //
                            // The eager path still carries its own copy of these lines rather
                            // than sharing a helper, for the reason item 2 gave — each loop owns
                            // its own incumbent and tie state, so a shared routine would take
                            // them all by ref and read worse. Item 2's comment said the two
                            // copies "must stay in step"; that is now HALF true, and saying so
                            // beats leaving it to mislead. The clear-on-adopt rule stays shared.
                            // The recording DEPTH deliberately diverges, because only the flush
                            // has a consumer that cares how many: TryEagerCommit asks only
                            // WHETHER a sibling tie exists, to return None.
                            //
                            // The leading _recordSiblingTies test is what a flag-off player pays
                            // for this feature, and it is per CANDIDATE, not per tie — this is
                            // the innermost body of the ci x pi x startIdx loop. One predictable
                            // test against a readonly field, hundreds of times per parse on the
                            // demo grammar. Measured rather than argued (requirements F18(d)).
                            else if (
                                _recordSiblingTies
                                && order == CandidateOrder.Tied
                                && TryFindSiblingRival(
                                    bestCommandIdx,
                                    bestPatternIdx,
                                    ci,
                                    pi,
                                    out int rivalSetId,
                                    out string winnerValue,
                                    out string rivalValue
                                )
                            )
                            {
                                // The Editor diagnostic names the FIRST sibling rival, whether or
                                // not it can be offered as a choice — a tie we cannot phrase a
                                // question about is still a coin flip, and that is what this
                                // field reports. Set before every refusal below.
                                if (diagRivalCommandIdx < 0)
                                {
                                    diagRivalCommandIdx = ci;
                                    diagRivalPatternIdx = pi;
                                }

                                // A truncated analysis answers the bool but names no set, so it
                                // can refuse on the eager path and cannot ask here.
                                bool nameable = rivalSetId >= 0;
                                if (!nameable)
                                    sawUnnameableRival = true;

                                // ONE question at a time. A pattern can belong to several sets,
                                // and AreSiblingRivals answers true on ANY shared set, so
                                // without this a winner sitting in two sets would mix a rival
                                // that differs at position 2 with one that differs at position 1
                                // into a single choice list — two questions, two winner values,
                                // one prompt. Which set wins is registration order,
                                // deterministically.
                                bool sameQuestion = tiedRivalCount == 0 || rivalSetId == tiedSetId;

                                // The second live ambiguity IS an answer the speaker could have
                                // given and will not be offered, so it is reported exactly as the
                                // cap is (F19 — never silently truncated). This is what the
                                // comment here used to promise and the code did not do.
                                if (nameable && !sameQuestion)
                                    tiedTruncated = true;

                                if (nameable && sameQuestion)
                                    RecordTiedRival(
                                        ci,
                                        pi,
                                        rivalSetId,
                                        winnerValue,
                                        rivalValue,
                                        matchResult,
                                        ref tiedRivalCount,
                                        ref tiedSetId,
                                        ref tiedWinnerValue,
                                        ref tiedTruncated
                                    );
                            }
                        }
                    }
                }

                if (bestCommandIdx < 0 || bestScore <= 0f)
                    break;

                // Safety: prevent infinite loop if a match consumes no tokens
                if (bestEndIdx <= searchStart)
                    break;

                // No post-selection adjustment: the score a candidate was SELECTED on is the
                // score it is reported with.
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

                // Written BEFORE _resultCount advances, so index i of _tiedSiblingBuf describes
                // index i of _resultBuf — the alignment the recogniser relies on when it walks
                // the two in lockstep. Written unconditionally, including RivalCount = 0, so a
                // round that found no tie clears whatever the previous round left here.
                _tiedSiblingBuf[_resultCount] = new TiedSiblingRecord
                {
                    RivalCount = tiedRivalCount,
                    SetId = tiedSetId,
                    WinnerValue = tiedWinnerValue,
                    StartIdx = bestStartIdx,

                    // An unnameable rival only counts as truncation once a question is actually
                    // being asked — otherwise the flush fires the winner and re-uttering is what
                    // the speaker does regardless, which is what the flag would have told them.
                    Truncated = tiedTruncated || (sawUnnameableRival && tiedRivalCount > 0),
                };

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
                    // The FIRST sibling rival of the round, offerable or not. Deliberately
                    // NOT read off the choice list: a truncated analysis ties provably but names
                    // no set, so it is refused as a choice — and the question this field answers,
                    // "was the winner decided by a coin flip, and against whom?", is still yes
                    // there. Reading the choice list made this go silently null on exactly that
                    // shape, which is what the review caught.
                    TiedSiblingIntent =
                        diagRivalCommandIdx >= 0 ? _commands[diagRivalCommandIdx].Intent : null,
                    TiedSiblingPatternIndex = diagRivalPatternIdx,
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

        // Appends one offerable sibling rival to the round's record, or accounts for why it was
        // not appended. Extracted from the selection loop rather than inlined there because the
        // loop body already carries three nested conditions and the tie state is five locals;
        // taken by ref so nothing is copied back and forth.
        //
        // matchResult is the RIVAL's match — its slots are in _matchSlotBuf right now, which is
        // why the capture has to happen here and not later.
        void RecordTiedRival(
            int ci,
            int pi,
            int rivalSetId,
            string winnerValue,
            string rivalValue,
            MatchResult matchResult,
            ref int tiedRivalCount,
            ref int tiedSetId,
            ref string tiedWinnerValue,
            ref bool tiedTruncated
        )
        {
            // The pair test only guarantees each rival differs from the WINNER — it says nothing
            // about the rivals differing from EACH OTHER, so the same rule has to be applied
            // again here. IsAnswerableRival is that rule, and using it rather than a second
            // hand-written comparison is what F13's "exactly one code site expresses reachable
            // as an answer" actually asks for.
            //
            // Both halves matter, and a review found the intent half missing:
            //
            //   same VALUE   two spellings of one choice; answering the word could not pick
            //                between them (item 1's F8, one level up).
            //   same INTENT  two DIFFERENT words that dispatch the identical command. The
            //                speaker is asked to choose between indistinguishable outcomes, and
            //                the duplicate burns one of the MaxDisambiguationRivals slots — on a
            //                grammar where one intent contributes four sibling patterns, it
            //                takes all of them and squeezes out the only real alternative.
            //
            // Deduped HERE rather than where the choice arrays are built, so the cap counts
            // offerable choices and Truncated means a real one did not fit. A bounded scan over
            // at most MaxDisambiguationRivals entries, and only on a tie that already walked two
            // membership arrays to get here.
            string rivalIntent = _commands[ci].Intent;
            int firstRival = _resultCount * MaxDisambiguationRivals;
            for (int r = 0; r < tiedRivalCount; r++)
            {
                var kept = _tiedRivalBuf[firstRival + r];
                if (
                    !IsAnswerableRival(
                        kept.Value,
                        _commands[kept.CommandIndex].Intent,
                        rivalValue,
                        rivalIntent
                    )
                )
                    return; // Indistinguishable from a choice already offered. Nothing lost.
            }

            if (tiedRivalCount >= MaxDisambiguationRivals)
            {
                // A choice the speaker could have given, that this buffer cannot hold. Flagged
                // rather than dropped in silence: the integrator can word "…or say the whole
                // command again", and the author was told at construction where they can act.
                tiedTruncated = true;
                return;
            }

            if (tiedRivalCount == 0)
            {
                tiedSetId = rivalSetId;
                tiedWinnerValue = winnerValue;
            }

            int slot = firstRival + tiedRivalCount;
            _tiedRivalBuf[slot] = new TiedSiblingRival
            {
                CommandIndex = ci,
                PatternIndex = pi,
                Value = rivalValue,
                SlotCount = matchResult.SlotCount,
                EndIdx = matchResult.EndIdx,
            };

            // This rival's OWN slots, captured now rather than derived from the winner's later
            // (requirements F6). Siblings are element-wise equal but for one required literal, so
            // the slots agree in every case anyone can construct — but the tie is between
            // AUTHORED patterns that may reach the sibling relation through different optional
            // expansions, and "they agree in practice" is not a reason to fire a command with
            // another candidate's arguments.
            if (matchResult.SlotCount > 0)
                Array.Copy(
                    _matchSlotBuf,
                    0,
                    _rivalSlotBuf,
                    slot * _maxSlotsPerPattern,
                    matchResult.SlotCount
                );

            tiedRivalCount++;
        }

        internal VoxrCommandResult[] ResultBuffer => _resultBuf;

        // Aligned with ResultBuffer: index i describes the result at index i, valid for the
        // count ParseInternal returned. Exposed the same way, and read the same way — the
        // recogniser already walks the result buffer by index in its Step 7 loop.
        internal TiedSiblingRecord[] TiedSiblingBuffer => _tiedSiblingBuf;

        // Rival n of result i. Flat indexing rather than a jagged array so nothing allocates per
        // round; the caller has already checked n against TiedSiblingBuffer[i].RivalCount.
        internal TiedSiblingRival TiedRival(int resultIdx, int n) =>
            _tiedRivalBuf[resultIdx * MaxDisambiguationRivals + n];

        // That rival's own slot matches, copied out fresh because they cross into a public
        // VoxrCommand the subscriber can retain — the PendingCommandHandler precedent that
        // anything reaching a public event is allocated, never pool-borrowed. Once per
        // ambiguity, never per candidate.
        internal VoxrSlotMatch[] CopyRivalSlots(int resultIdx, int n)
        {
            int slot = resultIdx * MaxDisambiguationRivals + n;
            int count = _tiedRivalBuf[slot].SlotCount;
            if (count <= 0)
                return Array.Empty<VoxrSlotMatch>();

            var slots = new VoxrSlotMatch[count];
            Array.Copy(_rivalSlotBuf, slot * _maxSlotsPerPattern, slots, 0, count);
            return slots;
        }

        // Rival n's intent, so the recogniser can resolve its definition before offering it as
        // a choice — and drop it from the list if that lookup fails.
        internal string RivalIntent(int resultIdx, int n) =>
            _commands[_tiedRivalBuf[resultIdx * MaxDisambiguationRivals + n].CommandIndex].Intent;

        // The command that fires if the speaker picks rival n — built here rather than in the
        // recogniser because _slotNames and the confidence span both live on this side.
        //
        // Confidence is computed over the RIVAL's own span. ComputeConfidence takes
        // (tokens, startIdx, endIdx): startIdx comes from the record, because CompareCandidate
        // returns Tied only when the start indices match, and endIdx is per rival, because the
        // tie compares ConsumedEndIdx rather than EndIdx — so two tied candidates can differ by
        // a trailing [unk] neither of them consumed.
        //
        // The score is the winner's, and that is not an approximation: reaching Tied means the
        // scores were equal.
        internal VoxrCommand BuildRivalCommand(
            int resultIdx,
            int n,
            string[] tokens,
            Dictionary<string, float> wordConfidence
        )
        {
            var rival = _tiedRivalBuf[resultIdx * MaxDisambiguationRivals + n];
            return new VoxrCommand(
                _commands[rival.CommandIndex].Intent,
                CopyRivalSlots(resultIdx, n),
                ComputeConfidence(
                    tokens,
                    _tiedSiblingBuf[resultIdx].StartIdx,
                    rival.EndIdx,
                    wordConfidence
                ),
                // Both from the winner, both legitimately: reaching Tied means the scores were
                // equal, and every tied candidate was matched against the same utterance.
                _resultBuf[resultIdx].Command.Score,
                _resultBuf[resultIdx].Command.RawText,
                _slotNames,
                rival.PatternIndex
            );
        }

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

        // internal rather than private so CompareCandidate below can be internal: C# forbids a
        // member more accessible than its parameter types. Nothing outside this file constructs
        // one except the comparator's own tests.
        internal struct MatchResult
        {
            public float Score;
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

        // How a candidate ranks against the current incumbent. Three states rather than the
        // bool this used to return: "not better" conflated a clear loss with an exact tie, and
        // an exact tie is not a ranking at all — it is the point where every key is exhausted
        // and registration order decides (issue #74, design DR-3).
        //
        // Better is returned in exactly the cases the old bool returned true, so selection is
        // bit-identical; Tied is carved out of what used to be false.
        //
        // The alternative — probing for equality at the call site — was rejected because it
        // duplicates a key list that has already changed twice (#41 added span, #65 added the
        // admission rule), and with both paths tie-aware it would have had to be maintained in
        // two places. A key added here and forgotten there is a silent divergence.
        internal enum CandidateOrder
        {
            Worse,
            Tied,
            Better,
        }

        // Candidate ordering, shared by ParseInternal and TryEagerCommit so the eager verdict
        // never names a pattern the flush would not fire. Earliest start wins, then highest
        // score, then the longer consumed span, then literal count, with registration order as
        // the final deterministic fallback.
        //
        // That invariant read "the eager verdict always names the pattern the subsequent flush
        // will fire" until issue #74's DR-5 had TryEagerCommit REFUSE on a sibling tie. The old
        // wording assumed the eager path always names something; the restatement covers a
        // refusal, which names nothing — exactly as the #66 and #70 conditions there already
        // did. Restated deliberately, not broken (design §5.8).
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
        internal static CandidateOrder CompareCandidate(
            in MatchResult candidate,
            int startIdx,
            float bestScore,
            int bestStartIdx,
            int bestConsumedEndIdx,
            int bestLiteralCount
        )
        {
            if (candidate.Score <= 0f)
                return CandidateOrder.Worse;

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
            //
            // IsAdmissibleStart applies a STRICTER count (missed < matched) for a different
            // question — whether a token may terminate another command's orphan run. The two
            // are deliberately one notch apart; see the reasoning there before aligning them.
            if (candidate.MissedRequired > candidate.MatchedRequired)
                return CandidateOrder.Worse;

            // No incumbent yet, NOT a tie with a worthless one. bestScore starts at
            // float.MinValue in both loops and is written only from the adopt block, which the
            // Score <= 0f floor above guards — so a real incumbent always carries a positive
            // score and this test can only mean "nothing to compare against". Returning Tied
            // here would record a rival that does not exist.
            if (bestScore <= 0f)
                return CandidateOrder.Better;

            if (startIdx != bestStartIdx)
                return startIdx < bestStartIdx ? CandidateOrder.Better : CandidateOrder.Worse;
            if (candidate.Score != bestScore)
                return candidate.Score > bestScore ? CandidateOrder.Better : CandidateOrder.Worse;
            if (candidate.ConsumedEndIdx != bestConsumedEndIdx)
                return candidate.ConsumedEndIdx > bestConsumedEndIdx
                    ? CandidateOrder.Better
                    : CandidateOrder.Worse;
            if (candidate.LiteralCount != bestLiteralCount)
                return candidate.LiteralCount > bestLiteralCount
                    ? CandidateOrder.Better
                    : CandidateOrder.Worse;

            // Every key compared equal. The old bool ended at a strict > here and returned
            // false, indistinguishable from a loss, so the incumbent silently kept the win on
            // registration order and the tie was never recorded anywhere.
            return CandidateOrder.Tied;
        }

#if UNITY_EDITOR
        internal struct ParseDiagnosticEntry
        {
            public string PatternString;
            public int[] SlotStartWords;
            public int[] SlotEndWords;

            // The sibling that tied this command at selection, or null / -1 when none did.
            // Null is the overwhelmingly common case: it is set only when the winner was
            // decided by registration order over a rival differing on one dropped word.
            //
            // Nothing reads this to make a decision — the flush fires the same command either
            // way. It exists because the coin flip is otherwise invisible: a score of 0.67 in
            // the log looks entirely healthy, and the winner is correct by every rule the
            // parser has. Issue #74 item 3 is what will act on it.
            public string TiedSiblingIntent;
            public int TiedSiblingPatternIndex;
        }

        internal ParseDiagnosticEntry[] LastParseDiagnostics;
#endif

        // searchStart is where this extraction round began, and only the leading coverage
        // term reads it: the words charged are those between the round's origin and where
        // this candidate starts, so a second command in a multi-command utterance is never
        // charged for the tokens the first one consumed.
        //
        // forStartProbe is IsAdmissibleStart calling in from inside BuildCoverageTables, before
        // the coverage tables it would read exist. See the coverage block at the bottom.
        MatchResult TryMatchScored(
            string[] tokens,
            int startIdx,
            string[] pattern,
            int searchStart,
            bool forStartProbe = false
        )
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
            // feed the admission rule in CompareCandidate, not the score.
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
                            _slots[slotIdx].MinWords,
                            _slots[slotIdx].MaxWords,
                            out consumed,
                            valueNeeded: !forStartProbe
                        );
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
                        // Dominated as an eager-REFUSAL cause: missedRequiredSlot refuses one
                        // guard earlier, so this increment is never the sole reason a commit
                        // is refused. It is NOT dominated generally — Amendment A3 gave it a
                        // second consumer, and there it acts alone: a non-zero value here
                        // selects the forced orphan table and so changes the flush-path score.
                        // Narrowing this increment would move scores grammar-wide.
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

            // Coverage (issue #65 §5.2), computed HERE — before the caller compares
            // candidates — which is the whole of the change. The leading term used to be
            // applied to the winner alone, after selection, so that it could only filter via
            // minScore and never reorder; symptom 2 cannot be fixed under that constraint,
            // because a bare pattern that matches perfectly scores 1.0 and nothing normalised
            // to 1.0 can beat it. See DefaultCoverageWeight for what the weight means.
            //
            // Adding to the denominator cannot change the sign of a negative rawScore, so the
            // Score <= 0f floors above and in CompareCandidate behave exactly as before; and
            // since rawScore <= denominator always holds, a strictly larger denominator keeps
            // the score inside [0, 1].
            //
            // requiredAfterLastMatch selects WHICH orphan table to read (Amendment A3): a
            // candidate whose own next required element failed at this position owns the token
            // it mis-predicted and may not claim some other pattern could have begun there.
            // Both tables are built once per utterance, so coverage is three array reads.
            //
            // The start probe is the one caller that runs BEFORE those tables exist — it is
            // what builds them — so it takes no coverage term at all. That costs the probe
            // nothing it needs: it reads only rawScore's sign and the two required-element
            // counts, and coverage can only ENLARGE the denominator, so it can never flip a
            // score from positive to non-positive. Skipping it here is what keeps the probe
            // from reading a half-filled table for its own answer.
            float coverage = 0f;
            if (!forStartProbe)
            {
                AssertCoverageTablesMatch(tokens);

                coverage =
                    (
                        SkippedBefore(searchStart, startIdx)
                        + OrphanedAfter(consumedEndIdx, requiredAfterLastMatch > 0)
                    ) * _coverageWeight;
            }

            float normalizedScore = denominator > 0f ? rawScore / (denominator + coverage) : 0f;

            return new MatchResult
            {
                Score = normalizedScore,
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

        // Consecutive digit words starting at startIdx (after the leading [unk] skip), capped
        // at maxWords. Shared by TryMatchNumberSequence and CanStartPattern's slot probe so
        // the two cannot drift on what counts as a number sequence — and so the probe, which
        // only needs to know whether enough digits are there, never builds the joined string
        // it would immediately throw away.
        static int CountNumberSequenceWords(
            string[] tokens,
            int startIdx,
            int maxWords,
            out int matchStart
        )
        {
            int idx = startIdx;

            while (idx < tokens.Length && tokens[idx] == UnkToken)
                idx++;

            matchStart = idx;
            int count = 0;
            while (count < maxWords && idx < tokens.Length
                && VoxrNumberParser.DigitVocabulary.Contains(tokens[idx]))
            {
                count++;
                idx++;
            }

            return count;
        }

        // Non-null stand-in for a matched number sequence whose joined value nobody will read.
        // Only the start probe passes valueNeeded: false, and it discards every slot it
        // matches — so this exists to keep "did it match" truthful without the allocation.
        // Named rather than blank so that if it ever does leak into a VoxrCommand it is
        // greppable instead of looking like a legitimately empty slot.
        const string UnreadSlotValue = "<probe>";

        string TryMatchNumberSequence(
            string[] tokens,
            int startIdx,
            int minWords,
            int maxWords,
            out int consumed,
            bool valueNeeded = true
        )
        {
            consumed = 0;
            int count = CountNumberSequenceWords(tokens, startIdx, maxWords, out int matchStart);

            if (count < minWords)
                return null;

            consumed = matchStart + count - startIdx;

            if (count == 1)
                return tokens[matchStart];

            // Every decision above — match or no match, and how much was consumed — is already
            // made, and none of it reads the joined string. So the probe can stop here and skip
            // the only allocation on this path. CanStartPattern avoids it by calling
            // CountNumberSequenceWords directly; the probe cannot, because it needs the whole
            // pattern walked, so it opts out here instead.
            if (!valueNeeded)
                return UnreadSlotValue;

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
        //
        // No longer on the parse path — issue #65 §5.2 replaced the scan with a prefix-sum
        // subtraction (SkippedBefore) because coverage moved inside the candidate loop, where
        // an O(n) scan per candidate would multiply parse cost by utterance length. Kept as
        // the reference implementation that optimisation is pinned against.
        internal static int CountRecognisedTokens(string[] tokens, int startIdx, int endIdx)
        {
            int count = 0;
            for (int i = startIdx; i < endIdx; i++)
                if (tokens[i] != UnkToken)
                    count++;
            return count;
        }

        // -------- Coverage tables (issue #65 §5.2) --------

        // Rebuilds the per-utterance coverage tables. Every path that reaches TryMatchScored
        // — ParseInternal's extraction loop and TryEagerCommit's scan — calls this first, over
        // the token array it is about to score, and the tables then stay valid for that whole
        // parse: the leading term re-bases per round through the searchStart subtraction, and
        // the trailing term is searchStart-independent by construction.
        internal void BuildCoverageTables(string[] tokens)
        {
            int n = tokens.Length;
            if (_recognisedPrefix == null || _recognisedPrefix.Length < n + 1)
            {
                // All three grow together and are only ever read below their built length, so
                // one capacity test covers them. Adding a fourth outside this block would
                // reintroduce the per-utterance allocation the pooling exists to avoid.
                _recognisedPrefix = new int[n + 1];
                _orphanRun = new int[n + 1];
                _forcedOrphanRun = new int[n + 1];
            }

            // Binds the tables to the array they describe, so a future entry point that
            // reaches TryMatchScored without building them fails loudly instead of reading
            // another utterance's answers. The arrays are grown and never shrunk, so a stale
            // longer table returns in-range numbers for the wrong tokens — silently wrong on
            // a shorter utterance, and only out-of-range on a longer one, which is the worse
            // ordering of the two.
            _coverageTokens = tokens;

            _recognisedPrefix[0] = 0;
            for (int i = 0; i < n; i++)
                _recognisedPrefix[i + 1] = _recognisedPrefix[i] + (tokens[i] == UnkToken ? 0 : 1);

            // Backwards in one pass. _orphanRun's three cases are the ordinary rule; the
            // [unk] line carries both halves of that token's exemption at once — never
            // charged, and TRANSPARENT rather than a run terminator. A stopper would let one
            // noise token shield every real orphan behind it, so "decelerate [unk] hard burn"
            // must cost exactly what "decelerate hard burn" costs.
            //
            // The start test is IsAdmissibleStart, not CanStartPattern: the run must stop
            // wherever the MATCHER could begin, and the matcher begins patterns whose leading
            // elements were dropped (issue #82). CanStartPattern is still the cheap first half
            // of that answer.
            //
            // _forcedOrphanRun is the same quantity under Amendment A3, for a candidate whose
            // own next required element failed at this position: the first non-[unk] token is
            // charged outright instead of being tested against the start predicate, and the
            // run continues normally after it.
            //
            // Without that, the rule rewards matching LESS. Take ["switch","to","weapons"] and
            // ["switch","to","navigation"] on "switch to weapons target hotel". The navigation
            // pattern MISSES its final element, so its consumed span stops at "weapons" —
            // which begins ["weapons","mode"], terminating its orphan run at zero for a tidy
            // 2/3. The weapons pattern MATCHES that element, so its origin moves past the very
            // token that would have terminated its own run, and pays for "target hotel":
            // 3/(3+2) = 0.6. The wrong command wins by 0.067 and fires. Measured: safe at one
            // leftover token, flips at two.
            //
            // Tabulated here rather than branched at the call site because the A3 rule is a
            // function of the token array alone — requiredAfterLastMatch only selects WHICH
            // table to read — which keeps the per-candidate cost at one array index and keeps
            // both forms of the rule at the one site that defines them.
            _orphanRun[n] = 0;
            _forcedOrphanRun[n] = 0;
            for (int i = n - 1; i >= 0; i--)
            {
                if (tokens[i] == UnkToken)
                {
                    _orphanRun[i] = _orphanRun[i + 1];
                    _forcedOrphanRun[i] = _forcedOrphanRun[i + 1];
                    continue;
                }

                _orphanRun[i] = IsAdmissibleStart(tokens, i) ? 0 : 1 + _orphanRun[i + 1];
                _forcedOrphanRun[i] = 1 + _orphanRun[i + 1];
            }
        }

        // Guards the precondition that BuildCoverageTables ran over the array being scored.
        // Editor-only: the coupling is real but the check is not worth a branch in a shipped
        // build, and both production callers build the tables at the top of the same method.
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        void AssertCoverageTablesMatch(string[] tokens)
        {
            if (!ReferenceEquals(_coverageTokens, tokens))
            {
                throw new InvalidOperationException(
                    "Coverage tables were not built for this token array — call "
                        + "BuildCoverageTables(tokens) before scoring candidates over it."
                );
            }
        }

        // In-grammar words the sliding start walked past this round — the count
        // CountRecognisedTokens produces by scanning, in O(1). Coverage sits inside a triple
        // nested loop (commands x patterns x start index), so a scan here would multiply
        // parse cost by utterance length.
        internal int SkippedBefore(int searchStart, int startIdx)
        {
            return _recognisedPrefix[startIdx] - _recognisedPrefix[searchStart];
        }

        // Tokens left unexplained in the run starting where the candidate's last match ended,
        // under whichever of the two rules applies to it.
        //
        // Measured from ConsumedEndIdx rather than EndIdx: the two provably agree today (the
        // only thing that moves the cursor without recording a match is the [unk] skip, and
        // [unk] is transparent above), but ConsumedEndIdx is the index whose correctness does
        // not depend on that staying true.
        //
        // `ownsTheNextToken` is Amendment A3: pass true when the candidate's own next required
        // element failed at this position, so it may not claim that some OTHER pattern could
        // have begun on the token it just mis-predicted.
        internal int OrphanedAfter(int consumedEndIdx, bool ownsTheNextToken = false)
        {
            return ownsTheNextToken ? _forcedOrphanRun[consumedEndIdx] : _orphanRun[consumedEndIdx];
        }

        // Could the MATCHER begin a match at tokens[idx]? This is the orphan run's terminator,
        // and it exists because CanStartPattern below answers a strictly narrower question than
        // the one the run needs to ask (issue #82).
        //
        // CanStartPattern asks whether a pattern's FIRST matchable element matches here.
        // ParseInternal asks no such thing: it tries every pattern at every non-[unk] index, so
        // a pattern whose leading elements VOSK dropped begins wherever its surviving elements
        // do — which is the single most common decoder failure and the reason coverage exists.
        // The two disagreed, and the disagreement charged one command for the next command's
        // tokens: in "cease fire target hotel one", "target" begins no pattern as a first
        // element, yet `approach target {target}` does match there and a later extraction round
        // does fire it, so cease_fire paid 0.4 for words that were never orphans.
        //
        // So the run asks the matcher instead. A position is a start when some active pattern,
        // started there, matches STRICTLY MORE of its required elements than it misses.
        //
        // That threshold is the whole of what keeps this from being the "crude widening" the
        // design refused, and it is deliberately one notch STRONGER than DR-7's admission rule
        // (`missed <= matched`, CompareCandidate's admission rule). DR-7 asks "is this a candidate at
        // all?" — a question answered for a candidate that may still lose its round and never
        // fire. Terminating another command's orphan run is a bigger claim: it moves score off
        // a candidate that IS firing, so it takes strictly more evidence for than against.
        //
        // The gap between the two is not academic; it is the whole #42 regression. Take the
        // pair command-recognition.md recommends as the safe remedy — ["decelerate"] beside
        // ["decelerate","?by","{burn_level}"] — on "decelerate hard burn please". Probed from
        // "hard", the slot-filled pattern misses "decelerate" (1) and matches {burn_level} (1).
        // Under `missed <= matched` that is admissible, so the value the bare pattern strands
        // terminates the bare pattern's own orphan run: bare scores 1/(1+0) = 1.00 against the
        // slot-filled 2/(2+1) = 0.67, the bare command fires, and the burn level the speaker
        // said is discarded — which is #42, reverted, on the grammar the docs prescribe.
        // Under `missed < matched` the probe refuses it and the charge stands.
        //
        // Counted over the WHOLE pattern, not over the elements skipped to reach idx: a pattern
        // is being asked whether it plausibly explains this token as a command, and a trailing
        // required element it also matched is evidence for exactly that.
        //
        // Two properties worth stating, because F11 is argued against this method:
        //   - The threshold is a COUNT, like DR-7 itself: no knob, nothing to configure, and
        //     independent of minScore and of coverageWeight.
        //   - It only ever ADDS claims. CanStartPattern is consulted first and is never
        //     overruled, so a pattern that begins here keeps terminating the run whatever DR-7
        //     would say about the candidate — which is the half of F11 that
        //     Coverage_ResidualHazard_WhenTheStrandedValueBeginsAPattern pins.
        //   - The answer is a function of (grammar, tokens, idx) alone. It reads no other
        //     candidate's verdict, no searchStart, and nothing from the selection round, so
        //     coverage does not become a function of DR-7's verdicts — the property F11 was
        //     narrowed to protect after the #78 review.
        //
        // Cost is one extraction round's work per utterance, paid once here rather than per
        // candidate, and only at positions the cheap test already declined.
        //
        // Callers must not pass an [unk] index; BuildCoverageTables settles those first.
        internal bool IsAdmissibleStart(string[] tokens, int idx)
        {
            if (CanStartPattern(tokens, idx))
                return true;

            // At weight 0 coverage is identically zero, so no orphan count can reach any score
            // and the sweep below is pure waste — on TryEagerCommit that is waste per partial
            // result, not per utterance. Falling back to the cheap predicate keeps parse
            // results bit-identical (0 x anything is 0) while skipping it.
            //
            // This does mean _orphanRun holds the narrower CanStartPattern answer on a weight-0
            // parser. Nothing can observe that through scoring; it is visible only to a test
            // reading OrphanedAfter directly, which is why there is one pinning both halves.
            if (_coverageWeight <= 0f)
                return false;

            for (int ci = 0; ci < _commands.Length; ci++)
            {
                var patterns = _commands[ci].Patterns;
                for (int pi = 0; pi < patterns.Length; pi++)
                {
                    // searchStart is idx so the leading term would be zero anyway; the probe
                    // suppresses the whole coverage term regardless.
                    var probe = TryMatchScored(tokens, idx, patterns[pi], idx, forStartProbe: true);

                    if (probe.Score > 0f && probe.MissedRequired < probe.MatchedRequired)
                        return true;
                }
            }

            return false;
        }

        // Could any registered active pattern begin a match at tokens[idx]? Deliberately
        // CONSERVATIVE: where the answer is uncertain it must be yes, and nothing is charged.
        // Corrected by Amendment A3: the asymmetry below held only while coverage sat BELOW
        // the selection barrier. Once coverage reorders, under-charging ONE candidate relative
        // to a sibling is not "today's behaviour" — it is how the wrong command came to beat
        // the right one, which is what A3 exists to close. The conservative default stands;
        // the argument for it does not extend to the reordering regime.
        //
        // The failure modes are not symmetric — over-charging destroys sequential extraction,
        // while under-charging merely leaves a score where it already is today.
        //
        // Callers must not pass an [unk] index; BuildCoverageTables settles those first.
        internal bool CanStartPattern(string[] tokens, int idx)
        {
            if (_startLiterals.Contains(tokens[idx]))
                return true;

            for (int s = 0; s < _startSlots.Length; s++)
            {
                int slotIdx = _startSlots[s];
                if (_slots[slotIdx].Type == VoxrSlotType.NumberSequence)
                {
                    // Capped at minWords, not maxWords: the question is only whether enough
                    // digits are present to satisfy the slot, so there is no reason to walk
                    // the rest of the run.
                    int minWords = _slots[slotIdx].MinWords;
                    if (CountNumberSequenceWords(tokens, idx, minWords, out _) >= minWords)
                        return true;
                }
                else if (TryMatchSlot(tokens, idx, slotIdx, out _) != null)
                {
                    return true;
                }
            }

            return false;
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

            // Only when the extendability analysis survived. A null _canCommitEarly returns
            // HoldExtendable below before any verdict can consult a sibling rival, so building
            // the lookup for such a grammar is work nothing can read — and it is not small:
            // measured at 1.2 ms and ~1.2 MB on a grammar that abandons, against 0.03 ms and
            // 219 B without it. AreSiblingRivals already answers false on a null lookup, so the
            // recording below simply never fires.
            if (_canCommitEarly != null)
                EnsureSiblingLookup();

            BuildCoverageTables(tokens);

            float bestScore = float.MinValue;
            int bestLiteralCount = -1;
            int bestCommandIdx = -1;
            int bestPatternIdx = -1;
            int bestStartIdx = int.MaxValue;
            int bestEndIdx = 0;
            int bestConsumedEndIdx = 0;
            bool bestMissedRequiredSlot = false;
            bool bestHasUnmatchedRequiredTail = false;

            // The first sibling rival found tying the current incumbent, or -1. Cleared
            // whenever a new incumbent is adopted: Tied means the whole key tuple compared
            // equal, so a candidate that beats the incumbent beats everything tied with it and
            // the recorded rival is stale by construction.
            //
            // The FIRST sibling rival rather than the most recent rival of any kind, and that
            // matters: a winner can be tied by several candidates, only some of them siblings.
            // Keeping "the last thing that tied" and testing sibling-ness afterwards would drop
            // a sibling rival whenever a non-sibling happened to be enumerated after it.
            //
            // Both indices, though this method reads only the first and only as a flag: design
            // §5.3 asks each selection loop to record "whether an equally-good rival was seen
            // and WHICH PATTERN it was", and ParseInternal's copy does consume both. Kept in
            // step so the two loops record the same thing; a bool here would make them differ
            // in shape for no gain, since the verdict names nothing either way (DR-5).
            int bestTiedSiblingCommandIdx = -1;
            int bestTiedSiblingPatternIdx = -1;

            for (int ci = 0; ci < _commands.Length; ci++)
            {
                var patterns = _commands[ci].Patterns;
                for (int pi = 0; pi < patterns.Length; pi++)
                {
                    for (int startIdx = 0; startIdx < tokens.Length; startIdx++)
                    {
                        if (tokens[startIdx] == UnkToken)
                            continue;

                        // searchStart is 0: this scan mirrors ParseInternal's first round.
                        var matchResult = TryMatchScored(tokens, startIdx, patterns[pi], 0);

                        var order = CompareCandidate(
                            matchResult,
                            startIdx,
                            bestScore,
                            bestStartIdx,
                            bestConsumedEndIdx,
                            bestLiteralCount
                        );

                        if (order == CandidateOrder.Better)
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
                            bestTiedSiblingCommandIdx = -1;
                            bestTiedSiblingPatternIdx = -1;
                        }
                        else if (
                            order == CandidateOrder.Tied
                            && bestTiedSiblingCommandIdx < 0
                            && AreSiblingRivals(bestCommandIdx, bestPatternIdx, ci, pi)
                        )
                        {
                            bestTiedSiblingCommandIdx = ci;
                            bestTiedSiblingPatternIdx = pi;
                        }
                    }
                }
            }

            if (bestCommandIdx < 0 || bestScore < minScore)
                return EagerCommitVerdict.None;

            // Note one condition that is NOT listed below because it has already run: the
            // admission rule in CompareCandidate (issue #65, DR-7) refuses any candidate
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
            // last token of the buffer. Nothing the speaker says next was owed to it, so
            // COMPLETENESS is satisfied and this condition lets it through.
            //
            // That is not the same as safe. This condition is silent about AMBIGUITY, and a
            // medial drop between two cross-intent siblings is exactly the case it lets past
            // (issue #74 design §2.8). The sibling condition at the end of this method is what
            // catches that; it sits there rather than here so that it can refuse a Commit
            // without also lengthening a HoldExtendable.
            if (bestHasUnmatchedRequiredTail)
                return EagerCommitVerdict.None;

            // The match must span the whole buffer from the first recognised token: anything
            // left over at the END (including trailing [unk]) means an in-progress tail that
            // more speech could still complete. A LEADING [unk] run carries no such ambiguity
            // — nothing arriving later extends the utterance leftward — so out-of-grammar
            // preamble ("Helm, coast") is skipped rather than blocking the commit (issue #43),
            // matching the sliding start that already absorbs it for free everywhere else.
            //
            // Only [unk] may precede the match. This scan and the flush now compute the SAME
            // score — both charge leading skipped words through TryMatchScored (issue #65
            // §5.2), where the flush path once applied that charge after selection and this
            // scan omitted it — so the two can no longer diverge, and this condition is not
            // what keeps them aligned. What it still buys is completeness: a leading run of
            // recognised words the match did not consume means the buffer holds more than
            // this command, and committing would fire on part of it.
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
            if (!CanCommitEarly(bestCommandIdx, bestPatternIdx))
                return EagerCommitVerdict.HoldExtendable;

            // Last, and deliberately last: a sibling tie means the buffer fits two intents
            // exactly equally, so the winner was picked by registration order alone. Refuse
            // rather than fire on that (design DR-5, §5.8).
            //
            // Note what refusing does NOT buy, because the design's own wording oversells it.
            // §5.8 says "the missing word may still arrive" — true of the TRAILING shape, which
            // the #70 condition above already refuses. It is false here: reaching this line
            // means #70 passed, so an element AFTER the dropped discriminator already matched,
            // and results only ever append to the buffer. The word cannot land in a position
            // the match has gone past. It was spoken and lost.
            //
            // What refusing buys is that the decision moves to the flush, where the transcript
            // is final and item 3 can ASK which intent was meant instead of guessing. Until
            // then the same intent fires, a buffer window later.
            //
            // This is still the natural extension of the #70 tail condition to the case #70
            // structurally cannot reach: that one covers a discriminator the speaker has not
            // said yet, this one a discriminator the recogniser dropped mid-utterance.
            //
            // It sits AFTER both HoldExtendable returns on purpose. Placed beside #70 it would
            // also convert HoldExtendable into None — lengthening the wait from
            // prefixHoldSeconds to the full bufferWindow on a buffer that was never going to
            // commit early anyway. There is nothing to refuse when nothing was being offered,
            // and a refusal must not lengthen a wait it is not responsible for. Here the only
            // transition it introduces is Commit -> None.
            if (bestTiedSiblingCommandIdx >= 0)
                return EagerCommitVerdict.None;

            return EagerCommitVerdict.Commit;
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
