using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VoXR.Commands;

namespace VoXR.Tests.Runtime
{
    // Unit tests for the eager-flush precompute (CanCommitEarly) and the speculative
    // TryEagerCommit gate on VoxrCommandParser (issue #25), including the
    // complete-but-extendable verdict that drives the shortened prefix hold (issue #32)
    // and the value-aware slot/literal compatibility that frees lone-slot patterns
    // to commit early (issue #33).
    public class VoxrEagerCommitTests
    {
        static VoxrSlotDefinition[] Slots(params VoxrSlotDefinition[] s) => s;
        static VoxrCommandDefinition[] Commands(params VoxrCommandDefinition[] c) => c;
        static VoxrCommandDefinition Cmd(string intent, params string[][] patterns)
            => new VoxrCommandDefinition(intent, patterns);
        static string[] P(params string[] tokens) => tokens;
        static string[] Tok(string text) => text.Split(' ');

        // ---------- CanCommitEarly classification ----------

        [Test]
        public void Terminal_LiteralEnding_IsCommittable()
        {
            var parser = new VoxrCommandParser(
                Slots(),
                Commands(Cmd("cease_fire", P("cease", "fire"))));

            Assert.IsTrue(parser.CanCommitEarly(0, 0));
        }

        [Test]
        public void Terminal_EnumeratedSlotEnding_NoValuePrefix_IsCommittable()
        {
            var parser = new VoxrCommandParser(
                Slots(new VoxrSlotDefinition("weapon", new[] { "missiles", "torpedoes" })),
                Commands(Cmd("fire", P("fire", "{weapon}"))));

            Assert.IsTrue(parser.CanCommitEarly(0, 0));
        }

        [Test]
        public void PrefixOfAnotherCommand_IsNotCommittable()
        {
            var parser = new VoxrCommandParser(
                Slots(new VoxrSlotDefinition("target", new[] { "hotel one" })),
                Commands(
                    Cmd("status", P("status")),
                    Cmd("status_report", P("status", "report", "{target}"))));

            Assert.IsFalse(parser.CanCommitEarly(0, 0),
                "\"status\" is a prefix of \"status report {target}\"");
            Assert.IsTrue(parser.CanCommitEarly(1, 0),
                "the longer command is terminal and not a prefix of anything");
        }

        [Test]
        public void PrefixOfSameCommandLongerPattern_IsNotCommittable()
        {
            var parser = new VoxrCommandParser(
                Slots(),
                Commands(Cmd("go", P("go"), P("go", "now"))));

            Assert.IsFalse(parser.CanCommitEarly(0, 0), "[go] is a prefix of [go, now]");
            Assert.IsTrue(parser.CanCommitEarly(0, 1), "[go, now] cannot be extended");
        }

        [Test]
        public void TrailingOptional_IsNotCommittable()
        {
            var parser = new VoxrCommandParser(
                Slots(),
                Commands(Cmd("go", P("go", "?now"))));

            Assert.IsFalse(parser.CanCommitEarly(0, 0));
        }

        [Test]
        public void LeadingOptionalInOtherCommand_StillDetectsPrefix()
        {
            // "status" must wait because dropping the optional "?please" makes
            // "status report" a valid utterance of the longer command.
            var parser = new VoxrCommandParser(
                Slots(),
                Commands(
                    Cmd("status", P("status")),
                    Cmd("polite_status", P("?please", "status", "report"))));

            Assert.IsFalse(parser.CanCommitEarly(0, 0),
                "expansion over optionals must catch the shifted prefix");
        }

        [Test]
        public void TrailingExtensibleEnumeratedSlot_IsNotCommittable()
        {
            var parser = new VoxrCommandParser(
                Slots(new VoxrSlotDefinition("colour", new[] { "red", "red dragon" })),
                Commands(Cmd("pick", P("pick", "{colour}"))));

            Assert.IsFalse(parser.CanCommitEarly(0, 0), "\"red\" can grow into \"red dragon\"");
        }

        [Test]
        public void AliasKeyThatIsWordPrefixOfAnotherSurfaceForm_IsNotCommittable()
        {
            var slot = new VoxrSlotDefinition("colour",
                new[] { "crimson" },
                new Dictionary<string, string> { { "red", "crimson" }, { "red dragon", "crimson" } });
            var parser = new VoxrCommandParser(
                Slots(slot),
                Commands(Cmd("pick", P("pick", "{colour}"))));

            Assert.IsFalse(parser.CanCommitEarly(0, 0),
                "alias \"red\" is a word-prefix of alias \"red dragon\"");
        }

        [Test]
        public void NumberSequence_FixedWidth_IsCommittable()
        {
            var parser = new VoxrCommandParser(
                Slots(VoxrSlotDefinition.NumberSequence("code", 3, 3)),
                Commands(Cmd("enter", P("enter", "{code}"))));

            Assert.IsTrue(parser.CanCommitEarly(0, 0), "min == max can't grow");
        }

        [Test]
        public void NumberSequence_VariableWidth_IsNotCommittable()
        {
            var parser = new VoxrCommandParser(
                Slots(VoxrSlotDefinition.NumberSequence("code", 1, 3)),
                Commands(Cmd("enter", P("enter", "{code}"))));

            Assert.IsFalse(parser.CanCommitEarly(0, 0), "min < max can absorb more digits");
        }

        // ---------- Cross-pattern word-level prefix (issue #25, review fix #1) ----------

        [Test]
        public void CrossSlotMultiWordValue_IsNotCommittable()
        {
            // "go {dir}" and "go {place}" have equal element counts, so the element-count
            // prefix check misses them — but "go north" (a full match of the first) is a word-
            // prefix of "go north pole" (a full match of the second), so the first must wait.
            var parser = new VoxrCommandParser(
                Slots(
                    new VoxrSlotDefinition("dir", new[] { "north" }),
                    new VoxrSlotDefinition("place", new[] { "north pole" })),
                Commands(
                    Cmd("go_dir", P("go", "{dir}")),
                    Cmd("go_place", P("go", "{place}"))));

            Assert.IsFalse(parser.CanCommitEarly(0, 0),
                "\"go north\" is a word-prefix of \"go north pole\"");
        }

        [Test]
        public void FixedWidthNumberSequence_WithWiderSibling_IsNotCommittable()
        {
            // Both commands end in a fixed-width number sequence. "dial 1 2 3" (a full match of
            // n3) is a word-prefix of "dial 1 2 3 4 5" (a full match of n5), so the narrower
            // command must wait. The wider one cannot be a prefix of the narrower, so it stays
            // committable — the cardinal rule only forbids firing too early, not too late.
            var parser = new VoxrCommandParser(
                Slots(
                    VoxrSlotDefinition.NumberSequence("n3", 3, 3),
                    VoxrSlotDefinition.NumberSequence("n5", 5, 5)),
                Commands(
                    Cmd("dial3", P("dial", "{n3}")),
                    Cmd("dial5", P("dial", "{n5}"))));

            Assert.IsFalse(parser.CanCommitEarly(0, 0),
                "the 3-digit command is a word-prefix of the 5-digit one");
            Assert.IsTrue(parser.CanCommitEarly(1, 0),
                "the 5-digit command is terminal and is not a prefix of the shorter sibling");
        }

        [Test]
        public void SharedVerbDistinctValues_StillCommittable()
        {
            // The tight rule must not over-suppress: "select {item}" shares the verb "select"
            // with "select all", but "all" is not a value of {item}, so "select red" can never
            // grow into "select all". The slotted command stays eligible for eager commit.
            var parser = new VoxrCommandParser(
                Slots(new VoxrSlotDefinition("item", new[] { "red" })),
                Commands(
                    Cmd("select_item", P("select", "{item}")),
                    Cmd("select_all", P("select", "all"))));

            Assert.IsTrue(parser.CanCommitEarly(0, 0),
                "\"select red\" cannot be extended into \"select all\"");
        }

        // ---------- Value-aware slot/literal compatibility (issue #33) ----------

        [Test]
        public void LoneSlotPattern_VocabularyStartsNoOtherPattern_IsCommittable()
        {
            // A bare single-slot pattern used to be judged a potential prefix of every longer
            // pattern in the grammar, because any slot position counted as compatible with
            // anything. No surface form of {burn_level} begins with "close" or "decelerate",
            // so "coast" cannot be the opening of either command and is safe to fire at once.
            var parser = new VoxrCommandParser(
                Slots(new VoxrSlotDefinition("burn_level", new[] { "coast", "hard burn" })),
                Commands(
                    Cmd("set_burn", P("{burn_level}")),
                    Cmd("close", P("close", "distance")),
                    Cmd("decelerate", P("decelerate", "{burn_level}"))));

            Assert.IsTrue(parser.CanCommitEarly(0, 0),
                "no value of {burn_level} starts either of the longer patterns");
        }

        [Test]
        public void LoneSlotPattern_ValueStartsAnotherPattern_IsNotCommittable()
        {
            // The tighter rule must not over-fire: "hard burn" IS a value of {burn_level}, and
            // a full match of it is a word-prefix of "hard burn now", so the lone-slot pattern
            // is genuinely extendable and must keep waiting.
            var parser = new VoxrCommandParser(
                Slots(new VoxrSlotDefinition("burn_level", new[] { "coast", "hard burn" })),
                Commands(
                    Cmd("set_burn", P("{burn_level}")),
                    Cmd("burn_now", P("hard", "burn", "now"))));

            Assert.IsFalse(parser.CanCommitEarly(0, 0),
                "\"hard burn\" can still grow into \"hard burn now\"");
        }

        [Test]
        public void SlotAfterSharedLiteralRun_VocabularyRulesOutLongerPattern_IsCommittable()
        {
            // Value-awareness is not limited to the first element: after the identical literal
            // "set" the two forms have consumed the same words, so the slot is still word-
            // aligned and no {level} value is "throttle".
            var parser = new VoxrCommandParser(
                Slots(
                    new VoxrSlotDefinition("level", new[] { "coast" }),
                    VoxrSlotDefinition.NumberSequence("code", 2, 2)),
                Commands(
                    Cmd("set_level", P("set", "{level}")),
                    Cmd("set_throttle", P("set", "throttle", "{code}"))));

            Assert.IsTrue(parser.CanCommitEarly(0, 0),
                "\"set coast\" cannot be extended into \"set throttle ...\"");
        }

        [Test]
        public void SlotPastAnEarlierSlot_StaysConservativelyHeld()
        {
            // The vocabulary test only applies where both forms have provably consumed the same
            // words. {target} may be one word or two, so {mode} is not reliably facing "two" —
            // and the hazard is real: "alpha two silent" (target = "alpha two") is a word-prefix
            // of "alpha two silent now" (target = "alpha"). Past the first slot the check stays
            // conservative, so the shorter command keeps waiting.
            var parser = new VoxrCommandParser(
                Slots(
                    new VoxrSlotDefinition("target", new[] { "alpha", "alpha two" }),
                    new VoxrSlotDefinition("mode", new[] { "silent" })),
                Commands(
                    Cmd("engage", P("{target}", "{mode}")),
                    Cmd("engage_now", P("{target}", "two", "silent", "now"))));

            Assert.IsFalse(parser.CanCommitEarly(0, 0),
                "an earlier slot can shift the words, so the vocabulary test must not be applied");
        }

        [Test]
        public void LoneNumberSequenceSlot_NonDigitLiteral_IsCommittable()
        {
            // A number sequence matches digit words only, so a fixed-width one standing alone
            // cannot be the opening of a command that starts with "abort".
            var parser = new VoxrCommandParser(
                Slots(VoxrSlotDefinition.NumberSequence("code", 3, 3)),
                Commands(
                    Cmd("enter", P("{code}")),
                    Cmd("abort", P("abort", "now"))));

            Assert.IsTrue(parser.CanCommitEarly(0, 0), "\"abort\" is not a digit word");

            var digitLiteral = new VoxrCommandParser(
                Slots(VoxrSlotDefinition.NumberSequence("code", 3, 3)),
                Commands(
                    Cmd("enter", P("{code}")),
                    Cmd("nine_lives", P("nine", "lives", "left"))));

            Assert.IsFalse(digitLiteral.CanCommitEarly(0, 0),
                "a code can start with the digit word \"nine\", so the pair stays held");
        }

        [Test]
        public void TryEagerCommit_LoneSlotCommand_ReturnsCommit()
        {
            // The payoff at the buffer level: a bare {burn_level} utterance now commits instead
            // of paying the whole buffer window for an ambiguity that does not exist.
            var parser = new VoxrCommandParser(
                Slots(new VoxrSlotDefinition("burn_level", new[] { "coast", "hard burn" })),
                Commands(
                    Cmd("set_burn", P("{burn_level}")),
                    Cmd("decelerate", P("decelerate", "{burn_level}"))));

            Assert.AreEqual(EagerCommitVerdict.Commit,
                parser.TryEagerCommit(Tok("coast"), null, 0.6f, 0.4f));
        }

        // ---------- Optional-expansion guard (issue #25, review fix #2) ----------

        // A command carrying 13 optional literals — one past MaxOptionalExpansion — so the
        // eligibility analysis is abandoned for whatever set it appears in. The elements are
        // written space-separated for legibility; Tok is just a split.
        static VoxrCommandDefinition OverLimitCommand() =>
            Cmd("noisy", Tok("noisy ?a ?b ?c ?d ?e ?f ?g ?h ?i ?j ?k ?l ?m"));

        // The same command exactly at the limit (12 optionals), which is still analysable.
        static VoxrCommandDefinition AtLimitCommand() =>
            Cmd("noisy", Tok("noisy ?a ?b ?c ?d ?e ?f ?g ?h ?i ?j ?k ?l"));

        [Test]
        public void ManyOptionalElements_DisablesEagerCommitForWholeParser()
        {
            // ExpandOptionals enumerates 2^optionals concrete forms; past MaxOptionalExpansion
            // (12) the parser refuses to analyse and disables eager commit for the whole command
            // set rather than overflow or partially (and unsoundly) analyse a single pattern.
            //
            // The warning is authoring-time now (issue #44), so it lands during construction,
            // before any eager probe — hence the expectation goes up here.
            LogAssert.Expect(LogType.Warning, new Regex("more than the 12"));

            var parser = new VoxrCommandParser(
                Slots(),
                Commands(OverLimitCommand(), Cmd("cease_fire", P("cease", "fire")))
            );

            bool overLimit = true, normal = true;
            Assert.DoesNotThrow(() =>
            {
                overLimit = parser.CanCommitEarly(0, 0); // triggers the lazy precompute
                normal = parser.CanCommitEarly(1, 0);
            });

            Assert.IsFalse(overLimit, "the over-limit pattern is disabled");
            Assert.IsFalse(normal,
                "a normal command in the same set is also disabled (whole-parser, never partial)");
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void ManyOptionalElements_WarnsAtConstruction_NamingThePattern()
        {
            // The condition is knowable from the assets alone, so the author must learn about
            // it — and about which pattern caused it — without a play session, and without
            // eager flush being enabled at all (issue #44). Nothing here probes eager commit.
            LogAssert.Expect(
                LogType.Warning,
                new Regex(@"Pattern ""noisy \?a .*"" \(intent 'noisy'\) has 13 optional elements")
            );

            var parser = new VoxrCommandParser(
                Slots(),
                Commands(OverLimitCommand(), Cmd("cease_fire", P("cease", "fire")))
            );

            Assert.IsNotNull(parser);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void OptionalElementsAtTheLimit_DoNotWarn()
        {
            // Exactly MaxOptionalExpansion is still analysable — the guard is strictly "more
            // than" — so an at-the-limit pattern must stay silent and keep the analysis.
            var parser = new VoxrCommandParser(
                Slots(),
                Commands(AtLimitCommand(), Cmd("cease_fire", P("cease", "fire")))
            );

            Assert.IsTrue(
                parser.CanCommitEarly(1, 0),
                "the analysis still runs, so an unextendable command commits early"
            );
        }

        [Test]
        public void CanCommitEarly_OutOfRange_ReturnsFalse()
        {
            var parser = new VoxrCommandParser(
                Slots(),
                Commands(Cmd("cease_fire", P("cease", "fire"))));

            Assert.IsFalse(parser.CanCommitEarly(5, 0));
            Assert.IsFalse(parser.CanCommitEarly(0, 5));
            Assert.IsFalse(parser.CanCommitEarly(-1, -1));
        }

        // ---------- TryEagerCommit gate ----------

        [Test]
        public void TryEagerCommit_FullTerminalMatch_ReturnsCommit()
        {
            var parser = new VoxrCommandParser(
                Slots(new VoxrSlotDefinition("weapon", new[] { "missiles" })),
                Commands(Cmd("fire", P("fire", "{weapon}"))));

            Assert.AreEqual(EagerCommitVerdict.Commit,
                parser.TryEagerCommit(Tok("fire missiles"), null, 0.6f, 0.4f));
        }

        [Test]
        public void TryEagerCommit_BelowScoreThreshold_ReturnsNone()
        {
            var parser = new VoxrCommandParser(
                Slots(
                    new VoxrSlotDefinition("weapon", new[] { "missiles" }),
                    new VoxrSlotDefinition("target", new[] { "hotel one" })),
                Commands(Cmd("launch", P("launch", "{weapon}", "target", "{target}"))));

            // {target} unfilled -> score 0.5 < 0.6 -> not eligible.
            Assert.AreEqual(EagerCommitVerdict.None,
                parser.TryEagerCommit(Tok("launch missiles target"), null, 0.6f, 0.4f));
        }

        [Test]
        public void TryEagerCommit_LeftoverTrailingTokens_ReturnsNone()
        {
            var parser = new VoxrCommandParser(
                Slots(),
                Commands(Cmd("cease_fire", P("cease", "fire"))));

            // "cease fire" matches fully and is committable, but "now" is unconsumed,
            // so the match does not span the whole buffer.
            Assert.AreEqual(EagerCommitVerdict.None,
                parser.TryEagerCommit(Tok("cease fire now"), null, 0.6f, 0.4f));
        }

        [Test]
        public void TryEagerCommit_PrefixCommand_ReturnsHoldExtendable()
        {
            var parser = new VoxrCommandParser(
                Slots(new VoxrSlotDefinition("target", new[] { "hotel one" })),
                Commands(
                    Cmd("status", P("status")),
                    Cmd("status_report", P("status", "report", "{target}"))));

            Assert.AreEqual(EagerCommitVerdict.HoldExtendable,
                parser.TryEagerCommit(Tok("status"), null, 0.6f, 0.4f),
                "a prefix command matches fully but could still grow, so it is held, not committed");
        }

        // ---------- HoldExtendable classification (issue #32) ----------

        [Test]
        public void TryEagerCommit_TrailingExtensibleSlot_ReturnsHoldExtendable()
        {
            // Not a prefix of another pattern — the same pattern's own trailing value can
            // grow ("red" -> "red dragon"). Still a complete match awaiting a continuation.
            var parser = new VoxrCommandParser(
                Slots(new VoxrSlotDefinition("colour", new[] { "red", "red dragon" })),
                Commands(Cmd("pick", P("pick", "{colour}"))));

            Assert.AreEqual(EagerCommitVerdict.HoldExtendable,
                parser.TryEagerCommit(Tok("pick red"), null, 0.6f, 0.4f));
        }

        [Test]
        public void TryEagerCommit_NoMatchAtAll_ReturnsNone()
        {
            var parser = new VoxrCommandParser(
                Slots(),
                Commands(Cmd("cease_fire", P("cease", "fire"))));

            Assert.AreEqual(EagerCommitVerdict.None,
                parser.TryEagerCommit(Tok("banana"), null, 0.6f, 0.4f),
                "speech that matches nothing must not arm the shortened hold");
        }

        [Test]
        public void TryEagerCommit_UnanalysableGrammar_ReturnsHoldExtendable()
        {
            // Past MaxOptionalExpansion the eager precompute is abandoned for the whole
            // parser, so nothing may commit early. Everything the hold asserts has still been
            // established though — one complete, confident, whole-buffer match — so the
            // verdict degrades to HoldExtendable rather than None (issue #44), which costs
            // the un-analysable grammar prefixHoldSeconds instead of the full window.
            LogAssert.Expect(LogType.Warning, new Regex("more than the 12"));

            var parser = new VoxrCommandParser(
                Slots(),
                Commands(OverLimitCommand(), Cmd("cease_fire", P("cease", "fire")))
            );

            Assert.AreEqual(
                EagerCommitVerdict.HoldExtendable,
                parser.TryEagerCommit(Tok("cease fire"), null, 0.6f, 0.4f),
                "an un-analysable grammar holds — it never commits early"
            );
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void TryEagerCommit_UnanalysableGrammar_IncompleteSpeech_StillReturnsNone()
        {
            // The degrade rides on the gates above it, not around them: speech that is not a
            // complete, confident, whole-buffer match must still report None, so the split
            // command it might be continuing keeps the full window to arrive in.
            LogAssert.Expect(LogType.Warning, new Regex("more than the 12"));

            var parser = new VoxrCommandParser(
                Slots(),
                Commands(OverLimitCommand(), Cmd("cease_fire", P("cease", "fire")))
            );

            Assert.AreEqual(EagerCommitVerdict.None,
                parser.TryEagerCommit(Tok("cease"), null, 0.6f, 0.4f),
                "half a command is not a complete match, analysed or not"
            );
            Assert.AreEqual(
                EagerCommitVerdict.None,
                parser.TryEagerCommit(Tok("banana"), null, 0.6f, 0.4f),
                "speech matching nothing must not arm the shortened hold"
            );
            LogAssert.NoUnexpectedReceived();
        }

        // ---------- Tie-break parity with ParseInternal (Review MF-5) ----------

        [Test]
        public void TryEagerCommit_AgreesWithParseInternalSelection()
        {
            // ParseInternal fires "greet" for input "hello"; the eager gate must agree
            // that this very command is NOT safe to commit early (it is a prefix of
            // "hello there"). This guards the duplicated selection scan from drifting.
            var parser = new VoxrCommandParser(
                Slots(),
                Commands(
                    Cmd("greet", P("hello")),
                    Cmd("greet_long", P("hello", "there"))));

            var results = parser.Parse("hello");
            Assert.AreEqual(1, results.Length);
            Assert.AreEqual("greet", results[0].Command.Intent);
            Assert.AreEqual(0, results[0].Command.MatchedPatternIndex);

            Assert.IsFalse(parser.CanCommitEarly(0, 0), "greet is a prefix of greet_long");
            Assert.AreEqual(EagerCommitVerdict.HoldExtendable,
                parser.TryEagerCommit(Tok("hello"), null, 0.6f, 0.4f),
                "eager verdict must match the non-committable selection ParseInternal made");
        }

        [Test]
        public void TryEagerCommit_PrefersLongerSpanOnTie_LikeParseInternal()
        {
            // The bare sibling is listed first and ties the tailed pattern at 1.0 with an
            // equal literal count, so before the span tie-break (issue #41) both scans
            // picked it: ParseInternal split the utterance in two, and the eager scan saw
            // a match that stopped short of the buffer end and reported None — paying the
            // full window for a command that was already complete.
            var parser = new VoxrCommandParser(
                Slots(
                    new VoxrSlotDefinition("track", new[] { "alpha", "bravo" }),
                    new VoxrSlotDefinition("burn_level", new[] { "maximum burn" })
                ),
                Commands(
                    Cmd(
                        "intercept",
                        P("intercept", "{track}"),
                        P("intercept", "{track}", "{burn_level}")
                    )
                )
            );

            var results = parser.Parse("intercept alpha maximum burn");
            Assert.AreEqual(1, results.Length);
            Assert.AreEqual(1, results[0].Command.MatchedPatternIndex);

            Assert.IsTrue(
                parser.CanCommitEarly(0, 1),
                "the tailed pattern is terminal — nothing can extend it"
            );
            Assert.AreEqual(
                EagerCommitVerdict.Commit,
                parser.TryEagerCommit(Tok("intercept alpha maximum burn"), null, 0.6f, 0.4f),
                "the eager scan must select the same tailed pattern ParseInternal fired"
            );
        }

        [Test]
        public void TryEagerCommit_SpanTieCanYieldHoldExtendable()
        {
            // The span tie-break also reaches a verdict the Commit case never does. Here the
            // newly-preferred pattern spans the buffer but is itself a prefix of a longer
            // sibling, so the buffer holds as extendable where it previously reported None
            // and waited out the full window. That verdict is load-bearing: it arms the
            // shortened prefix hold (issue #32), so the wait drops to prefixHoldSeconds.
            var parser = new VoxrCommandParser(
                Slots(
                    new VoxrSlotDefinition("track", new[] { "alpha" }),
                    new VoxrSlotDefinition("burn_level", new[] { "maximum burn" })
                ),
                Commands(
                    Cmd(
                        "intercept",
                        P("intercept", "{track}"),
                        P("intercept", "{track}", "{burn_level}"),
                        P("intercept", "{track}", "{burn_level}", "now")
                    )
                )
            );

            Assert.AreEqual(
                EagerCommitVerdict.HoldExtendable,
                parser.TryEagerCommit(Tok("intercept alpha maximum burn"), null, 0.6f, 0.4f),
                "pattern 1 spans the buffer but is a prefix of pattern 2"
            );
        }

        [Test]
        public void TryEagerCommit_TrailingUnkStillHoldsTheFullWindow()
        {
            // The whole-buffer gate treats anything left over — including trailing [unk] — as
            // an in-progress tail. Tie-breaking on a raw end index would smuggle that [unk]
            // into the winner's span and let the gate pass; the span is measured over tokens
            // actually matched, so this stays None.
            var parser = new VoxrCommandParser(
                Slots(new VoxrSlotDefinition("mode", new[] { "silent" })),
                Commands(Cmd("fire", P("fire"), P("fire", "{?mode}")))
            );

            Assert.AreEqual(
                EagerCommitVerdict.None,
                parser.TryEagerCommit(Tok("fire [unk]"), null, 0.6f, 0.4f),
                "an unrecognised trailing word means more speech may still be coming"
            );
        }

        // ---------- Leading [unk] does not block the gate (issue #43) ----------

        [Test]
        public void TryEagerCommit_LeadingUnk_StillCommits()
        {
            // An out-of-grammar station prefix ("Helm, fire missiles") pushes the match start
            // past 0. Nothing arriving later can extend the utterance leftward, so the leading
            // [unk] run is skipped and the command commits instead of paying the full window.
            var parser = new VoxrCommandParser(
                Slots(new VoxrSlotDefinition("weapon", new[] { "missiles" })),
                Commands(Cmd("fire", P("fire", "{weapon}")))
            );

            var results = parser.Parse("[unk] fire missiles");
            Assert.AreEqual(1, results.Length);
            Assert.AreEqual("fire", results[0].Command.Intent);

            Assert.AreEqual(
                EagerCommitVerdict.Commit,
                parser.TryEagerCommit(Tok("[unk] fire missiles"), null, 0.6f, 0.4f),
                "the eager verdict must name the command the subsequent flush fires"
            );
            Assert.AreEqual(
                EagerCommitVerdict.Commit,
                parser.TryEagerCommit(Tok("[unk] [unk] fire missiles"), null, 0.6f, 0.4f),
                "a run of leading [unk] is skipped, not just a single token"
            );
        }

        [Test]
        public void TryEagerCommit_LeadingUnkPrefixCommand_ReturnsHoldExtendable()
        {
            // Previously None — the full window. Now the prefix reaches its real verdict, so
            // the addressed form arms the shortened prefix hold (issue #32) like the bare one.
            var parser = new VoxrCommandParser(
                Slots(new VoxrSlotDefinition("target", new[] { "hotel one" })),
                Commands(
                    Cmd("status", P("status")),
                    Cmd("status_report", P("status", "report", "{target}"))
                )
            );

            Assert.AreEqual(
                EagerCommitVerdict.HoldExtendable,
                parser.TryEagerCommit(Tok("[unk] status"), null, 0.6f, 0.4f)
            );
        }

        [Test]
        public void TryEagerCommit_LeadingUnkWithLeftoverTail_StillReturnsNone()
        {
            // Only the leading run is forgiven. A tail — recognised or [unk] — is still an
            // in-progress utterance that more speech could complete.
            var parser = new VoxrCommandParser(
                Slots(),
                Commands(Cmd("cease_fire", P("cease", "fire")))
            );

            Assert.AreEqual(
                EagerCommitVerdict.None,
                parser.TryEagerCommit(Tok("[unk] cease fire now"), null, 0.6f, 0.4f)
            );
            Assert.AreEqual(
                EagerCommitVerdict.None,
                parser.TryEagerCommit(Tok("[unk] cease fire [unk]"), null, 0.6f, 0.4f)
            );
        }

        [Test]
        public void TryEagerCommit_LeadingRecognisedLeftover_StillReturnsNone()
        {
            // The trim is specific to [unk]. A leading word VOSK did resolve is skipped speech
            // the match failed to cover, which the gate must keep refusing.
            var parser = new VoxrCommandParser(
                Slots(),
                Commands(
                    Cmd("cease_fire", P("cease", "fire")),
                    Cmd("greet", P("hello"), P("hello", "there"))
                )
            );

            Assert.AreEqual(
                EagerCommitVerdict.None,
                parser.TryEagerCommit(Tok("hello cease fire"), null, 0.6f, 0.4f)
            );
        }

        [Test]
        public void TryEagerCommit_AllUnkTokens_ReturnsNone()
        {
            var parser = new VoxrCommandParser(
                Slots(),
                Commands(Cmd("cease_fire", P("cease", "fire")))
            );

            Assert.AreEqual(
                EagerCommitVerdict.None,
                parser.TryEagerCommit(Tok("[unk] [unk]"), null, 0.6f, 0.4f),
                "a buffer of pure filler matches nothing and must not commit"
            );
        }

        // ---------- Unfilled required slot (issue #66) ----------

        static VoxrSlotDefinition[] LaunchSlots() =>
            Slots(
                new VoxrSlotDefinition("quantity", new[] { "all", "one", "two", "three" }),
                new VoxrSlotDefinition("weapon", new[] { "missiles", "torpedoes" }),
                new VoxrSlotDefinition("target", new[] { "hotel one", "alpha three" })
            );

        // The shipped demo pattern, whose five elements are what put the missed-slot score on
        // exactly the default minScore instead of safely below it.
        static VoxrCommandDefinition LaunchCommand() =>
            Cmd("launch_weapon", P("launch", "{?quantity}", "{weapon}", "target", "{target}"));

        static VoxrCommandParser LaunchParser() =>
            new VoxrCommandParser(LaunchSlots(), Commands(LaunchCommand()));

        [Test]
        public void TryEagerCommit_UnfilledRequiredSlot_ReturnsNone()
        {
            // Four of five elements match; {target} matches nothing. Score is
            // (1 + 1 + 1 + 1 - 1) / 5 = exactly 0.60, so minScore does NOT catch it — and a
            // missed slot consumes no tokens, so EndIdx still reaches the end of the buffer
            // and the whole-buffer condition does not catch it either. Only the completeness
            // condition stands between this and a launch_weapon fired with no target, one
            // word before "hotel one" arrives.
            Assert.AreEqual(
                EagerCommitVerdict.None,
                LaunchParser().TryEagerCommit(Tok("launch all missiles target"), null, 0.6f, 0.4f),
                "a command missing a required argument must never commit early"
            );
        }

        [Test]
        public void TryEagerCommit_RequiredSlotFilled_StillCommits()
        {
            // Guards against over-correcting: the same pattern with the slot filled is a
            // complete, terminal match and must still commit.
            Assert.AreEqual(
                EagerCommitVerdict.Commit,
                LaunchParser()
                    .TryEagerCommit(Tok("launch all missiles target hotel one"), null, 0.6f, 0.4f)
            );
        }

        [Test]
        public void TryEagerCommit_UnfilledOptionalSlot_StillCommits()
        {
            // {?quantity} is optional, so omitting it is not a missed required slot.
            Assert.AreEqual(
                EagerCommitVerdict.Commit,
                LaunchParser()
                    .TryEagerCommit(Tok("launch missiles target hotel one"), null, 0.6f, 0.4f)
            );
        }

        [Test]
        public void TryEagerCommit_MissedRequiredLiteral_AllSlotsFilled_StillCommits()
        {
            // The "target" literal is dropped but every slot is filled, so the command is
            // fully determined — score (1 + 1 + 1 - 0.5 + 1) / 5 = 0.70. Neither completeness
            // condition may catch this: the slot condition is scoped to slots, and the miss
            // is MEDIAL, so {target} matches afterwards and the tail condition (issue #70)
            // clears too.
            Assert.AreEqual(
                EagerCommitVerdict.Commit,
                LaunchParser()
                    .TryEagerCommit(Tok("launch all missiles hotel one"), null, 0.6f, 0.4f),
                "a dropped function word does not make the command incomplete"
            );
        }

        [Test]
        public void TryEagerCommit_UnfilledRequiredSlot_UnanalysableGrammar_StillReturnsNone()
        {
            // The completeness condition has to sit ABOVE the issue #44 degrade, not in its
            // shadow. That degrade returns HoldExtendable for any otherwise-complete match in
            // a grammar too complex to analyse, so a check running later would hand an
            // incomplete command a verdict above None — arming the shortened prefixHoldSeconds
            // hold on exactly the buffer whose missing words still need the full window to
            // arrive in. Nothing else in the suite reaches the degrade with a missed slot:
            // both other un-analysable cases use a slot-free grammar.
            LogAssert.Expect(LogType.Warning, new Regex("more than the 12"));

            var parser = new VoxrCommandParser(
                LaunchSlots(),
                Commands(OverLimitCommand(), LaunchCommand())
            );

            Assert.AreEqual(
                EagerCommitVerdict.None,
                parser.TryEagerCommit(Tok("launch all missiles target"), null, 0.6f, 0.4f),
                "an incomplete command must not reach the degrade's hold either"
            );
            LogAssert.NoUnexpectedReceived();
        }

        // ---------- Unmatched required tail (issue #70) ----------

        // Two mode commands sharing a two-word prefix and diverging only on the last word —
        // the shipped demo grammar's shape (Tests~/Runtime/DemoGrammar.cs).
        static VoxrCommandParser ModeSwitchParser() =>
            new VoxrCommandParser(
                Slots(),
                Commands(
                    Cmd("mode_weapons", P("switch", "to", "weapons")),
                    Cmd("mode_navigation", P("switch", "to", "navigation"))
                )
            );

        [Test]
        public void TryEagerCommit_UnmatchedTerminalLiteral_ReturnsNone()
        {
            // "switch to" is two words into "switch to navigation". The trailing literal
            // matched nothing, and a miss consumes no token — so EndIdx still reaches the end
            // of the buffer and the whole-buffer condition cannot catch it, exactly as a
            // missed slot evades it (issue #66). Both patterns score (1 + 1 - 0.5) / 3 = 0.50
            // and tie, so registration order would decide: the speaker says "navigation" and
            // mode_weapons fires. The wrong command, not merely an early one.
            //
            // minScore is lowered to 0.4 deliberately. At the 0.6 default this particular
            // buffer is caught by the score gate today, which would make the test green
            // without the fix; below the gate only the tail condition stands in the way.
            Assert.AreEqual(
                EagerCommitVerdict.None,
                ModeSwitchParser().TryEagerCommit(Tok("switch to"), null, 0.4f, 0.4f),
                "a pattern still owing its last word must not commit early"
            );
        }

        [Test]
        public void TryEagerCommit_UnmatchedTerminalLiteral_LiveAtDefaultThreshold()
        {
            // The same hole at the DEFAULT minScore, so this is not a low-threshold curiosity.
            // Four required literals with the last unspoken score (1 + 1 + 1 - 0.5) / 4 =
            // 0.625, over the 0.6 default, with every other eager condition satisfied.
            var parser = new VoxrCommandParser(
                Slots(),
                Commands(
                    Cmd("autopilot_on", P("set", "auto", "pilot", "on")),
                    Cmd("autopilot_off", P("set", "auto", "pilot", "off"))
                )
            );

            Assert.AreEqual(
                EagerCommitVerdict.None,
                parser.TryEagerCommit(Tok("set auto pilot"), null, 0.6f, 0.4f),
                "0.625 clears minScore, so only the tail condition can refuse this"
            );
        }

        [Test]
        public void TryEagerCommit_CompletedTerminalLiteral_StillCommits()
        {
            // Guards against over-correcting: once the last word arrives the pattern owes
            // nothing and must commit exactly as before.
            Assert.AreEqual(
                EagerCommitVerdict.Commit,
                ModeSwitchParser().TryEagerCommit(Tok("switch to navigation"), null, 0.4f, 0.4f)
            );
        }

        [Test]
        public void TryEagerCommit_OmittedTrailingOptional_IsNotAnUnmatchedTail()
        {
            // An omitted trailing OPTIONAL is not an unmatched required tail: it leaves both
            // sides of the ratio, so the match is a perfect 1.0 and the pattern owes nothing.
            // The verdict must therefore be one ABOVE None — the tail condition must not fire.
            //
            // It is HoldExtendable rather than Commit for an unrelated, pre-existing reason:
            // IsTerminalPattern treats any pattern whose last element is optional as
            // non-terminal, since later speech could still fill it, which
            // TrailingOptional_IsNotCommittable above pins independently. That rule is older
            // than this condition and untouched by it — what matters here is that the verdict
            // did not collapse to None.
            var parser = new VoxrCommandParser(Slots(), Commands(Cmd("go", P("go", "?now"))));

            Assert.AreEqual(
                EagerCommitVerdict.HoldExtendable,
                parser.TryEagerCommit(Tok("go"), null, 0.6f, 0.4f),
                "optionality is not incompleteness — the tail condition must not refuse this"
            );
        }

        [Test]
        public void TryEagerCommit_OmittedMedialOptional_StillCommits()
        {
            // The counter behind the tail condition must not treat an omitted optional as a
            // miss. "turn light on" omits "?the" and then matches two more required literals,
            // so the pattern ends on the buffer's last token owing nothing — a perfect 1.0,
            // terminal, and committable exactly as before.
            var parser = new VoxrCommandParser(
                Slots(),
                Commands(Cmd("light_on", P("turn", "?the", "light", "on")))
            );

            Assert.AreEqual(
                EagerCommitVerdict.Commit,
                parser.TryEagerCommit(Tok("turn light on"), null, 0.6f, 0.4f),
                "an omitted optional mid-pattern leaves no unmatched tail behind it"
            );
        }

        [Test]
        public void TryEagerCommit_UnmatchedTerminalLiteral_UnanalysableGrammar_StillReturnsNone()
        {
            // Same ordering requirement the slot condition has (issue #66): the tail condition
            // must sit ABOVE the issue #44 degrade, or an un-analysable grammar would hand a
            // still-incomplete match HoldExtendable and arm the shortened hold on precisely
            // the buffer whose missing word needs the full window to arrive in.
            LogAssert.Expect(LogType.Warning, new Regex("more than the 12"));

            var parser = new VoxrCommandParser(
                Slots(),
                Commands(OverLimitCommand(), Cmd("autopilot_on", P("set", "auto", "pilot", "on")))
            );

            Assert.AreEqual(
                EagerCommitVerdict.None,
                parser.TryEagerCommit(Tok("set auto pilot"), null, 0.6f, 0.4f),
                "an incomplete command must not reach the degrade's hold either"
            );
            LogAssert.NoUnexpectedReceived();
        }
    }
}
