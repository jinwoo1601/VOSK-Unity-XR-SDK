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
    }
}
