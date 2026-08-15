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

        // ---------- Coverage and the eager gate (issue #65 §5.2) ----------
        //
        // Design §5.4 claimed the trailing coverage term "cannot destabilise the eager gate,
        // structurally", on the ground that anything reaching a verdict already spans the
        // buffer and so has no trailing orphans. The premise is right and the conclusion is
        // wrong: the term does not change the WINNER'S score, but it changes which candidate
        // IS the winner, and the completeness gates are applied to whoever that is.
        //
        // The movement is one-way, and the reason is worth stating because it is what makes
        // the change safe rather than merely small. A candidate that clears the gates starts
        // at the first recognised token (so no leading skips) and ends at the buffer end (so
        // its orphan run is empty, the ConsumedEndIdx..EndIdx gap being all-[unk] and [unk]
        // free). Its score is therefore IDENTICAL before and after. Every other candidate's
        // score can only fall. So a verdict above None can never be withdrawn and a committed
        // command can never change identity — the only available move is None -> Commit or
        // None -> HoldExtendable, when the bare sibling that used to outrank the real command
        // is demoted out of the way.

        [Test]
        public void TryEagerCommit_MedialDropWithEverySlotFilled_NowCommits()
        {
            // Design §5.4's counter-example. "at" is dropped, so before coverage the bare
            // "fire" won at 1.0, spanned only one token of three, and the gate refused —
            // correctly, but for the wrong command. Now bare "fire" is charged for the two
            // tokens it cannot explain (1/(1+2) = 0.333), "fire at {target}" wins on 2/3, and
            // it clears every gate in turn: no required slot missed, no required element
            // after the last match, starts at token 0, ends at the buffer end.
            //
            // The new verdict is the one DR-6 already blesses as safe — a medial drop with
            // every argument present — so the gate now fires the right command early instead
            // of waiting out the full buffer window to fire the wrong one.
            var parser = new VoxrCommandParser(
                Slots(new VoxrSlotDefinition("target", new[] { "hotel one" })),
                Commands(Cmd("fire", P("fire")), Cmd("fire_at", P("fire", "at", "{target}")))
            );

            Assert.AreEqual(
                EagerCommitVerdict.Commit,
                parser.TryEagerCommit(Tok("fire hotel one"), null, 0.6f, 0.4f)
            );
        }

        [Test]
        public void TryEagerCommit_VerdictNamesTheCommandTheFlushFires()
        {
            // The invariant the whole eager path rests on, checked on the utterance that
            // newly moves. Both paths call the same scorer over the same token array, so they
            // cannot disagree — but that is true by construction only while a single method
            // computes the score, which is exactly why coverage went into TryMatchScored
            // rather than into each caller.
            var parser = new VoxrCommandParser(
                Slots(new VoxrSlotDefinition("target", new[] { "hotel one" })),
                Commands(Cmd("fire", P("fire")), Cmd("fire_at", P("fire", "at", "{target}")))
            );

            var verdict = parser.TryEagerCommit(Tok("fire hotel one"), null, 0.6f, 0.4f);
            var flushed = parser.Parse("fire hotel one");

            Assert.AreEqual(EagerCommitVerdict.Commit, verdict);
            Assert.AreEqual(1, flushed.Length);
            Assert.AreEqual(
                "fire_at",
                flushed[0].Command.Intent,
                "the flush must fire the command the verdict was computed on"
            );
            Assert.AreEqual("hotel one", flushed[0].Command.GetSlot("target"));
            Assert.AreEqual(2f / 3f, flushed[0].Command.Score, 0.001f);
            Assert.Greater(
                flushed[0].Command.Score,
                0.6f,
                "and it must clear the same threshold the gate applied"
            );
        }

        [Test]
        public void TryEagerCommit_TrailingUnkGap_CostsTheCandidateNothing()
        {
            // The gate's end-of-buffer condition is over EndIdx while orphans count from
            // ConsumedEndIdx, so "spans the buffer" does not by itself mean "explains the
            // buffer". What closes that gap is that the region between the two indices is
            // only ever reached by the [unk] skip loop, and [unk] is never charged.
            //
            // "cease fire [unk]" leaves ConsumedEndIdx at 2 and EndIdx at 3 — the skip runs
            // before the trailing optional, which then matches nothing. The candidate must
            // still score a full 1.0 and reach the same verdict as the gapless buffer.
            var parser = new VoxrCommandParser(
                Slots(new VoxrSlotDefinition("mode", new[] { "silent" })),
                Commands(Cmd("cease_fire", P("cease", "fire", "{?mode}")))
            );

            // Both verdicts are named outright rather than compared to each other: an equality
            // would hold just as well if both regressed to None, which is one of the failures
            // this is meant to catch. HoldExtendable rather than Commit because the trailing
            // optional means more speech could still extend the match.
            Assert.AreEqual(
                EagerCommitVerdict.HoldExtendable,
                parser.TryEagerCommit(Tok("cease fire"), null, 0.6f, 0.4f),
                "the gapless buffer holds"
            );
            Assert.AreEqual(
                EagerCommitVerdict.HoldExtendable,
                parser.TryEagerCommit(Tok("cease fire [unk]"), null, 0.6f, 0.4f),
                "a trailing [unk] the pattern never consumed must not change the verdict"
            );
            Assert.AreEqual(1.0f, parser.Parse("cease fire [unk]")[0].Command.Score, 0.001f);
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
            // fully determined — score (1 + 1 + 1 + 0 + 1) / 5 = 0.80. Neither completeness
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
        public void TryEagerCommit_MedialUnfilledRequiredSlot_ReturnsNone()
        {
            // Both refusal pins above miss a TERMINAL slot, which the issue #70 tail condition
            // also refuses — so neither of them can tell whether this condition still works.
            // Here {weapon} misses and then "target", {target} and "now" all match, resetting
            // the tail counter to 0, so the tail condition clears and only this one is left.
            // Score (1 - 1 + 1 + 1 + 1) / 5 = exactly 0.60 clears the default gate, and EndIdx
            // reaches the buffer end, so nothing else refuses it either.
            var parser = new VoxrCommandParser(
                LaunchSlots(),
                Commands(Cmd("launch_weapon", P("launch", "{weapon}", "target", "{target}", "now")))
            );

            Assert.AreEqual(
                EagerCommitVerdict.None,
                parser.TryEagerCommit(Tok("launch target hotel one now"), null, 0.6f, 0.4f),
                "a MEDIAL missed required slot must still refuse — the tail condition cannot see it"
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
            // missed slot evades it (issue #66). Both patterns score (1 + 1 + 0) / 3 = 0.667
            // and tie, so registration order would decide: the speaker says "navigation" and
            // mode_weapons fires. The wrong command, not merely an early one.
            //
            // minScore is lowered to 0.4 for a reason that has since expired. When this test
            // was written the buffer scored 0.50 and the 0.6 default caught it by arithmetic,
            // which would have made the test green without the fix; dropping below the gate
            // left only the tail condition in the way. Issue #65 §5.1 then raised it to 0.667
            // — over the default — so the tail condition is now load-bearing at any threshold
            // and the sibling test below pins exactly that. The 0.4 is kept because it still
            // isolates the condition under test rather than sharing the work with the gate.
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
            // Four required literals with the last unspoken score (1 + 1 + 1 + 0) / 4 = 0.75,
            // over the 0.6 default, with every other eager condition satisfied. This was
            // 0.625 before issue #65 §5.1 zeroed the miss penalty — already over the gate
            // then, which is why #70 was a live bug rather than a consequence of §5.1.
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
                "0.75 clears minScore, so only the tail condition can refuse this"
            );
        }

        [Test]
        public void TryEagerCommit_UnmatchedTerminalLiteral_TrailingUnk_ReturnsNone()
        {
            // Trailing filler makes the whole-buffer condition pass even harder: the [unk] skip
            // runs before EVERY element, including the one that then matches nothing, so EndIdx
            // reaches 4 == tokens.Length while ConsumedEndIdx stays at 3. The score is unchanged
            // at 0.75, so neither the score gate nor the whole-buffer check can refuse this —
            // only the tail condition can. This is the shape a final result carrying breath or
            // filler presents while the last word is still to come.
            var parser = new VoxrCommandParser(
                Slots(),
                Commands(
                    Cmd("autopilot_on", P("set", "auto", "pilot", "on")),
                    Cmd("autopilot_off", P("set", "auto", "pilot", "off"))
                )
            );

            Assert.AreEqual(
                EagerCommitVerdict.None,
                parser.TryEagerCommit(Tok("set auto pilot [unk]"), null, 0.6f, 0.4f),
                "trailing filler must not carry a pattern still owing its last word past the gate"
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

        // ---------- Required-literal miss cost (issue #65 §5.1) ----------
        //
        // Zeroing RequiredLiteralMissPenalty raises the scores the eager scan selects on, so
        // more buffers clear its gate. That is intended (F9), but it puts weight on the two
        // completeness conditions that the score arithmetic used to carry by coincidence.
        // These two tests bound it from both sides: one buffer that SHOULD newly commit, and
        // one that must not, at exactly the score the design predicted it would rise to.

        [Test]
        public void TryEagerCommit_MedialMissNewlyClearingTheGate_CommitsAndAgreesWithParse()
        {
            // The case §5.1 exists for, at the eager gate. "time to target" heard as "time
            // target" rises from (1 - 0.5 + 1) / 3 = 0.50 to (1 + 0 + 1) / 3 = 0.667 and now
            // clears the default minScore.
            //
            // The miss is MEDIAL by construction, which is what makes committing correct here:
            // "target" matches afterwards, so the tail counter resets and the pattern ends on
            // the buffer's last token owing nothing. A TRAILING drop at the same score is the
            // issue #70 case and is refused above — that one would prove nothing about this
            // change, since the tail condition catches it whatever the score.
            //
            // F9's invariant is the second half: a verdict must name the command the flush
            // will actually fire, so the same buffer is put through Parse as well.
            var parser = new VoxrCommandParser(
                Slots(),
                Commands(Cmd("time_to_target", P("time", "to", "target")))
            );

            Assert.AreEqual(
                EagerCommitVerdict.Commit,
                parser.TryEagerCommit(Tok("time target"), null, 0.6f, 0.4f),
                "a medial drop leaves nothing owing, so the raised score should commit"
            );

            var flushed = parser.Parse("time target");
            Assert.AreEqual(
                1,
                flushed.Length,
                "the verdict must name exactly what the flush fires"
            );
            Assert.AreEqual("time_to_target", flushed[0].Command.Intent);
            Assert.AreEqual(2f / 3f, flushed[0].Command.Score, 0.001f);
        }

        [Test]
        public void TryEagerCommit_AdmissionRefusesASparseCandidate_ReturnsNone()
        {
            // DR-7 is a refusal reason at this gate too — TryEagerCommit and ParseInternal
            // share IsBetterCandidate, so the admission rule applies before any of the
            // conditions this method documents. Nothing else covers that inheritance.
            //
            // "launch mark" against a five-literal pattern matches 2 and misses 3, so DR-7
            // refuses it. Every other condition would have let it through: both misses are
            // medial ("mark" matches last, so no unmatched tail), there are no slots to miss,
            // and the match reaches the end of the buffer. minScore is lowered to 0.4 because
            // the score is exactly (1 + 0 + 0 + 0 + 1) / 5 = 0.40 — at the default the score
            // gate would refuse it and the test would pass without the rule.
            var parser = new VoxrCommandParser(
                Slots(),
                Commands(Cmd("launch_weapon", P("launch", "missiles", "target", "hotel", "mark")))
            );

            Assert.AreEqual(
                EagerCommitVerdict.None,
                parser.TryEagerCommit(Tok("launch mark"), null, 0.4f, 0.4f),
                "a candidate too sparse to be admitted must not commit early either"
            );
        }

        [Test]
        public void TryEagerCommit_WidenedSlotMissHole_StaysClosed()
        {
            // F10, and the reason issue #66 was a hard prerequisite of this change. §5.4
            // computed the exact shape that §5.1 newly lifts over the gate: eight elements,
            // one missed required SLOT alongside one dropped required literal, scoring
            // (1 - 1 + 1 + 0 + 1 + 1 + 1 + 1) / 8 = 0.625 where it scored 4.5 / 8 = 0.5625
            // before. The score gate used to refuse this on its own; now only the completeness
            // condition does.
            //
            // Both misses are medial and "mark" matches on the last token, so the issue #70
            // tail condition clears and cannot be what refuses this — the slot condition is
            // load-bearing here and nothing else is. The Parse assertion pins the raised score
            // so the refusal is demonstrably happening ABOVE the gate rather than because the
            // candidate never reached it.
            var parser = new VoxrCommandParser(
                LaunchSlots(),
                Commands(
                    Cmd(
                        "launch_weapon",
                        P(
                            "launch",
                            "{quantity}",
                            "{weapon}",
                            "target",
                            "{target}",
                            "on",
                            "my",
                            "mark"
                        )
                    )
                )
            );

            var parsed = parser.Parse("launch missiles hotel one on my mark");
            Assert.AreEqual(1, parsed.Length);
            Assert.AreEqual(
                5f / 8f,
                parsed[0].Command.Score,
                0.001f,
                "the widened hole is real — this candidate now clears the 0.6 default"
            );
            Assert.IsFalse(
                parsed[0].Command.HasSlot("quantity"),
                "the required quantity slot is what went missing"
            );

            Assert.AreEqual(
                EagerCommitVerdict.None,
                parser.TryEagerCommit(
                    Tok("launch missiles hotel one on my mark"),
                    null,
                    0.6f,
                    0.4f
                ),
                "a command missing a required argument must never commit early, however it scores"
            );
        }

        [Test]
        public void TrailingCoverage_ChangesTheVerdict_ByDecidingWhichCandidateIsChecked()
        {
            // Documentation~/scoring.md §6 condition 3 used to claim the trailing term "can
            // never be what decides an eager verdict", reasoning that a candidate passing the
            // whole-buffer check has nothing trailing it. True of that candidate's own charge,
            // but it misses that coverage runs in SELECTION — and selection picks which
            // candidate the conditions are then applied to. Corrected under issue #83, and
            // pinned here because the claim is not derivable from the conditions alone.
            var slots = Slots(new VoxrSlotDefinition("burn_level", new[] { "hard burn" }));
            var commands = Commands(
                Cmd("decelerate", P("decelerate"), P("decelerate", "by", "{burn_level}"))
            );

            LogAssert.Expect(LogType.Warning, new Regex("required literal \"by\""));
            var charged = new VoxrCommandParser(slots, commands, 1.0f);

            Assert.AreEqual(
                EagerCommitVerdict.Commit,
                charged.TryEagerCommit(Tok("decelerate hard burn"), null, 0.6f, 0.4f),
                "coverage demotes the bare form, so the buffer-spanning pattern is the one checked"
            );

            LogAssert.Expect(LogType.Warning, new Regex("required literal \"by\""));
            var uncharged = new VoxrCommandParser(slots, commands, 0f);

            Assert.AreEqual(
                EagerCommitVerdict.None,
                uncharged.TryEagerCommit(Tok("decelerate hard burn"), null, 0.6f, 0.4f),
                "at weight 0 the bare form wins selection and fails the whole-buffer condition"
            );
        }

        // ---------- Medial sibling discriminator (issue #74 design §2.8) ----------
        //
        // The sibling-tie design reasoned from source that a MEDIAL discriminator slips every
        // condition this file documents, and rests DR-5 — the eager gate refusing on a sibling
        // tie — entirely on that. It was never observed. These two tests are what backlog item
        // 1 owes the design (§7.5): together they either confirm the finding or refute it, and
        // a refutation reopens the design rather than being quietly dropped.
        //
        // Nothing above covers it. TryEagerCommit_UnmatchedTerminalLiteral_ReturnsNone is the
        // sibling shape but TRAILING, where the issue #70 tail condition refuses it.
        // TryEagerCommit_MedialMissNewlyClearingTheGate_CommitsAndAgreesWithParse is a medial
        // miss that commits, but on a SINGLE command with no rival — so it says nothing about
        // what happens when a second pattern ties it. This pair is the intersection.

        // Two intents differing only at a MEDIAL required literal, with a slot in front of it
        // so the discriminator cannot be the first divergence the matcher meets.
        static VoxrCommandParser MedialSiblingParser() =>
            new VoxrCommandParser(
                Slots(new VoxrSlotDefinition("ship", new[] { "alpha" })),
                Commands(
                    Cmd("set_mode", P("set", "{ship}", "mode", "on")),
                    Cmd("set_level", P("set", "{ship}", "level", "on"))
                )
            );

        [Test]
        public void TryEagerCommit_MedialSiblingDiscriminator_CommitsOnAnUndecidableBuffer()
        {
            // "set alpha on" — the discriminating word elided. Walking the conditions in the
            // order TryEagerCommit applies them:
            //
            //   minScore                     (1 + 1 + 0 + 1) / 4 = 0.75, clears the default
            //   bestMissedRequiredSlot       false — {ship} is filled; the miss is a LITERAL
            //   bestHasUnmatchedRequiredTail false — the miss is MEDIAL, so "on" matches after
            //                                it and resets the counter (issue #70 by design)
            //   whole-buffer                 satisfied — the match spans "set alpha on" exactly
            //   confidence                   null confidence bypasses the gate
            //   CanCommitEarly               both patterns are terminal on "on" and neither is
            //                                a prefix of the other, so the precompute allows it
            //
            // Every condition is satisfied by BOTH siblings identically, at the same score,
            // over the same span, with the same matched-literal count. The gate therefore
            // commits on evidence that cannot distinguish which command the speaker meant.
            //
            // Note the score is 0.75, not the 0.8 the design's §2.8 predicted — that constant
            // belongs to the five-element analog above (MissedRequiredLiteral_AllSlotsFilled).
            // Both clear the 0.6 default, so the condition outcome §2.8 reasoned to is
            // unaffected; only the illustrative arithmetic was carried from the wrong example.
            // This grammar is the sibling shape by construction, so it now warns at
            // construction too (issue #74 backlog item 1). Declared rather than tolerated,
            // matching every other warning-producing test in this file.
            LogAssert.Expect(LogType.Warning, new Regex("differ only at element 3"));

            Assert.AreEqual(
                EagerCommitVerdict.Commit,
                MedialSiblingParser().TryEagerCommit(Tok("set alpha on"), null, 0.6f, 0.4f),
                "design §2.8: a medial discriminator slips every condition and commits early"
            );
        }

        [Test]
        public void Parse_MedialSiblingDiscriminator_FiresByRegistrationOrderAlone()
        {
            // The other half of §2.8. The verdict above names no command, so it cannot show
            // that the WRONG sibling fires — that happens in the flush the verdict authorises,
            // where both siblings tie on every selection key and the comparison falls through
            // to registration order.
            //
            // The medial analog of MissedLiteral_DroppedDiscriminator_FiresTheFirstRegistered
            // Sibling (VoxrCommandParserTests.cs), which pins the same fall-through for a
            // TRAILING discriminator. Worth pinning separately because the trailing case never
            // reaches the eager gate — issue #70 refuses it there — while this one does.
            //
            // Reversing the declaration order is what makes this a coin flip rather than a
            // defensible preference: nothing about the utterance changed, only the order the
            // author happened to register two commands in.
            // Two constructions below, each emitting the construction-time sibling warning.
            LogAssert.Expect(LogType.Warning, new Regex("differ only at element 3"));
            var flushed = MedialSiblingParser().Parse("set alpha on");

            Assert.AreEqual(1, flushed.Length, "the tie resolves to exactly one command");
            Assert.AreEqual("set_mode", flushed[0].Command.Intent, "the first-registered wins");
            Assert.AreEqual(3f / 4f, flushed[0].Command.Score, 0.001f);

            LogAssert.Expect(LogType.Warning, new Regex("differ only at element 3"));
            var reversed = new VoxrCommandParser(
                Slots(new VoxrSlotDefinition("ship", new[] { "alpha" })),
                Commands(
                    Cmd("set_level", P("set", "{ship}", "level", "on")),
                    Cmd("set_mode", P("set", "{ship}", "mode", "on"))
                )
            ).Parse("set alpha on");

            Assert.AreEqual(1, reversed.Length);
            Assert.AreEqual(
                "set_level",
                reversed[0].Command.Intent,
                "the same utterance fires the other intent purely because it was declared first"
            );
        }
    }
}
