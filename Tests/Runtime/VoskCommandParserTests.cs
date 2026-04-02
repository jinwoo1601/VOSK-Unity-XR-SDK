using System;
using NUnit.Framework;
using VoskXR;
using VoskXR.Commands;

namespace VoskXR.Tests.Runtime
{
    public class VoskCommandParserTests
    {
        static VoskSlotDefinition[] MakeSlots()
        {
            return new[]
            {
                new VoskSlotDefinition("target",
                    new[] { "hotel one", "hotel two", "alpha one", "alpha three", "bravo two" }),
                new VoskSlotDefinition("weapon",
                    new[] { "missiles", "torpedoes", "jackal", "jackals" }),
                new VoskSlotDefinition("quantity",
                    new[] { "all", "one", "two", "three" }),
                new VoskSlotDefinition("range",
                    new[] { "cqb", "safe range", "torpedo range", "pdc range", "railgun range" }),
            };
        }

        static VoskCommandDefinition[] MakeCommands()
        {
            return new[]
            {
                new VoskCommandDefinition("launch_weapon", new[]
                {
                    new[] { "launch", "{?quantity}", "{weapon}", "target", "{target}" },
                    new[] { "launch", "a", "{weapon}", "target", "{target}" },
                    new[] { "fire", "{?quantity}", "{weapon}", "at", "{target}" },
                    new[] { "shoot", "{weapon}" },
                }),
                new VoskCommandDefinition("cease_fire", new[]
                {
                    new[] { "cease", "fire" },
                    new[] { "stop", "firing" },
                    new[] { "disengage" },
                }),
                new VoskCommandDefinition("resume_fire", new[]
                {
                    new[] { "resume", "fire" },
                    new[] { "resume", "firing" },
                    new[] { "reengage" },
                }),
                new VoskCommandDefinition("set_distance_named", new[]
                {
                    new[] { "close", "distance", "{range}", "target", "{target}" },
                    new[] { "set", "distance", "{range}", "target", "{target}" },
                }),
            };
        }

        VoskCommandParser CreateParser()
        {
            return new VoskCommandParser(MakeSlots(), MakeCommands());
        }

        [Test]
        public void ExactMatch_AllSlotsFilled()
        {
            var parser = CreateParser();

            var result = parser.Parse("launch all missiles target hotel one");

            Assert.IsTrue(result.IsMatch);
            Assert.AreEqual("launch_weapon", result.Command.Intent);
            Assert.AreEqual("missiles", result.Command.GetSlot("weapon"));
            Assert.AreEqual("all", result.Command.GetSlot("quantity"));
            Assert.AreEqual("hotel one", result.Command.GetSlot("target"));
            Assert.AreEqual("launch all missiles target hotel one", result.Command.RawText);
        }

        [Test]
        public void OptionalSlotMissing_StillMatches()
        {
            var parser = CreateParser();

            var result = parser.Parse("launch missiles target hotel one");

            Assert.IsTrue(result.IsMatch);
            Assert.AreEqual("launch_weapon", result.Command.Intent);
            Assert.AreEqual("missiles", result.Command.GetSlot("weapon"));
            Assert.AreEqual("hotel one", result.Command.GetSlot("target"));
            Assert.IsFalse(result.Command.HasSlot("quantity"));
            Assert.AreEqual(string.Empty, result.Command.GetSlot("quantity"));
        }

        [Test]
        public void SynonymPattern_SameResult()
        {
            var parser = CreateParser();

            var result = parser.Parse("fire all missiles at hotel one");

            Assert.IsTrue(result.IsMatch);
            Assert.AreEqual("launch_weapon", result.Command.Intent);
            Assert.AreEqual("missiles", result.Command.GetSlot("weapon"));
            Assert.AreEqual("all", result.Command.GetSlot("quantity"));
            Assert.AreEqual("hotel one", result.Command.GetSlot("target"));
        }

        [Test]
        public void MultiWordSlot_ExtractedAsSingleValue()
        {
            var parser = CreateParser();

            var result = parser.Parse("launch all missiles target hotel one");

            Assert.IsTrue(result.IsMatch);
            Assert.AreEqual("hotel one", result.Command.GetSlot("target"));
        }

        [Test]
        public void NoMatch_ReturnsFalse()
        {
            var parser = CreateParser();

            var result = parser.Parse("hello world");

            Assert.IsFalse(result.IsMatch);
            Assert.AreEqual("hello world", result.RawText);
        }

        [Test]
        public void EmptyInput_ReturnsFalse()
        {
            var parser = CreateParser();

            var result = parser.Parse("");

            Assert.IsFalse(result.IsMatch);
        }

        [Test]
        public void WhitespaceInput_ReturnsFalse()
        {
            var parser = CreateParser();

            var result = parser.Parse("   ");

            Assert.IsFalse(result.IsMatch);
        }

        [Test]
        public void NullInput_ReturnsFalse()
        {
            var parser = CreateParser();

            var result = parser.Parse(null);

            Assert.IsFalse(result.IsMatch);
        }

        [Test]
        public void CommandWithNoSlots_CeaseFire()
        {
            var parser = CreateParser();

            var result = parser.Parse("cease fire");

            Assert.IsTrue(result.IsMatch);
            Assert.AreEqual("cease_fire", result.Command.Intent);
            Assert.AreEqual(0, result.Command.Slots.Length);
        }

        [Test]
        public void UnkTokensSkipped()
        {
            var parser = CreateParser();

            var result = parser.Parse("launch [unk] missiles target hotel one");

            Assert.IsTrue(result.IsMatch);
            Assert.AreEqual("launch_weapon", result.Command.Intent);
            Assert.AreEqual("missiles", result.Command.GetSlot("weapon"));
            Assert.AreEqual("hotel one", result.Command.GetSlot("target"));
        }

        [Test]
        public void GrammarJson_ContainsExpectedWords()
        {
            var parser = CreateParser();

            string json = parser.GenerateGrammarJson();

            Assert.IsTrue(json.Contains("\"launch\""));
            Assert.IsTrue(json.Contains("\"fire\""));
            Assert.IsTrue(json.Contains("\"missiles\""));
            Assert.IsTrue(json.Contains("\"hotel\""));
            Assert.IsTrue(json.Contains("\"one\""));
            Assert.IsTrue(json.Contains("\"cease\""));
        }

        [Test]
        public void GrammarJson_ContainsUnk()
        {
            var parser = CreateParser();

            string json = parser.GenerateGrammarJson();

            Assert.IsTrue(json.Contains("\"[unk]\""));
        }

        [Test]
        public void GrammarJson_NoDuplicates()
        {
            var parser = CreateParser();

            string json = parser.GenerateGrammarJson();

            // "one" appears in both quantity slot and target "hotel one" — should not duplicate
            int firstIdx = json.IndexOf("\"one\"", StringComparison.Ordinal);
            Assert.IsTrue(firstIdx >= 0, "\"one\" should be present");

            int secondIdx = json.IndexOf("\"one\"", firstIdx + 1, StringComparison.Ordinal);
            Assert.IsTrue(secondIdx < 0, "\"one\" should not appear twice");
        }

        [Test]
        public void GrammarJson_IsValidJsonArray()
        {
            var parser = CreateParser();

            string json = parser.GenerateGrammarJson();

            Assert.IsTrue(json.StartsWith("["));
            Assert.IsTrue(json.EndsWith("]"));
        }

        [Test]
        public void UndefinedSlotReference_ThrowsAtConstruction()
        {
            var slots = new[] { new VoskSlotDefinition("weapon", new[] { "missiles" }) };
            var commands = new[]
            {
                new VoskCommandDefinition("test", new[]
                {
                    new[] { "fire", "{nonexistent}" }
                })
            };

            Assert.Throws<ArgumentException>(() => new VoskCommandParser(slots, commands));
        }

        [Test]
        public void UndefinedOptionalSlotReference_ThrowsAtConstruction()
        {
            var slots = new[] { new VoskSlotDefinition("weapon", new[] { "missiles" }) };
            var commands = new[]
            {
                new VoskCommandDefinition("test", new[]
                {
                    new[] { "fire", "{?nonexistent}" }
                })
            };

            Assert.Throws<ArgumentException>(() => new VoskCommandParser(slots, commands));
        }

        [Test]
        public void ConfidencePropagation_MinWordConfidence()
        {
            var parser = CreateParser();

            var words = new[]
            {
                new VoskWord("cease", 0.95f, 0.0f, 0.3f),
                new VoskWord("fire", 0.72f, 0.3f, 0.6f),
            };

            var result = parser.Parse("cease fire", words);

            Assert.IsTrue(result.IsMatch);
            Assert.AreEqual(0.72f, result.Command.Confidence, 0.001f);
        }

        [Test]
        public void NoWordData_ConfidenceIsZero()
        {
            var parser = CreateParser();

            var result = parser.Parse("cease fire");

            Assert.IsTrue(result.IsMatch);
            Assert.AreEqual(0f, result.Command.Confidence, 0.001f);
        }

        [Test]
        public void DefaultCommand_WhenNoMatch()
        {
            var parser = CreateParser();

            var result = parser.Parse("something random");

            Assert.IsFalse(result.IsMatch);
            Assert.AreEqual(default(VoskCommand), result.Command);
            Assert.IsNull(result.Command.Intent);
        }

        [Test]
        public void ShootWeapon_NoTargetPattern()
        {
            var parser = CreateParser();

            var result = parser.Parse("shoot missiles");

            Assert.IsTrue(result.IsMatch);
            Assert.AreEqual("launch_weapon", result.Command.Intent);
            Assert.AreEqual("missiles", result.Command.GetSlot("weapon"));
            Assert.IsFalse(result.Command.HasSlot("target"));
        }

        [Test]
        public void MultiWordRange_MatchedCorrectly()
        {
            var parser = CreateParser();

            var result = parser.Parse("set distance torpedo range target hotel two");

            Assert.IsTrue(result.IsMatch);
            Assert.AreEqual("set_distance_named", result.Command.Intent);
            Assert.AreEqual("torpedo range", result.Command.GetSlot("range"));
            Assert.AreEqual("hotel two", result.Command.GetSlot("target"));
        }

        [Test]
        public void SingleWordCommand_Disengage()
        {
            var parser = CreateParser();

            var result = parser.Parse("disengage");

            Assert.IsTrue(result.IsMatch);
            Assert.AreEqual("cease_fire", result.Command.Intent);
        }

        [Test]
        public void LeftoverTokensAllowed()
        {
            var parser = CreateParser();

            // "cease fire please" has leftover "please", but pattern still matches
            var result = parser.Parse("cease fire please");

            Assert.IsTrue(result.IsMatch);
            Assert.AreEqual("cease_fire", result.Command.Intent);
        }

        [Test]
        public void EmptyCommandSet_GrammarContainsOnlyUnk()
        {
            var parser = new VoskCommandParser(
                Array.Empty<VoskSlotDefinition>(),
                Array.Empty<VoskCommandDefinition>());

            string json = parser.GenerateGrammarJson();

            Assert.AreEqual("[\"[unk]\"]", json);
        }
    }
}
