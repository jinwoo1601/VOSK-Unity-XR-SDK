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
    // complete-but-extendable verdict that drives the shortened prefix hold (issue #32).
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

        // ---------- Optional-expansion guard (issue #25, review fix #2) ----------

        [Test]
        public void ManyOptionalElements_DisablesEagerCommitForWholeParser()
        {
            // ExpandOptionals enumerates 2^optionals concrete forms; past MaxOptionalExpansion
            // (12) the parser refuses to analyse and disables eager commit for the whole command
            // set rather than overflow or partially (and unsoundly) analyse a single pattern.
            var noisy = Cmd("noisy", P("noisy",
                "?a", "?b", "?c", "?d", "?e", "?f", "?g", "?h", "?i", "?j", "?k", "?l", "?m"));
            var parser = new VoxrCommandParser(
                Slots(),
                Commands(noisy, Cmd("cease_fire", P("cease", "fire"))));

            LogAssert.Expect(LogType.Warning, new Regex("more than 12 optional"));

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
        public void TryEagerCommit_UnanalysableGrammar_ReturnsNone()
        {
            // Past MaxOptionalExpansion the eager precompute is abandoned for the whole
            // parser. HoldExtendable is a product of that analysis, so with no analysis the
            // verdict must be None — the full window stays in force.
            var noisy = Cmd("noisy", P("noisy",
                "?a", "?b", "?c", "?d", "?e", "?f", "?g", "?h", "?i", "?j", "?k", "?l", "?m"));
            var parser = new VoxrCommandParser(
                Slots(),
                Commands(noisy, Cmd("cease_fire", P("cease", "fire"))));

            LogAssert.Expect(LogType.Warning, new Regex("more than 12 optional"));

            Assert.AreEqual(EagerCommitVerdict.None,
                parser.TryEagerCommit(Tok("cease fire"), null, 0.6f, 0.4f));
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
    }
}
