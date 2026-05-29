using System.Collections.Generic;
using NUnit.Framework;
using VoXR.Commands;

namespace VoXR.Tests.Runtime
{
    // Unit tests for the eager-flush precompute (CanCommitEarly) and the speculative
    // TryEagerCommit gate on VoxrCommandParser (issue #25).
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
        public void TryEagerCommit_FullTerminalMatch_ReturnsTrue()
        {
            var parser = new VoxrCommandParser(
                Slots(new VoxrSlotDefinition("weapon", new[] { "missiles" })),
                Commands(Cmd("fire", P("fire", "{weapon}"))));

            Assert.IsTrue(parser.TryEagerCommit(Tok("fire missiles"), null, 0.6f, 0.4f));
        }

        [Test]
        public void TryEagerCommit_BelowScoreThreshold_ReturnsFalse()
        {
            var parser = new VoxrCommandParser(
                Slots(
                    new VoxrSlotDefinition("weapon", new[] { "missiles" }),
                    new VoxrSlotDefinition("target", new[] { "hotel one" })),
                Commands(Cmd("launch", P("launch", "{weapon}", "target", "{target}"))));

            // {target} unfilled -> score 0.5 < 0.6 -> not eligible.
            Assert.IsFalse(parser.TryEagerCommit(Tok("launch missiles target"), null, 0.6f, 0.4f));
        }

        [Test]
        public void TryEagerCommit_LeftoverTrailingTokens_ReturnsFalse()
        {
            var parser = new VoxrCommandParser(
                Slots(),
                Commands(Cmd("cease_fire", P("cease", "fire"))));

            // "cease fire" matches fully and is committable, but "now" is unconsumed,
            // so the match does not span the whole buffer.
            Assert.IsFalse(parser.TryEagerCommit(Tok("cease fire now"), null, 0.6f, 0.4f));
        }

        [Test]
        public void TryEagerCommit_PrefixCommand_ReturnsFalse()
        {
            var parser = new VoxrCommandParser(
                Slots(new VoxrSlotDefinition("target", new[] { "hotel one" })),
                Commands(
                    Cmd("status", P("status")),
                    Cmd("status_report", P("status", "report", "{target}"))));

            Assert.IsFalse(parser.TryEagerCommit(Tok("status"), null, 0.6f, 0.4f),
                "a prefix command must not eager-commit even though it matches fully");
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
            Assert.IsFalse(parser.TryEagerCommit(Tok("hello"), null, 0.6f, 0.4f),
                "eager verdict must match the non-committable selection ParseInternal made");
        }
    }
}
