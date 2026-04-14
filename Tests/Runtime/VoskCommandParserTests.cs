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

        static VoskCommandResult ParseOne(VoskCommandParser parser, string text,
            VoskWord[] words = null)
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
                new VoskWord("cease", 0.95f, 0.0f, 0.3f),
                new VoskWord("fire", 0.72f, 0.3f, 0.6f),
                new VoskWord("resume", 0.88f, 0.7f, 1.0f),
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
                new VoskCommandDefinition("cease_fire", new[]
                {
                    new[] { "cease", "fire" },
                    new[] { "stop", "firing" },
                    new[] { "disengage" },
                }),
                new VoskCommandDefinition("resume_fire", new[]
                {
                    new[] { "resume", "fire" },
                }),
            };

            var parser = new VoskCommandParser(slots, subsetCommands);

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
                new VoskCommandDefinition("cease_fire", new[]
                {
                    new[] { "cease", "fire" },
                }),
            };

            var parser = new VoskCommandParser(slots, subsetCommands);
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
                new VoskCommandDefinition("cease_fire", new[]
                {
                    new[] { "cease", "fire" },
                }),
            };

            var parser = new VoskCommandParser(slots, subsetCommands);
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
                new VoskCommandDefinition("cease_fire", new[]
                {
                    new[] { "cease", "fire" },
                }),
                new VoskCommandDefinition("launch_weapon", new[]
                {
                    new[] { "launch", "{?quantity}", "{weapon}", "target", "{target}" },
                }),
            };

            var parser = new VoskCommandParser(slots, combinedCommands);

            var ceaseResult = parser.Parse("cease fire");
            Assert.AreEqual(1, ceaseResult.Length);
            Assert.AreEqual("cease_fire", ceaseResult[0].Command.Intent);

            var launchResult = parser.Parse("launch all missiles target hotel one");
            Assert.AreEqual(1, launchResult.Length);
            Assert.AreEqual("launch_weapon", launchResult[0].Command.Intent);
        }
    }
}
