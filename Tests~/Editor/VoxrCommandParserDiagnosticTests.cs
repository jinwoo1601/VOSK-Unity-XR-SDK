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
    }
}
