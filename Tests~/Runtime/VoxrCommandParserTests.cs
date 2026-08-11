using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine.TestTools;
using VoXR;
using VoXR.Commands;

namespace VoXR.Tests.Runtime
{
    public class VoxrCommandParserTests
    {
        static VoxrSlotDefinition[] MakeSlots()
        {
            return new[]
            {
                new VoxrSlotDefinition("target",
                    new[] { "hotel one", "hotel two", "alpha one", "alpha three", "bravo two" }),
                new VoxrSlotDefinition("weapon",
                    new[] { "missiles", "torpedoes", "jackal" },
                    aliases: new Dictionary<string, string> { { "jackals", "jackal" } }),
                new VoxrSlotDefinition("quantity",
                    new[] { "all", "one", "two", "three" },
                    aliases: new Dictionary<string, string> { { "a", "one" } }),
                new VoxrSlotDefinition("range",
                    new[] { "cqb", "safe range", "torpedo range", "pdc range", "railgun range" }),
            };
        }

        static VoxrCommandDefinition[] MakeCommands()
        {
            return new[]
            {
                new VoxrCommandDefinition("launch_weapon", new[]
                {
                    new[] { "launch", "?a", "{?quantity}", "{weapon}", "target", "{target}" },
                    new[] { "fire", "{?quantity}", "{weapon}", "at", "{target}" },
                    new[] { "shoot", "{weapon}" },
                }),
                new VoxrCommandDefinition("cease_fire", new[]
                {
                    new[] { "cease", "fire" },
                    new[] { "stop", "firing" },
                    new[] { "disengage" },
                }),
                new VoxrCommandDefinition("resume_fire", new[]
                {
                    new[] { "resume", "fire" },
                    new[] { "resume", "firing" },
                    new[] { "reengage" },
                }),
                new VoxrCommandDefinition("set_distance_named", new[]
                {
                    new[] { "close", "distance", "{range}", "target", "{target}" },
                    new[] { "set", "distance", "{range}", "target", "{target}" },
                }),
            };
        }

        VoxrCommandParser CreateParser()
        {
            return new VoxrCommandParser(MakeSlots(), MakeCommands());
        }

        static VoxrCommandResult ParseOne(VoxrCommandParser parser, string text,
            VoxrWord[] words = null)
        {
            var results = words != null ? parser.Parse(text, words) : parser.Parse(text);
            Assert.AreEqual(1, results.Length,
                $"Expected 1 match for \"{text}\" but got {results.Length}");
            return results[0];
        }

        [Test]
        public void ExactMatch_AllSlotsFilled()
        {
            var parser = CreateParser();

            var result = ParseOne(parser, "launch all missiles target hotel one");

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

            var result = ParseOne(parser, "launch missiles target hotel one");

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

            var result = ParseOne(parser, "fire all missiles at hotel one");

            Assert.AreEqual("launch_weapon", result.Command.Intent);
            Assert.AreEqual("missiles", result.Command.GetSlot("weapon"));
            Assert.AreEqual("all", result.Command.GetSlot("quantity"));
            Assert.AreEqual("hotel one", result.Command.GetSlot("target"));
        }

        [Test]
        public void MultiWordSlot_ExtractedAsSingleValue()
        {
            var parser = CreateParser();

            var result = ParseOne(parser, "launch all missiles target hotel one");

            Assert.AreEqual("hotel one", result.Command.GetSlot("target"));
        }

        [Test]
        public void NoMatch_ReturnsEmpty()
        {
            var parser = CreateParser();

            var results = parser.Parse("hello world");

            Assert.AreEqual(0, results.Length);
        }

        [Test]
        public void EmptyInput_ReturnsEmpty()
        {
            var parser = CreateParser();

            var results = parser.Parse("");

            Assert.AreEqual(0, results.Length);
        }

        [Test]
        public void WhitespaceInput_ReturnsEmpty()
        {
            var parser = CreateParser();

            var results = parser.Parse("   ");

            Assert.AreEqual(0, results.Length);
        }

        [Test]
        public void NullInput_ReturnsEmpty()
        {
            var parser = CreateParser();

            var results = parser.Parse(null);

            Assert.AreEqual(0, results.Length);
        }

        [Test]
        public void CommandWithNoSlots_CeaseFire()
        {
            var parser = CreateParser();

            var result = ParseOne(parser, "cease fire");

            Assert.AreEqual("cease_fire", result.Command.Intent);
            Assert.AreEqual(0, result.Command.Slots.Length);
        }

        [Test]
        public void UnkTokensSkipped()
        {
            var parser = CreateParser();

            var result = ParseOne(parser, "launch [unk] missiles target hotel one");

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
            var slots = new[] { new VoxrSlotDefinition("weapon", new[] { "missiles" }) };
            var commands = new[]
            {
                new VoxrCommandDefinition("test", new[]
                {
                    new[] { "fire", "{nonexistent}" }
                })
            };

            Assert.Throws<ArgumentException>(() => new VoxrCommandParser(slots, commands));
        }

        [Test]
        public void UndefinedOptionalSlotReference_ThrowsAtConstruction()
        {
            var slots = new[] { new VoxrSlotDefinition("weapon", new[] { "missiles" }) };
            var commands = new[]
            {
                new VoxrCommandDefinition("test", new[]
                {
                    new[] { "fire", "{?nonexistent}" }
                })
            };

            Assert.Throws<ArgumentException>(() => new VoxrCommandParser(slots, commands));
        }

        [Test]
        public void ConfidencePropagation_MinWordConfidence()
        {
            var parser = CreateParser();

            var words = new[]
            {
                new VoxrWord("cease", 0.95f, 0.0f, 0.3f),
                new VoxrWord("fire", 0.72f, 0.3f, 0.6f),
            };

            var result = ParseOne(parser, "cease fire", words);

            Assert.AreEqual(0.72f, result.Command.Confidence, 0.001f);
        }

        [Test]
        public void NoWordData_ConfidenceIsNegativeOne()
        {
            var parser = CreateParser();

            var result = ParseOne(parser, "cease fire");

            Assert.AreEqual(-1f, result.Command.Confidence, 0.001f);
        }

        [Test]
        public void NoMatch_EmptyArray()
        {
            var parser = CreateParser();

            var results = parser.Parse("something random");

            Assert.AreEqual(0, results.Length);
        }

        [Test]
        public void ShootWeapon_NoTargetPattern()
        {
            var parser = CreateParser();

            var result = ParseOne(parser, "shoot missiles");

            Assert.AreEqual("launch_weapon", result.Command.Intent);
            Assert.AreEqual("missiles", result.Command.GetSlot("weapon"));
            Assert.IsFalse(result.Command.HasSlot("target"));
        }

        [Test]
        public void MultiWordRange_MatchedCorrectly()
        {
            var parser = CreateParser();

            var result = ParseOne(parser, "set distance torpedo range target hotel two");

            Assert.AreEqual("set_distance_named", result.Command.Intent);
            Assert.AreEqual("torpedo range", result.Command.GetSlot("range"));
            Assert.AreEqual("hotel two", result.Command.GetSlot("target"));
        }

        [Test]
        public void SingleWordCommand_Disengage()
        {
            var parser = CreateParser();

            var result = ParseOne(parser, "disengage");

            Assert.AreEqual("cease_fire", result.Command.Intent);
        }

        [Test]
        public void EmptyCommandSet_GrammarContainsOnlyUnk()
        {
            var parser = new VoxrCommandParser(
                Array.Empty<VoxrSlotDefinition>(),
                Array.Empty<VoxrCommandDefinition>());

            string json = parser.GenerateGrammarJson();

            Assert.AreEqual("[\"[unk]\"]", json);
        }

        [Test]
        public void Score_PerfectMatch_HighScore()
        {
            var parser = CreateParser();

            var result = ParseOne(parser, "cease fire");

            Assert.AreEqual(1.0f, result.Command.Score, 0.001f);
        }

        [Test]
        public void Score_BetterMatchWins()
        {
            // "cease fire" is 2/2 = 1.0, "disengage" is 1/1 = 1.0 — but "cease fire"
            // has more literals and should win on tie-break
            var parser = CreateParser();

            var result = ParseOne(parser, "cease fire");
            Assert.AreEqual("cease_fire", result.Command.Intent);
        }

        [Test]
        public void OptionalLiteral_PresentInInput()
        {
            var parser = CreateParser();

            // Pattern: "launch", "?a", "{?quantity}", "{weapon}", "target", "{target}"
            // Input has "a" present
            var result = ParseOne(parser, "launch a jackal target hotel one");

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
            var result = ParseOne(parser, "launch jackal target hotel one");

            Assert.AreEqual("launch_weapon", result.Command.Intent);
            Assert.AreEqual("jackal", result.Command.GetSlot("weapon"));
            Assert.AreEqual("hotel one", result.Command.GetSlot("target"));
        }

        [Test]
        public void SlidingStart_PreambleSkipped()
        {
            var parser = CreateParser();

            // "uh" is not a grammar word; sliding start should find "cease fire" starting at token 1
            var result = ParseOne(parser, "uh cease fire");

            Assert.AreEqual("cease_fire", result.Command.Intent);
        }

        [Test]
        public void SlidingStart_FalseStartRecovery()
        {
            var parser = CreateParser();

            // Double "launch" — sliding start finds best match from later position
            var result = ParseOne(parser, "launch launch all missiles target hotel one");

            Assert.AreEqual("launch_weapon", result.Command.Intent);
            Assert.AreEqual("missiles", result.Command.GetSlot("weapon"));
            Assert.AreEqual("hotel one", result.Command.GetSlot("target"));
        }

        [Test]
        public void Alias_ResolvesToCanonicalValue()
        {
            var parser = CreateParser();

            var result = ParseOne(parser, "shoot jackals");

            Assert.AreEqual("launch_weapon", result.Command.Intent);
            Assert.AreEqual("jackal", result.Command.GetSlot("weapon"));
        }

        [Test]
        public void Alias_QuantityA_ResolvesToOne()
        {
            var parser = CreateParser();

            // "a" in quantity context should resolve to "one" via alias
            var result = ParseOne(parser, "launch a missiles target hotel one");

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

        static VoxrSlotDefinition[] MakeNumericSlots()
        {
            return new[]
            {
                new VoxrSlotDefinition("target",
                    new[] { "hotel one", "hotel two", "bravo two" }),
                VoxrSlotDefinition.NumberSequence("heading", minWords: 1, maxWords: 3),
                VoxrSlotDefinition.NumberSequence("elevation", minWords: 1, maxWords: 2),
            };
        }

        static VoxrCommandDefinition[] MakeNumericCommands()
        {
            return new[]
            {
                new VoxrCommandDefinition("set_heading", new[]
                {
                    new[] { "orient", "to", "heading", "{heading}" },
                    new[] { "orient", "to", "heading", "{heading}", "mark", "{?elevation}" },
                }),
                new VoxrCommandDefinition("close_distance", new[]
                {
                    new[] { "close", "distance", "{heading}", "klicks", "target", "{target}" },
                }),
            };
        }

        VoxrCommandParser CreateNumericParser()
        {
            return new VoxrCommandParser(MakeNumericSlots(), MakeNumericCommands());
        }

        // --- NumberSequence tests ---

        [Test]
        public void NumberSequence_ThreeDigitWords_Match()
        {
            var parser = CreateNumericParser();

            var result = ParseOne(parser, "orient to heading two seven zero");

            Assert.AreEqual("set_heading", result.Command.Intent);
            Assert.AreEqual("two seven zero", result.Command.GetSlot("heading"));
        }

        [Test]
        public void NumberSequence_WithOptionalElevation()
        {
            var parser = CreateNumericParser();

            var result = ParseOne(parser, "orient to heading two seven zero mark one five");

            Assert.AreEqual("set_heading", result.Command.Intent);
            Assert.AreEqual("two seven zero", result.Command.GetSlot("heading"));
            Assert.AreEqual("one five", result.Command.GetSlot("elevation"));
        }

        [Test]
        public void NumberSequence_SingleDigitWord_Match()
        {
            var parser = CreateNumericParser();

            var result = ParseOne(parser, "orient to heading five");

            Assert.AreEqual("set_heading", result.Command.Intent);
            Assert.AreEqual("five", result.Command.GetSlot("heading"));
        }

        [Test]
        public void NumberSequence_StopsAtNonDigitWord()
        {
            var parser = CreateNumericParser();

            // "mark" is not a digit word — heading should stop at 3 words
            var result = ParseOne(parser, "orient to heading two seven zero mark");

            Assert.AreEqual("two seven zero", result.Command.GetSlot("heading"));
        }

        [Test]
        public void NumberSequence_RespectsMaxWords()
        {
            // elevation has maxWords=2; input has 3 digit words after "mark"
            var parser = CreateNumericParser();

            var result = ParseOne(parser, "orient to heading five mark one two three");

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
                VoxrSlotDefinition.NumberSequence("code", minWords: 2, maxWords: 4),
            };
            var commands = new[]
            {
                // Short pattern so a required slot miss brings score to <= 0
                new VoxrCommandDefinition("enter_code", new[]
                {
                    new[] { "enter", "{code}" },
                }),
            };
            var parser = new VoxrCommandParser(slots, commands);

            // Only 1 digit word — below minWords=2, required slot fails
            var results = parser.Parse("enter five");

            Assert.AreEqual(0, results.Length);
        }

        [Test]
        public void NumberSequence_OptionalMissing_StillMatches()
        {
            var parser = CreateNumericParser();

            // "orient to heading five mark" — no digits after "mark" for optional elevation
            var result = ParseOne(parser, "orient to heading five mark");

            Assert.AreEqual("set_heading", result.Command.Intent);
            Assert.AreEqual("five", result.Command.GetSlot("heading"));
            Assert.IsFalse(result.Command.HasSlot("elevation"));
        }

        [Test]
        public void NumberSequence_MixedWithEnumerated()
        {
            var parser = CreateNumericParser();

            var result = ParseOne(parser, "close distance fifteen klicks target bravo two");

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

            var result = ParseOne(parser, "launch all missiles target hotel one");

            Assert.GreaterOrEqual(result.Command.Score, 0f);
            Assert.LessOrEqual(result.Command.Score, 1f);
        }

        [Test]
        public void Score_ShortAndLongPatterns_Comparable()
        {
            var parser = CreateParser();

            // "disengage" = 1 element pattern, score = 1/1 = 1.0
            var shortResult = ParseOne(parser, "disengage");
            // "cease fire" = 2 element pattern, score = 2/2 = 1.0
            var longResult = ParseOne(parser, "cease fire");

            // Both should have score 1.0 since they match perfectly
            Assert.AreEqual(shortResult.Command.Score, longResult.Command.Score, 0.001f);
        }

        [Test]
        public void Validation_UppercaseSlotValue_NoException()
        {
            // Should log a warning but not throw
            var slots = new[] { new VoxrSlotDefinition("weapon", new[] { "Missiles" }) };
            var commands = new[]
            {
                new VoxrCommandDefinition("test", new[]
                {
                    new[] { "fire", "{weapon}" }
                })
            };

            Assert.DoesNotThrow(() => new VoxrCommandParser(slots, commands));
        }

        [Test]
        public void Validation_SingleCharSlotValue_NoException()
        {
            // Single-char values should warn but not throw
            var slots = new[] { new VoxrSlotDefinition("quantity", new[] { "a", "one" }) };
            var commands = new[]
            {
                new VoxrCommandDefinition("test", new[]
                {
                    new[] { "fire", "{quantity}" }
                })
            };

            Assert.DoesNotThrow(() => new VoxrCommandParser(slots, commands));
        }

        [Test]
        public void LeftoverTokens_StillMatches()
        {
            var parser = CreateParser();

            // "cease fire please" — "please" is leftover. Sliding start from 0 matches
            // "cease fire" with score 2/2 = 1.0, which is the best.
            var result = ParseOne(parser, "cease fire please");

            Assert.AreEqual("cease_fire", result.Command.Intent);
        }

        // --- Sequential command extraction tests (v2.3) ---

        [Test]
        public void Sequential_TwoCommandsInOneUtterance()
        {
            var parser = CreateParser();

            // "cease fire" should match cease_fire, then remaining tokens match launch_weapon
            var results = parser.Parse("cease fire launch all missiles target hotel one");

            Assert.AreEqual(2, results.Length);
            Assert.AreEqual("cease_fire", results[0].Command.Intent);
            Assert.AreEqual("launch_weapon", results[1].Command.Intent);
            Assert.AreEqual("missiles", results[1].Command.GetSlot("weapon"));
            Assert.AreEqual("hotel one", results[1].Command.GetSlot("target"));
        }

        [Test]
        public void Sequential_SingleCommandNoRemainder()
        {
            var parser = CreateParser();

            var results = parser.Parse("launch missiles target hotel one");

            Assert.AreEqual(1, results.Length);
            Assert.AreEqual("launch_weapon", results[0].Command.Intent);
        }

        [Test]
        public void Sequential_TwoShortCommandsInOrder()
        {
            var parser = CreateParser();

            var results = parser.Parse("cease fire resume fire");

            Assert.AreEqual(2, results.Length);
            Assert.AreEqual("cease_fire", results[0].Command.Intent);
            Assert.AreEqual("resume_fire", results[1].Command.Intent);
        }

        [Test]
        public void Sequential_NoisePlusSingleCommand()
        {
            var parser = CreateParser();

            // "hello world" is noise, sliding start finds "cease fire"
            var results = parser.Parse("hello world cease fire");

            Assert.AreEqual(1, results.Length);
            Assert.AreEqual("cease_fire", results[0].Command.Intent);
        }

        [Test]
        public void Sequential_NoiseDoesNotProduceSecondCommand()
        {
            var parser = CreateParser();

            // After extracting "cease fire", "please" has no match
            var results = parser.Parse("cease fire please");

            Assert.AreEqual(1, results.Length);
            Assert.AreEqual("cease_fire", results[0].Command.Intent);
        }

        [Test]
        public void Sequential_RawTextIsFullInput()
        {
            var parser = CreateParser();

            var results = parser.Parse("cease fire resume fire");

            // Both commands should carry the full original text
            Assert.AreEqual("cease fire resume fire", results[0].Command.RawText);
            Assert.AreEqual("cease fire resume fire", results[1].Command.RawText);
        }

        [Test]
        public void Sequential_CommandThenWeaponCommand()
        {
            var parser = CreateParser();

            // Reversed order: weapon command first, then cease_fire
            var results = parser.Parse("launch missiles target hotel one cease fire");

            Assert.AreEqual(2, results.Length);
            Assert.AreEqual("launch_weapon", results[0].Command.Intent);
            Assert.AreEqual("cease_fire", results[1].Command.Intent);
        }

        [Test]
        public void Sequential_ConfidencePropagatedPerCommand()
        {
            var parser = CreateParser();

            var words = new[]
            {
                new VoxrWord("cease", 0.95f, 0.0f, 0.3f),
                new VoxrWord("fire", 0.72f, 0.3f, 0.6f),
                new VoxrWord("resume", 0.88f, 0.7f, 1.0f),
            };

            // "cease fire resume fire" — two commands
            // "fire" appears in both; word confidence map stores first occurrence (0.72)
            var results = parser.Parse("cease fire resume fire", words);

            Assert.AreEqual(2, results.Length);
            // First command: min(cease=0.95, fire=0.72) = 0.72
            Assert.AreEqual(0.72f, results[0].Command.Confidence, 0.001f);
            // Second command: min(resume=0.88, fire=0.72) = 0.72
            Assert.AreEqual(0.72f, results[1].Command.Confidence, 0.001f);
        }

        // --- Command Set Support Tests (v2.4) ---

        [Test]
        public void FilteredCommands_OnlyMatchesSubset()
        {
            var slots = MakeSlots();
            var subsetCommands = new[]
            {
                new VoxrCommandDefinition("cease_fire", new[]
                {
                    new[] { "cease", "fire" },
                    new[] { "stop", "firing" },
                    new[] { "disengage" },
                }),
                new VoxrCommandDefinition("resume_fire", new[]
                {
                    new[] { "resume", "fire" },
                }),
            };

            var parser = new VoxrCommandParser(slots, subsetCommands);

            var ceaseResult = parser.Parse("cease fire");
            Assert.AreEqual(1, ceaseResult.Length);
            Assert.AreEqual("cease_fire", ceaseResult[0].Command.Intent);

            // launch_weapon is not in the subset — should not match
            var launchResult = parser.Parse("launch all missiles target hotel one");
            Assert.AreEqual(0, launchResult.Length);
        }

        [Test]
        public void FilteredCommands_GrammarExcludesRemovedLiterals()
        {
            var slots = MakeSlots();
            var subsetCommands = new[]
            {
                new VoxrCommandDefinition("cease_fire", new[]
                {
                    new[] { "cease", "fire" },
                }),
            };

            var parser = new VoxrCommandParser(slots, subsetCommands);
            string json = parser.GenerateGrammarJson();

            Assert.IsTrue(json.Contains("\"cease\""), "Active literal should be in grammar");
            Assert.IsTrue(json.Contains("\"fire\""), "Active literal should be in grammar");
            Assert.IsFalse(json.Contains("\"launch\""), "Inactive literal should not be in grammar");
            Assert.IsFalse(json.Contains("\"shoot\""), "Inactive literal should not be in grammar");
        }

        [Test]
        public void FilteredCommands_GrammarStillIncludesSharedSlotValues()
        {
            var slots = MakeSlots();
            // Only cease_fire — doesn't reference any slots
            var subsetCommands = new[]
            {
                new VoxrCommandDefinition("cease_fire", new[]
                {
                    new[] { "cease", "fire" },
                }),
            };

            var parser = new VoxrCommandParser(slots, subsetCommands);
            string json = parser.GenerateGrammarJson();

            // Slot values are always included (slots are shared/global)
            Assert.IsTrue(json.Contains("\"missiles\""), "Shared slot values should remain in grammar");
            Assert.IsTrue(json.Contains("\"hotel\""), "Shared slot values should remain in grammar");
        }

        [Test]
        public void CombinedSets_AllCommandsMatch()
        {
            var slots = MakeSlots();
            // Simulate combining commands from two sets
            var combinedCommands = new[]
            {
                new VoxrCommandDefinition("cease_fire", new[]
                {
                    new[] { "cease", "fire" },
                }),
                new VoxrCommandDefinition("launch_weapon", new[]
                {
                    new[] { "launch", "{?quantity}", "{weapon}", "target", "{target}" },
                }),
            };

            var parser = new VoxrCommandParser(slots, combinedCommands);

            var ceaseResult = parser.Parse("cease fire");
            Assert.AreEqual(1, ceaseResult.Length);
            Assert.AreEqual("cease_fire", ceaseResult[0].Command.Intent);

            var launchResult = parser.Parse("launch all missiles target hotel one");
            Assert.AreEqual(1, launchResult.Length);
            Assert.AreEqual("launch_weapon", launchResult[0].Command.Intent);
        }

        // --- Optional tokens no longer inflate the score denominator (issue #21) ---

        static VoxrCommandParser MakeOptionalLiteralParser()
        {
            var slots = new[] { new VoxrSlotDefinition("device", new[] { "light", "fan" }) };
            var commands = new[]
            {
                new VoxrCommandDefinition("activate", new[]
                {
                    new[] { "turn", "on", "?the", "{device}" },
                }),
            };
            return new VoxrCommandParser(slots, commands);
        }

        [Test]
        public void Score_OptionalLiteralPresent_ReachesOne()
        {
            var parser = MakeOptionalLiteralParser();

            // Optional literal "?the" spoken: (1 + 1 + 0.5 + 1) / (1 + 1 + 0.5 + 1) = 1.0.
            // Previously this capped at 3.5/4 = 0.875 (optional literal never reached full credit).
            var result = ParseOne(parser, "turn on the light");

            Assert.AreEqual("activate", result.Command.Intent);
            Assert.AreEqual("light", result.Command.GetSlot("device"));
            Assert.AreEqual(1.0f, result.Command.Score, 0.001f);
        }

        [Test]
        public void Score_OptionalLiteralOmitted_NotPenalized()
        {
            var parser = MakeOptionalLiteralParser();

            // Omitting "?the" must not cost anything: (1 + 1 + 1) / (1 + 1 + 1) = 1.0.
            // Previously this was 3.0/4 = 0.75 — penalized for using optionality.
            var result = ParseOne(parser, "turn on fan");

            Assert.AreEqual("activate", result.Command.Intent);
            Assert.AreEqual("fan", result.Command.GetSlot("device"));
            Assert.AreEqual(1.0f, result.Command.Score, 0.001f);
        }

        [Test]
        public void Score_ShortPatternOptionalOmitted_AboveDefaultThreshold()
        {
            // Pattern ["go", "?now"] spoken as just "go". Under the old static denominator
            // this scored 1.0/2 = 0.5 and was rejected by the default minScore of 0.6.
            // The dynamic denominator drops the omitted optional, giving 1.0/1.0 = 1.0.
            var slots = Array.Empty<VoxrSlotDefinition>();
            var commands = new[]
            {
                new VoxrCommandDefinition("go", new[]
                {
                    new[] { "go", "?now" },
                }),
            };
            var parser = new VoxrCommandParser(slots, commands);

            var result = ParseOne(parser, "go");

            Assert.AreEqual("go", result.Command.Intent);
            Assert.AreEqual(1.0f, result.Command.Score, 0.001f);
        }

        [Test]
        public void ScoreFollowUp_OptionalLiteralOmitted_ReachesOne()
        {
            // Follow-up scoring must use the same dynamic denominator so initial and
            // follow-up scores stay consistent: a filled required slot plus required
            // literals, with the optional literal dropping out, scores 1.0 (not 3/4).
            var parser = MakeOptionalLiteralParser();

            var filled = new[] { new VoxrSlotMatch("device", "light") };
            float score = parser.ScoreFollowUp("activate", 0, filled);

            Assert.AreEqual(1.0f, score, 0.001f);
        }

        // --- Skipped-word penalty (issue #31) ---

        [Test]
        public void SkippedWord_ShortPatternInUtteranceTail_FallsBelowDefaultThreshold()
        {
            // Issue #31: "disengage" is a one-element pattern. Reached only by skipping the
            // in-grammar word "target", it used to score a full 1.0 and fire — any stray
            // utterance whose tail resembles a short pattern could execute it. The skipped
            // word now counts toward the denominator: 1 / (1 + 1) = 0.5.
            var parser = CreateParser();

            var result = ParseOne(parser, "target disengage");

            Assert.AreEqual("cease_fire", result.Command.Intent);
            Assert.AreEqual(0.5f, result.Command.Score, 0.001f);
            Assert.Less(result.Command.Score, 0.6f,
                "should now be rejected by the default minScore of 0.6");
        }

        [Test]
        public void SkippedWord_UnkFillerNotCharged()
        {
            // Tolerating out-of-grammar preamble and hesitation is what the sliding start
            // is for, so [unk] runs stay free and the score is still a perfect 1.0.
            var parser = CreateParser();

            var result = ParseOne(parser, "[unk] [unk] disengage");

            Assert.AreEqual("cease_fire", result.Command.Intent);
            Assert.AreEqual(1.0f, result.Command.Score, 0.001f);
        }

        [Test]
        public void SkippedWord_LongPatternAbsorbsFalseStart()
        {
            // The penalty is proportional, so it only bites patterns short enough to be
            // swallowed by a stray utterance. A six-element pattern reached past one
            // repeated word scores 5 / (5 + 1) = 0.833 and still clears minScore.
            var parser = CreateParser();

            var result = ParseOne(parser, "launch launch all missiles target hotel one");

            Assert.AreEqual("launch_weapon", result.Command.Intent);
            Assert.AreEqual(5f / 6f, result.Command.Score, 0.001f);
            Assert.Greater(result.Command.Score, 0.6f);
        }

        [Test]
        public void SkippedWord_SecondCommandInUtterance_NotCharged()
        {
            // Skipped words are counted from where the previous match ended, not from the
            // start of the utterance, so chained commands are not penalized for each other.
            var parser = CreateParser();

            var results = parser.Parse("cease fire resume fire");

            Assert.AreEqual(2, results.Length);
            Assert.AreEqual(1.0f, results[0].Command.Score, 0.001f);
            Assert.AreEqual("resume_fire", results[1].Command.Intent);
            Assert.AreEqual(1.0f, results[1].Command.Score, 0.001f);
        }

        [Test]
        public void SkippedWord_PenaltyDisabled_RestoresFullScore()
        {
            var parser = new VoxrCommandParser(MakeSlots(), MakeCommands(), 0f);

            var result = ParseOne(parser, "target disengage");

            Assert.AreEqual("cease_fire", result.Command.Intent);
            Assert.AreEqual(1.0f, result.Command.Score, 0.001f);
        }

        // --- Equal-score span tie-break (issue #41) ---

        // A tailed pattern and its bare sibling both score 1.0 with equal literal counts
        // on an utterance carrying the tail, so before the span tie-break the winner was
        // whichever the asset listed first. "burn_level" is also a command in its own
        // right, so a bare-pattern win split one order into two commands.
        static VoxrCommandParser CreateTailedParser(bool bareFirst)
        {
            var slots = new[]
            {
                VoxrSlotDefinition.NumberSequence("track", minWords: 1, maxWords: 4),
                new VoxrSlotDefinition("burn_level", new[] { "maximum burn", "minimum burn" }),
            };

            var bare = new[] { "intercept", "track", "{track}" };
            var tailed = new[] { "intercept", "track", "{track}", "{burn_level}" };

            var commands = new[]
            {
                new VoxrCommandDefinition(
                    "intercept_target",
                    bareFirst ? new[] { bare, tailed } : new[] { tailed, bare }
                ),
                new VoxrCommandDefinition("set_burn", new[] { new[] { "{burn_level}" } }),
            };

            return new VoxrCommandParser(slots, commands);
        }

        [Test]
        public void SpanTieBreak_TailedPatternWins_WhenBareSiblingListedFirst()
        {
            var parser = CreateTailedParser(bareFirst: true);

            var results = parser.Parse("intercept track one two zero one maximum burn");

            Assert.AreEqual(
                1,
                results.Length,
                "the tail must stay part of the intercept, not become a second command"
            );
            Assert.AreEqual("intercept_target", results[0].Command.Intent);
            Assert.AreEqual("one two zero one", results[0].Command.GetSlot("track"));
            Assert.AreEqual("maximum burn", results[0].Command.GetSlot("burn_level"));
            Assert.AreEqual(1, results[0].Command.MatchedPatternIndex);
        }

        [Test]
        public void SpanTieBreak_OutcomeIndependentOfPatternOrder()
        {
            var parser = CreateTailedParser(bareFirst: false);

            var results = parser.Parse("intercept track one two zero one maximum burn");

            Assert.AreEqual(1, results.Length);
            Assert.AreEqual("intercept_target", results[0].Command.Intent);
            Assert.AreEqual("maximum burn", results[0].Command.GetSlot("burn_level"));
            Assert.AreEqual(0, results[0].Command.MatchedPatternIndex);
        }

        [Test]
        public void SpanTieBreak_BareUtterance_StillMatchesBarePattern()
        {
            // Span only breaks *exact* score ties: without the tail the tailed pattern misses
            // a required slot, which is a negative numerator term (RequiredSlotMissPenalty),
            // so it scores (1+1+1-1)/4 = 0.5 and the bare sibling wins on score outright.
            var parser = CreateTailedParser(bareFirst: true);

            var results = parser.Parse("intercept track one two zero one");

            Assert.AreEqual(1, results.Length);
            Assert.AreEqual("intercept_target", results[0].Command.Intent);
            Assert.AreEqual("one two zero one", results[0].Command.GetSlot("track"));
            Assert.IsFalse(results[0].Command.HasSlot("burn_level"));
            Assert.AreEqual(0, results[0].Command.MatchedPatternIndex);
        }

        [Test]
        public void SpanTieBreak_StandaloneTailStillMatchesItsOwnCommand()
        {
            // Longer-span preference only redirects tails that an earlier-starting match
            // can absorb — a tail spoken on its own is still its own command.
            var parser = CreateTailedParser(bareFirst: true);

            var results = parser.Parse("maximum burn");

            Assert.AreEqual(1, results.Length);
            Assert.AreEqual("set_burn", results[0].Command.Intent);
            Assert.AreEqual("maximum burn", results[0].Command.GetSlot("burn_level"));
        }

        [Test]
        public void SpanTieBreak_LongerSpanBeatsHigherLiteralCount()
        {
            // Span sits ABOVE literal count, so it also settles equal-score candidates whose
            // literal counts differ — outcomes literal count used to decide on its own,
            // deterministically, in either declaration order. Both patterns score 1.0 at
            // token 0; the slot pattern has fewer literals but covers one more token.
            var parser = new VoxrCommandParser(
                new[] { new VoxrSlotDefinition("target", new[] { "hotel one" }) },
                new[]
                {
                    new VoxrCommandDefinition(
                        "fire_at",
                        new[]
                        {
                            new[] { "fire", "at", "hotel" },
                            new[] { "fire", "at", "{target}" },
                        }
                    ),
                }
            );

            var result = ParseOne(parser, "fire at hotel one");

            Assert.AreEqual(
                1,
                result.Command.MatchedPatternIndex,
                "the 2-literal/4-token pattern must beat the 3-literal/3-token one"
            );
            Assert.AreEqual("hotel one", result.Command.GetSlot("target"));
        }

        [Test]
        public void SpanTieBreak_ChoosesBetweenCommands_NotJustPatterns()
        {
            // The comparison runs across the whole command list, so a span tie changes which
            // *intent* fires, not merely which pattern index within one command. Both match
            // fully at token 0 with one literal each; go_place covers one more token.
            var parser = new VoxrCommandParser(
                new[]
                {
                    new VoxrSlotDefinition("dir", new[] { "north" }),
                    new VoxrSlotDefinition("place", new[] { "north pole" }),
                },
                new[]
                {
                    new VoxrCommandDefinition("go_dir", new[] { new[] { "go", "{dir}" } }),
                    new VoxrCommandDefinition("go_place", new[] { new[] { "go", "{place}" } }),
                }
            );

            var result = ParseOne(parser, "go north pole");

            Assert.AreEqual(
                "go_place",
                result.Command.Intent,
                "the longer-span command wins even though it is declared second"
            );
            Assert.AreEqual("north pole", result.Command.GetSlot("place"));
        }

        [Test]
        public void SpanTieBreak_TrailingUnkCannotWinTheTie()
        {
            // The span is measured over tokens a pattern actually matched. EndIdx alone would
            // overstate it — the [unk] skip runs before every element, including a trailing
            // optional that then matches nothing — which would let "fire {?mode}" beat "fire"
            // purely by absorbing filler, and make the same spoken command report a different
            // pattern index whenever VOSK emitted noise.
            var parser = new VoxrCommandParser(
                new[] { new VoxrSlotDefinition("mode", new[] { "silent" }) },
                new[]
                {
                    new VoxrCommandDefinition(
                        "fire",
                        new[] { new[] { "fire" }, new[] { "fire", "{?mode}" } }
                    ),
                }
            );

            Assert.AreEqual(0, ParseOne(parser, "fire").Command.MatchedPatternIndex);
            Assert.AreEqual(
                0,
                ParseOne(parser, "fire [unk]").Command.MatchedPatternIndex,
                "a stray [unk] must not flip which pattern is reported"
            );
        }

        // ---------- Droppable required literal before a slot (issue #42) ----------

        static VoxrSlotDefinition[] BurnSlots() =>
            new[] { new VoxrSlotDefinition("burn_level", new[] { "coast", "hard burn" }) };

        static VoxrCommandDefinition[] DecelerateCommands(string separator) =>
            new[]
            {
                new VoxrCommandDefinition(
                    "decelerate",
                    new[]
                    {
                        new[] { "decelerate" },
                        new[] { "decelerate", separator, "{burn_level}" },
                    }
                ),
            };

        [Test]
        public void RequiredLiteralBeforeSlot_WarnsAtConstruction()
        {
            LogAssert.Expect(UnityEngine.LogType.Warning, new Regex("required literal \"by\""));

            var parser = new VoxrCommandParser(BurnSlots(), DecelerateCommands("by"));

            Assert.IsNotNull(
                parser,
                "the shape is a warning, not an error — construction still succeeds"
            );
        }

        [Test]
        public void RequiredLiteralDropped_BarePatternWinsAndDiscardsTheSpokenSlot()
        {
            // The behaviour the warning names. VOSK drops short unstressed function words
            // more than any other token, and when "by" goes the slot-filled pattern is
            // charged for the miss while the bare one still matches perfectly.
            LogAssert.Expect(UnityEngine.LogType.Warning, new Regex("required literal \"by\""));
            var parser = new VoxrCommandParser(BurnSlots(), DecelerateCommands("by"));

            var result = ParseOne(parser, "decelerate hard burn");

            Assert.AreEqual(
                0,
                result.Command.MatchedPatternIndex,
                "the bare pattern scores a clean 1.0 and wins"
            );
            Assert.IsFalse(
                result.Command.HasSlot("burn_level"),
                "the spoken burn level is discarded with nothing to signal it"
            );
        }

        [Test]
        public void OptionalLiteralBeforeSlot_KeepsTheSlotWhenTheLiteralIsDropped()
        {
            // The remedy the warning recommends: with the literal optional the slot-filled
            // pattern scores 1.0 whether or not the word was spoken, so it takes the
            // consumed-span tie-break (issue #41) over the bare form instead of losing to it.
            var parser = new VoxrCommandParser(BurnSlots(), DecelerateCommands("?by"));

            var dropped = ParseOne(parser, "decelerate hard burn");
            Assert.AreEqual(1, dropped.Command.MatchedPatternIndex);
            Assert.AreEqual("hard burn", dropped.Command.GetSlot("burn_level"));

            var spoken = ParseOne(parser, "decelerate by hard burn");
            Assert.AreEqual(1, spoken.Command.MatchedPatternIndex);
            Assert.AreEqual("hard burn", spoken.Command.GetSlot("burn_level"));

            var bare = ParseOne(parser, "decelerate");
            Assert.AreEqual(
                0,
                bare.Command.MatchedPatternIndex,
                "the bare form still wins when no value was spoken"
            );
            Assert.IsFalse(bare.Command.HasSlot("burn_level"));

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void NonHazardPatternShapes_DoNotWarn()
        {
            // A sibling that adds the slot directly has no literal to drop; one that adds a
            // second literal instead of a slot has no value to discard; and a sibling that is
            // not an element-prefix of the short pattern is a different phrasing, not a bare
            // form of it. None of them can strand a spoken value, so none of them warn.
            var parser = new VoxrCommandParser(
                BurnSlots(),
                new[]
                {
                    new VoxrCommandDefinition(
                        "decelerate",
                        new[]
                        {
                            new[] { "decelerate" },
                            new[] { "decelerate", "{burn_level}" },
                            new[] { "decelerate", "to", "station", "keeping" },
                            new[] { "slow", "by", "{burn_level}" },
                        }
                    ),
                }
            );

            Assert.IsNotNull(parser);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void OptionalSlotAfterTheLiteral_AlsoWarns()
        {
            // The stranded value need not be required to be lost: "orient heading two seven
            // zero mark one five" with "mark" dropped scores 0.7 against the bare pattern's
            // 1.0, so the elevation goes the same way the burn level does.
            LogAssert.Expect(UnityEngine.LogType.Warning, new Regex("required literal \"mark\""));

            var parser = new VoxrCommandParser(
                new[] { VoxrSlotDefinition.NumberSequence("elevation", minWords: 1, maxWords: 2) },
                new[]
                {
                    new VoxrCommandDefinition(
                        "orient",
                        new[] { new[] { "orient" }, new[] { "orient", "mark", "{?elevation}" } }
                    ),
                }
            );

            Assert.IsNotNull(parser);
        }
    }
}
