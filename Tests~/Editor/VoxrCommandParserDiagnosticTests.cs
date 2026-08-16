using System;
using System.Collections.Generic;
using NUnit.Framework;
using VoXR;
using VoXR.Commands;

namespace VoXR.Tests.Editor
{
    public class VoxrCommandParserDiagnosticTests
    {
        static VoxrSlotDefinition[] MakeSlots() => new[]
        {
            new VoxrSlotDefinition("weapon", new[] { "missiles", "torpedoes" }),
            new VoxrSlotDefinition("target", new[] { "hotel one", "hotel two" }),
            new VoxrSlotDefinition("quantity", new[] { "all", "one", "two" }),
        };

        static VoxrCommandDefinition[] MakeCommands() => new[]
        {
            new VoxrCommandDefinition("launch_weapon", new[]
            {
                new[] { "launch", "{?quantity}", "{weapon}", "target", "{target}" },
                new[] { "shoot", "{weapon}" },
            }),
            new VoxrCommandDefinition("cease_fire", new[]
            {
                new[] { "cease", "fire" },
            }),
        };

        VoxrCommandParser CreateParser() =>
            new VoxrCommandParser(MakeSlots(), MakeCommands());

        // 3.1
        [Test]
        public void LastParseDiagnostics_EmptyOnNoInput()
        {
            var parser = CreateParser();
            parser.Parse("", null);

            Assert.IsNotNull(parser.LastParseDiagnostics);
            Assert.AreEqual(0, parser.LastParseDiagnostics.Length);
        }

        // 3.2
        [Test]
        public void LastParseDiagnostics_EmptyOnNoMatch()
        {
            var parser = CreateParser();
            parser.Parse("xyz abc", null);

            Assert.IsNotNull(parser.LastParseDiagnostics);
            Assert.AreEqual(0, parser.LastParseDiagnostics.Length);
        }

        // 3.3
        [Test]
        public void LastParseDiagnostics_OneEntryPerCommand()
        {
            var parser = CreateParser();
            parser.Parse("shoot missiles", null);

            Assert.AreEqual(1, parser.LastParseDiagnostics.Length);
        }

        // 3.4
        [Test]
        public void PatternString_RecordedCorrectly()
        {
            var parser = CreateParser();
            parser.Parse("shoot missiles", null);

            Assert.AreEqual("shoot {weapon}",
                parser.LastParseDiagnostics[0].PatternString);
        }

        // 3.5
        [Test]
        public void SlotStartWords_Recorded()
        {
            var parser = CreateParser();
            // "shoot missiles" → pattern "shoot {weapon}", slot at token 1
            parser.Parse("shoot missiles", null);

            var entry = parser.LastParseDiagnostics[0];
            Assert.IsNotNull(entry.SlotStartWords);
            Assert.AreEqual(1, entry.SlotStartWords.Length);
            Assert.AreEqual(1, entry.SlotStartWords[0]);
        }

        // 3.6
        [Test]
        public void SlotEndWords_Recorded_Exclusive()
        {
            var parser = CreateParser();
            // "shoot missiles" → {weapon} consumes 1 token starting at 1 → EndWord = 2
            parser.Parse("shoot missiles", null);

            var entry = parser.LastParseDiagnostics[0];
            Assert.IsNotNull(entry.SlotEndWords);
            Assert.AreEqual(1, entry.SlotEndWords.Length);
            Assert.AreEqual(2, entry.SlotEndWords[0]);
        }

        // 3.7
        [Test]
        public void MultiSlot_Positions()
        {
            var parser = CreateParser();
            // "launch all missiles target hotel one" →
            //   pattern "launch {?quantity} {weapon} target {target}"
            //   {quantity}=all at [1,2), {weapon}=missiles at [2,3), {target}=hotel one at [4,6)
            parser.Parse("launch all missiles target hotel one", null);

            var entry = parser.LastParseDiagnostics[0];
            Assert.AreEqual(3, entry.SlotStartWords.Length);
            Assert.AreEqual(3, entry.SlotEndWords.Length);

            // {quantity}
            Assert.AreEqual(1, entry.SlotStartWords[0]);
            Assert.AreEqual(2, entry.SlotEndWords[0]);
            // {weapon}
            Assert.AreEqual(2, entry.SlotStartWords[1]);
            Assert.AreEqual(3, entry.SlotEndWords[1]);
            // {target}
            Assert.AreEqual(4, entry.SlotStartWords[2]);
            Assert.AreEqual(6, entry.SlotEndWords[2]);
        }

        // 3.8
        [Test]
        public void MultipleCommandsExtracted_MultipleEntries()
        {
            var parser = CreateParser();
            // Sequential extraction: "cease fire" then "shoot missiles"
            parser.Parse("cease fire shoot missiles", null);

            Assert.AreEqual(2, parser.LastParseDiagnostics.Length);
            Assert.AreEqual("cease fire", parser.LastParseDiagnostics[0].PatternString);
            Assert.AreEqual("shoot {weapon}", parser.LastParseDiagnostics[1].PatternString);
        }

        // 3.9
        [Test]
        public void ComputeConfidence_InternalAccess()
        {
            var tokens = new[] { "cease", "fire" };
            var wordConf = new Dictionary<string, float>
            {
                { "cease", 0.9f },
                { "fire", 0.7f },
            };

            float conf = VoxrCommandParser.ComputeConfidence(tokens, 0, 2, wordConf);
            Assert.AreEqual(0.7f, conf, 1e-5f, "Should return min confidence across span");
        }

        [Test]
        public void ComputeConfidence_NoWordData_ReturnsNegativeOne()
        {
            var tokens = new[] { "cease", "fire" };
            float conf = VoxrCommandParser.ComputeConfidence(tokens, 0, 2, null);
            Assert.AreEqual(-1f, conf);
        }

        // 3.10
        [Test]
        public void UnkToken_InternalAccess()
        {
            Assert.AreEqual("[unk]", VoxrCommandParser.UnkToken);
        }

        [Test]
        public void SplitSeparator_InternalAccess()
        {
            Assert.IsNotNull(VoxrCommandParser.SplitSeparator);
            Assert.AreEqual(1, VoxrCommandParser.SplitSeparator.Length);
            Assert.AreEqual(' ', VoxrCommandParser.SplitSeparator[0]);
        }

        // ---------- The tied sibling rival (issue #74 item 2) ----------
        //
        // The flush fires the same command whether or not a rival tied it, so this record
        // changes no behaviour. It exists because the coin flip is otherwise invisible: the
        // winner is correct by every rule the parser has, and a 0.75 in the log looks healthy.

        static VoxrSlotDefinition[] ShipSlots() =>
            new[] { new VoxrSlotDefinition("ship", new[] { "alpha" }) };

        [Test]
        public void LastParseDiagnostics_SiblingTie_NamesTheRivalThatTiedTheWinner()
        {
            var parser = new VoxrCommandParser(
                ShipSlots(),
                new[]
                {
                    new VoxrCommandDefinition(
                        "set_mode",
                        new[] { new[] { "set", "{ship}", "mode", "on" } }
                    ),
                    new VoxrCommandDefinition(
                        "set_level",
                        new[] { new[] { "set", "{ship}", "level", "on" } }
                    ),
                }
            );

            var results = parser.Parse("set alpha on", null);

            Assert.AreEqual(1, results.Length, "the tie still resolves to exactly one command");
            Assert.AreEqual("set_mode", results[0].Command.Intent, "first-registered, as before");

            var diag = parser.LastParseDiagnostics;
            Assert.AreEqual(1, diag.Length);
            Assert.AreEqual(
                "set_level",
                diag[0].TiedSiblingIntent,
                "the rival that made the winner a coin flip"
            );
            Assert.AreEqual(0, diag[0].TiedSiblingPatternIndex);
        }

        [Test]
        public void LastParseDiagnostics_NoTie_RecordsNoRival()
        {
            var parser = CreateParser();
            parser.Parse("shoot missiles", null);

            var diag = parser.LastParseDiagnostics;
            Assert.AreEqual(1, diag.Length);
            Assert.IsNull(diag[0].TiedSiblingIntent);
            Assert.AreEqual(-1, diag[0].TiedSiblingPatternIndex);
        }

        [Test]
        public void LastParseDiagnostics_SameIntentTie_RecordsNoRival()
        {
            // Two phrasings of one command tie, but the same command dispatches either way, so
            // there is no coin flip to report. This is the PER-PAIR intent test doing work the
            // set-level filter could not: it is what keeps a same-intent rival out of the
            // record even when it is enumerated before a cross-intent one.
            var parser = new VoxrCommandParser(
                ShipSlots(),
                new[]
                {
                    new VoxrCommandDefinition(
                        "set_mode",
                        new[]
                        {
                            new[] { "set", "{ship}", "mode", "on" },
                            new[] { "set", "{ship}", "level", "on" },
                        }
                    ),
                }
            );

            parser.Parse("set alpha on", null);

            var diag = parser.LastParseDiagnostics;
            Assert.AreEqual(1, diag.Length);
            Assert.IsNull(diag[0].TiedSiblingIntent);
        }

        [Test]
        public void LastParseDiagnostics_ShadowedRival_IsTheCrossIntentOne()
        {
            // The winner's FIRST tied rival here is set_mode's own second pattern, which shares
            // its intent. Recording that one would name a rival the speaker never had a choice
            // about. The per-pair intent test skips it and the scan continues to set_level,
            // which is the real hazard — and the one issue #90's retention keeps in the set at
            // all.
            var parser = new VoxrCommandParser(
                ShipSlots(),
                new[]
                {
                    new VoxrCommandDefinition(
                        "set_mode",
                        new[]
                        {
                            new[] { "set", "{ship}", "mode", "on" },
                            new[] { "set", "{ship}", "level", "on" },
                        }
                    ),
                    new VoxrCommandDefinition(
                        "set_level",
                        new[] { new[] { "set", "{ship}", "level", "on" } }
                    ),
                }
            );

            parser.Parse("set alpha on", null);

            var diag = parser.LastParseDiagnostics;
            Assert.AreEqual(1, diag.Length);
            Assert.AreEqual(
                "set_level",
                diag[0].TiedSiblingIntent,
                "a same-intent rival must not shadow the cross-intent one"
            );
        }

        [Test]
        public void LastParseDiagnostics_SameIntentAcrossTwoCommands_IsNotTheRecordedRival()
        {
            // The intent test in AreSiblingRivals has two arms — `ci1 == ci2`, or two DISTINCT
            // commands whose Intent strings match — and only the first was covered. Both
            // existing fixtures put the same-intent rival in the same command, so they exit on
            // the index comparison and the string comparison was never reached.
            //
            // Here "set_mode" is registered as two separate commands. The winner's first tied
            // rival is the second set_mode command: a different command index, a different
            // discriminating value, sharing the set — so the index arm does NOT reject it, and
            // only the string arm keeps it out of the record. set_level, the genuine
            // cross-intent hazard, is what should be named.
            //
            // The eager verdict cannot show this: the set is cross-intent, so set_level ties
            // the winner too and the gate refuses either way. The recorded rival is where the
            // per-pair rule is observable at all.
            var parser = new VoxrCommandParser(
                ShipSlots(),
                new[]
                {
                    new VoxrCommandDefinition(
                        "set_mode",
                        new[] { new[] { "set", "{ship}", "mode", "on" } }
                    ),
                    new VoxrCommandDefinition(
                        "set_mode",
                        new[] { new[] { "set", "{ship}", "level", "on" } }
                    ),
                    new VoxrCommandDefinition(
                        "set_level",
                        new[] { new[] { "set", "{ship}", "level", "on" } }
                    ),
                }
            );

            parser.Parse("set alpha on", null);

            var diag = parser.LastParseDiagnostics;
            Assert.AreEqual(1, diag.Length);
            Assert.AreEqual(
                "set_level",
                diag[0].TiedSiblingIntent,
                "a rival sharing the winner's intent across two commands is still not a rival"
            );
        }
    }
}
