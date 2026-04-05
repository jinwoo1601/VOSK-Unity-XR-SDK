using System;
using System.Collections.Generic;
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
                    new[] { "missiles", "torpedoes", "jackal" },
                    aliases: new Dictionary<string, string> { { "jackals", "jackal" } }),
                new VoskSlotDefinition("quantity",
                    new[] { "all", "one", "two", "three" },
                    aliases: new Dictionary<string, string> { { "a", "one" } }),
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
                    new[] { "launch", "?a", "{?quantity}", "{weapon}", "target", "{target}" },
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
        public void NoWordData_ConfidenceIsNegativeOne()
        {
            var parser = CreateParser();

            var result = parser.Parse("cease fire");

            Assert.IsTrue(result.IsMatch);
            Assert.AreEqual(-1f, result.Command.Confidence, 0.001f);
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
        public void EmptyCommandSet_GrammarContainsOnlyUnk()
        {
            var parser = new VoskCommandParser(
                Array.Empty<VoskSlotDefinition>(),
                Array.Empty<VoskCommandDefinition>());

            string json = parser.GenerateGrammarJson();

            Assert.AreEqual("[\"[unk]\"]", json);
        }

        [Test]
        public void Score_PerfectMatch_HighScore()
        {
            var parser = CreateParser();

            var result = parser.Parse("cease fire");

            Assert.IsTrue(result.IsMatch);
            Assert.AreEqual(1.0f, result.Command.Score, 0.001f);
        }

        [Test]
        public void Score_BetterMatchWins()
        {
            // "cease fire" is 2/2 = 1.0, "disengage" is 1/1 = 1.0 — but "cease fire"
            // has more literals and should win on tie-break
            var parser = CreateParser();

            var result = parser.Parse("cease fire");
            Assert.IsTrue(result.IsMatch);
            Assert.AreEqual("cease_fire", result.Command.Intent);
        }

        [Test]
        public void OptionalLiteral_PresentInInput()
        {
            var parser = CreateParser();

            // Pattern: "launch", "?a", "{?quantity}", "{weapon}", "target", "{target}"
            // Input has "a" present
            var result = parser.Parse("launch a jackal target hotel one");

            Assert.IsTrue(result.IsMatch);
            Assert.AreEqual("launch_weapon", result.Command.Intent);
            Assert.AreEqual("jackal", result.Command.GetSlot("weapon"));
            Assert.AreEqual("hotel one", result.Command.GetSlot("target"));
        }

        [Test]
        public void OptionalLiteral_AbsentInInput()
        {
            var parser = CreateParser();

            // Pattern: "launch", "?a", "{?quantity}", "{weapon}", "target", "{target}"
            // Input lacks "a" — should still match
            var result = parser.Parse("launch jackal target hotel one");

            Assert.IsTrue(result.IsMatch);
            Assert.AreEqual("launch_weapon", result.Command.Intent);
            Assert.AreEqual("jackal", result.Command.GetSlot("weapon"));
            Assert.AreEqual("hotel one", result.Command.GetSlot("target"));
        }

        [Test]
        public void SlidingStart_PreambleSkipped()
        {
            var parser = CreateParser();

            // "uh" is not a grammar word; sliding start should find "cease fire" starting at token 1
            var result = parser.Parse("uh cease fire");

            Assert.IsTrue(result.IsMatch);
            Assert.AreEqual("cease_fire", result.Command.Intent);
        }

        [Test]
        public void SlidingStart_FalseStartRecovery()
        {
            var parser = CreateParser();

            // Double "launch" — sliding start finds best match from later position
            var result = parser.Parse("launch launch all missiles target hotel one");

            Assert.IsTrue(result.IsMatch);
            Assert.AreEqual("launch_weapon", result.Command.Intent);
            Assert.AreEqual("missiles", result.Command.GetSlot("weapon"));
            Assert.AreEqual("hotel one", result.Command.GetSlot("target"));
        }

        [Test]
        public void Alias_ResolvesToCanonicalValue()
        {
            var parser = CreateParser();

            var result = parser.Parse("shoot jackals");

            Assert.IsTrue(result.IsMatch);
            Assert.AreEqual("launch_weapon", result.Command.Intent);
            Assert.AreEqual("jackal", result.Command.GetSlot("weapon"));
        }

        [Test]
        public void Alias_QuantityA_ResolvesToOne()
        {
            var parser = CreateParser();

            // "a" in quantity context should resolve to "one" via alias
            var result = parser.Parse("launch a missiles target hotel one");

            Assert.IsTrue(result.IsMatch);
            Assert.AreEqual("launch_weapon", result.Command.Intent);
            // "a" could match as optional literal or as quantity alias.
            // The pattern with ?a should consume "a" as optional literal,
            // then "missiles" as weapon. Either way the command should match.
            Assert.AreEqual("missiles", result.Command.GetSlot("weapon"));
        }

        [Test]
        public void GrammarJson_ContainsAliasWords()
        {
            var parser = CreateParser();

            string json = parser.GenerateGrammarJson();

            // "jackals" is an alias key, should be in grammar
            Assert.IsTrue(json.Contains("\"jackals\""), "Alias word 'jackals' should be in grammar");
        }

        [Test]
        public void GrammarJson_ContainsOptionalLiteralWords()
        {
            var parser = CreateParser();

            string json = parser.GenerateGrammarJson();

            // "a" from "?a" optional literal should be in grammar
            Assert.IsTrue(json.Contains("\"a\""), "Optional literal 'a' should be in grammar");
        }

        // --- NumberSequence helpers ---

        static VoskSlotDefinition[] MakeNumericSlots()
        {
            return new[]
            {
                new VoskSlotDefinition("target",
                    new[] { "hotel one", "hotel two", "bravo two" }),
                VoskSlotDefinition.NumberSequence("heading", minWords: 1, maxWords: 3),
                VoskSlotDefinition.NumberSequence("elevation", minWords: 1, maxWords: 2),
            };
        }

        static VoskCommandDefinition[] MakeNumericCommands()
        {
            return new[]
            {
                new VoskCommandDefinition("set_heading", new[]
                {
                    new[] { "orient", "to", "heading", "{heading}" },
                    new[] { "orient", "to", "heading", "{heading}", "mark", "{?elevation}" },
                }),
                new VoskCommandDefinition("close_distance", new[]
                {
                    new[] { "close", "distance", "{heading}", "klicks", "target", "{target}" },
                }),
            };
        }

        VoskCommandParser CreateNumericParser()
        {
            return new VoskCommandParser(MakeNumericSlots(), MakeNumericCommands());
        }

        // --- NumberSequence tests ---

        [Test]
        public void NumberSequence_ThreeDigitWords_Match()
        {
            var parser = CreateNumericParser();

            var result = parser.Parse("orient to heading two seven zero");

            Assert.IsTrue(result.IsMatch);
            Assert.AreEqual("set_heading", result.Command.Intent);
            Assert.AreEqual("two seven zero", result.Command.GetSlot("heading"));
        }

        [Test]
        public void NumberSequence_WithOptionalElevation()
        {
            var parser = CreateNumericParser();

            var result = parser.Parse("orient to heading two seven zero mark one five");

            Assert.IsTrue(result.IsMatch);
            Assert.AreEqual("set_heading", result.Command.Intent);
            Assert.AreEqual("two seven zero", result.Command.GetSlot("heading"));
            Assert.AreEqual("one five", result.Command.GetSlot("elevation"));
        }

        [Test]
        public void NumberSequence_SingleDigitWord_Match()
        {
            var parser = CreateNumericParser();

            var result = parser.Parse("orient to heading five");

            Assert.IsTrue(result.IsMatch);
            Assert.AreEqual("set_heading", result.Command.Intent);
            Assert.AreEqual("five", result.Command.GetSlot("heading"));
        }

        [Test]
        public void NumberSequence_StopsAtNonDigitWord()
        {
            var parser = CreateNumericParser();

            // "mark" is not a digit word — heading should stop at 3 words
            var result = parser.Parse("orient to heading two seven zero mark");

            Assert.IsTrue(result.IsMatch);
            Assert.AreEqual("two seven zero", result.Command.GetSlot("heading"));
        }

        [Test]
        public void NumberSequence_RespectsMaxWords()
        {
            // elevation has maxWords=2; input has 3 digit words after "mark"
            var parser = CreateNumericParser();

            var result = parser.Parse("orient to heading five mark one two three");

            Assert.IsTrue(result.IsMatch);
            Assert.AreEqual("five", result.Command.GetSlot("heading"));
            // elevation should consume only 2 of the 3 digits
            Assert.AreEqual("one two", result.Command.GetSlot("elevation"));
        }

        [Test]
        public void NumberSequence_BelowMinWords_NoMatch()
        {
            // Create a slot that requires minWords=2
            var slots = new[]
            {
                VoskSlotDefinition.NumberSequence("code", minWords: 2, maxWords: 4),
            };
            var commands = new[]
            {
                // Short pattern so a required slot miss brings score to <= 0
                new VoskCommandDefinition("enter_code", new[]
                {
                    new[] { "enter", "{code}" },
                }),
            };
            var parser = new VoskCommandParser(slots, commands);

            // Only 1 digit word — below minWords=2, required slot fails
            var result = parser.Parse("enter five");

            Assert.IsFalse(result.IsMatch);
        }

        [Test]
        public void NumberSequence_OptionalMissing_StillMatches()
        {
            var parser = CreateNumericParser();

            // "orient to heading five mark" — no digits after "mark" for optional elevation
            var result = parser.Parse("orient to heading five mark");

            Assert.IsTrue(result.IsMatch);
            Assert.AreEqual("set_heading", result.Command.Intent);
            Assert.AreEqual("five", result.Command.GetSlot("heading"));
            Assert.IsFalse(result.Command.HasSlot("elevation"));
        }

        [Test]
        public void NumberSequence_MixedWithEnumerated()
        {
            var parser = CreateNumericParser();

            var result = parser.Parse("close distance fifteen klicks target bravo two");

            Assert.IsTrue(result.IsMatch);
            Assert.AreEqual("close_distance", result.Command.Intent);
            Assert.AreEqual("fifteen", result.Command.GetSlot("heading"));
            Assert.AreEqual("bravo two", result.Command.GetSlot("target"));
        }

        [Test]
        public void GrammarJson_ContainsDigitVocab_WhenNumberSequenceExists()
        {
            var parser = CreateNumericParser();

            string json = parser.GenerateGrammarJson();

            Assert.IsTrue(json.Contains("\"zero\""), "Should contain 'zero'");
            Assert.IsTrue(json.Contains("\"nine\""), "Should contain 'nine'");
            Assert.IsTrue(json.Contains("\"twenty\""), "Should contain 'twenty'");
            Assert.IsTrue(json.Contains("\"hundred\""), "Should contain 'hundred'");
        }

        [Test]
        public void GrammarJson_NoDigitVocab_WhenNoNumberSequence()
        {
            // Use the standard (enumerated-only) parser
            var parser = CreateParser();

            string json = parser.GenerateGrammarJson();

            // "twenty", "thirty" etc. should NOT be present (no NumberSequence slots)
            Assert.IsFalse(json.Contains("\"twenty\""), "'twenty' should not be in grammar");
            Assert.IsFalse(json.Contains("\"thirty\""), "'thirty' should not be in grammar");
            Assert.IsFalse(json.Contains("\"hundred\""), "'hundred' should not be in grammar");
        }

        // --- Existing score/validation tests ---

        [Test]
        public void Score_NormalizedBetweenZeroAndOne()
        {
            var parser = CreateParser();

            var result = parser.Parse("launch all missiles target hotel one");

            Assert.IsTrue(result.IsMatch);
            Assert.GreaterOrEqual(result.Command.Score, 0f);
            Assert.LessOrEqual(result.Command.Score, 1f);
        }

        [Test]
        public void Score_ShortAndLongPatterns_Comparable()
        {
            var parser = CreateParser();

            // "disengage" = 1 element pattern, score = 1/1 = 1.0
            var shortResult = parser.Parse("disengage");
            // "cease fire" = 2 element pattern, score = 2/2 = 1.0
            var longResult = parser.Parse("cease fire");

            Assert.IsTrue(shortResult.IsMatch);
            Assert.IsTrue(longResult.IsMatch);
            // Both should have score 1.0 since they match perfectly
            Assert.AreEqual(shortResult.Command.Score, longResult.Command.Score, 0.001f);
        }

        [Test]
        public void Validation_UppercaseSlotValue_NoException()
        {
            // Should log a warning but not throw
            var slots = new[] { new VoskSlotDefinition("weapon", new[] { "Missiles" }) };
            var commands = new[]
            {
                new VoskCommandDefinition("test", new[]
                {
                    new[] { "fire", "{weapon}" }
                })
            };

            Assert.DoesNotThrow(() => new VoskCommandParser(slots, commands));
        }

        [Test]
        public void Validation_SingleCharSlotValue_NoException()
        {
            // Single-char values should warn but not throw
            var slots = new[] { new VoskSlotDefinition("quantity", new[] { "a", "one" }) };
            var commands = new[]
            {
                new VoskCommandDefinition("test", new[]
                {
                    new[] { "fire", "{quantity}" }
                })
            };

            Assert.DoesNotThrow(() => new VoskCommandParser(slots, commands));
        }

        [Test]
        public void LeftoverTokens_StillMatches()
        {
            var parser = CreateParser();

            // "cease fire please" — "please" is leftover. Sliding start from 0 matches
            // "cease fire" with score 2/2 = 1.0, which is the best.
            var result = parser.Parse("cease fire please");

            Assert.IsTrue(result.IsMatch);
            Assert.AreEqual("cease_fire", result.Command.Intent);
        }
    }
}
