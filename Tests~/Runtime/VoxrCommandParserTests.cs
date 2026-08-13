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

        // --- Phrase-chunked grammar entries (issue #45) ---

        [Test]
        public void GrammarJson_EmitsContiguousLiteralRunAsPhrase()
        {
            var parser = CreateParser();

            string json = parser.GenerateGrammarJson();

            Assert.IsTrue(json.Contains("\"cease fire\""), "Expected phrase entry 'cease fire'");
            Assert.IsTrue(json.Contains("\"stop firing\""), "Expected phrase entry 'stop firing'");
            Assert.IsTrue(
                json.Contains("\"close distance\""),
                "Expected phrase entry 'close distance'"
            );
        }

        [Test]
        public void GrammarJson_EmitsMultiWordSlotValuesAsPhrases()
        {
            var parser = CreateParser();

            string json = parser.GenerateGrammarJson();

            Assert.IsTrue(json.Contains("\"hotel one\""), "Expected slot surface form 'hotel one'");
            Assert.IsTrue(
                json.Contains("\"safe range\""),
                "Expected slot surface form 'safe range'"
            );
        }

        [Test]
        public void GrammarJson_PhraseEntriesKeepTheirIndividualWords()
        {
            var parser = CreateParser();

            string json = parser.GenerateGrammarJson();

            // A VAD split mid-phrase still has to decode as fragments, so every word
            // of a phrase stays individually legal — the phrase is a bias, not a rule.
            Assert.IsTrue(json.Contains("\"cease\""), "'cease' should remain a single-word entry");
            Assert.IsTrue(json.Contains("\"fire\""), "'fire' should remain a single-word entry");
            Assert.IsTrue(json.Contains("\"hotel\""), "'hotel' should remain a single-word entry");
            Assert.IsTrue(json.Contains("\"range\""), "'range' should remain a single-word entry");
        }

        [Test]
        public void GrammarJson_SlotBoundaryEndsThePhrase()
        {
            var parser = CreateParser();

            string json = parser.GenerateGrammarJson();

            // "close distance {range} target {target}" must not weld literals across
            // a slot — the slot's value sits between them when the pattern is spoken.
            // Assert the exact welds the boundary prevents: these are what the
            // emitter produces if EITHER the flush or the reset at the slot branch
            // is dropped, so they fail on both forms of the defect.
            Assert.IsFalse(
                json.Contains("\"close distance target\""),
                "Phrase must not span a slot boundary"
            );
            Assert.IsFalse(
                json.Contains("\"set distance target\""),
                "Phrase must not span a slot boundary"
            );

            // ...while the runs either side must still be emitted in their own right.
            Assert.IsTrue(json.Contains("\"close distance\""), "Expected the pre-slot run");
            Assert.IsTrue(json.Contains("\"target\""), "Expected the post-slot run");
        }

        [Test]
        public void GrammarJson_OptionalLiteralDoesNotJoinItsNeighbours()
        {
            var parser = CreateParser();

            string json = parser.GenerateGrammarJson();

            // "launch ?a {?quantity} ..." — "a" may be omitted, so it must not be
            // welded onto "launch"; both stay separately legal.
            Assert.IsFalse(
                json.Contains("\"launch a\""),
                "Optional literal must not be welded into a phrase"
            );
            Assert.IsTrue(json.Contains("\"launch\""), "'launch' should be a single-word entry");
            Assert.IsTrue(json.Contains("\"a\""), "'a' should be a single-word entry");
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
            LogAssert.NoUnexpectedReceived();
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
            LogAssert.NoUnexpectedReceived();
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
            // The stranded value need not be required to be lost. For this grammar, "orient
            // mark one five" with "mark" dropped scores (1 + 0 + 1) / 3 = 0.667 against the
            // bare pattern's 1.0, so the elevation goes the same way the burn level does.
            // Issue #65 §5.1 raised that 0.5 to 0.667 and it changes nothing here: the hazard
            // is that nothing normalized to 1.0 can be beaten, which is symptom 2's territory.
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
            LogAssert.NoUnexpectedReceived();
        }

        // Mirrors the shipped set_heading grammar (DemoGrammar / CommandDemo /
        // Cmd_SetHeading.asset) after the "mark" -> "?mark" change, so the edit made to the
        // sample is pinned here rather than only reasoned about. Both halves matter: what
        // the optional literal buys, and what it costs.
        static VoxrCommandParser HeadingParser() =>
            new VoxrCommandParser(
                new[]
                {
                    VoxrSlotDefinition.NumberSequence("heading", minWords: 1, maxWords: 3),
                    VoxrSlotDefinition.NumberSequence("elevation", minWords: 1, maxWords: 2),
                },
                new[]
                {
                    new VoxrCommandDefinition(
                        "set_heading",
                        new[]
                        {
                            new[] { "orient", "heading", "{heading}" },
                            new[] { "orient", "heading", "{heading}", "?mark", "{?elevation}" },
                        }
                    ),
                }
            );

        [Test]
        public void OptionalLiteralBeforeNumberSequence_RecoversTheElidedLiteral()
        {
            var parser = HeadingParser();

            var spoken = ParseOne(parser, "orient heading two seven zero mark one five");
            Assert.AreEqual(1, spoken.Command.MatchedPatternIndex);
            Assert.AreEqual("two seven zero", spoken.Command.GetSlot("heading"));
            Assert.AreEqual("one five", spoken.Command.GetSlot("elevation"));

            // The point of the change: with "mark" gone the elevation survives. Under the
            // required form this scored 4/5 = 0.8, lost to the bare pattern's 1.0, and the
            // elevation was discarded.
            var elided = ParseOne(parser, "orient heading two seven zero one five");
            Assert.AreEqual(1, elided.Command.MatchedPatternIndex);
            Assert.AreEqual("one five", elided.Command.GetSlot("elevation"));

            var plain = ParseOne(parser, "orient heading two seven zero");
            Assert.AreEqual(
                0,
                plain.Command.MatchedPatternIndex,
                "with nothing after the heading, the bare pattern still wins the span tie"
            );
            Assert.IsFalse(plain.Command.HasSlot("elevation"));

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void OptionalLiteralBeforeNumberSequence_AlsoClaimsAnUnmarkedStrayDigit()
        {
            // The documented price of the remedy (KNOWN_LIMITATIONS, "A dropped required
            // literal…"): an optional literal no longer anchors the slot behind it, so a
            // spurious fourth digit past the maxed-out heading is absorbed as an elevation
            // nobody marked, and wins on span at 4/4 = 1.0. The required form scored
            // 4/5 = 0.8 here and correctly dropped the stray token. Pinned so the tradeoff
            // is a known, tested consequence rather than a surprise.
            var parser = HeadingParser();

            var stray = ParseOne(parser, "orient heading two seven zero four");

            Assert.AreEqual(1, stray.Command.MatchedPatternIndex);
            Assert.AreEqual("two seven zero", stray.Command.GetSlot("heading"));
            Assert.AreEqual("four", stray.Command.GetSlot("elevation"));
        }

        // ---------- Widened detector scope (PR #58 review) ----------
        // The scan mirrors what ParseInternal compares, so all three of these strand a slot
        // value the same way the single-literal same-command shape does, and all three warn.

        [Test]
        public void MultipleRequiredLiteralsBeforeTheSlot_Warn()
        {
            // "decelerate hard burn" with only "the" dropped still loses to the bare pattern.
            LogAssert.Expect(
                UnityEngine.LogType.Warning,
                new Regex("required literals including \"by\"")
            );

            var parser = new VoxrCommandParser(
                BurnSlots(),
                new[]
                {
                    new VoxrCommandDefinition(
                        "decelerate",
                        new[]
                        {
                            new[] { "decelerate" },
                            new[] { "decelerate", "by", "the", "{burn_level}" },
                        }
                    ),
                }
            );

            Assert.IsNotNull(parser);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void HazardSplitAcrossTwoIntents_Warns()
        {
            // Selection runs across every command, so declaring the two phrasings as separate
            // intents reproduces the hazard exactly — a per-command scan would miss it.
            LogAssert.Expect(UnityEngine.LogType.Warning, new Regex("is a bare form of"));

            var parser = new VoxrCommandParser(
                BurnSlots(),
                new[]
                {
                    new VoxrCommandDefinition("decelerate", new[] { new[] { "decelerate" } }),
                    new VoxrCommandDefinition(
                        "decelerate_by",
                        new[] { new[] { "decelerate", "by", "{burn_level}" } }
                    ),
                }
            );

            var result = ParseOne(parser, "decelerate hard burn");
            Assert.AreEqual(
                "decelerate",
                result.Command.Intent,
                "the bare intent wins and the spoken burn level is stranded"
            );
            Assert.IsFalse(result.Command.HasSlot("burn_level"));
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void BareFormReachableOnlyByOmittingAnOptional_Warns()
        {
            // "fire {?quantity} {weapon}" is not literally a prefix of "fire {weapon} at
            // {target}", but it is once its own optional is omitted — which is exactly the
            // form the parser matches when no quantity is spoken.
            LogAssert.Expect(UnityEngine.LogType.Warning, new Regex("required literal \"at\""));

            var parser = new VoxrCommandParser(
                new[]
                {
                    new VoxrSlotDefinition("weapon", new[] { "missiles" }),
                    new VoxrSlotDefinition("quantity", new[] { "two" }),
                    new VoxrSlotDefinition("target", new[] { "hotel one" }),
                },
                new[]
                {
                    new VoxrCommandDefinition(
                        "fire",
                        new[]
                        {
                            new[] { "fire", "{?quantity}", "{weapon}" },
                            new[] { "fire", "{weapon}", "at", "{target}" },
                        }
                    ),
                }
            );

            var result = ParseOne(parser, "fire missiles hotel one");
            Assert.AreEqual(
                0,
                result.Command.MatchedPatternIndex,
                "the bare form wins at 1.0 and the target is stranded"
            );
            Assert.IsFalse(result.Command.HasSlot("target"));
            LogAssert.NoUnexpectedReceived();
        }

        // --- Required-literal miss cost (issue #65 §5.1) ---
        //
        // A missed required literal withholds its credit but is no longer ALSO charged a
        // penalty, so one drop costs 1/N of the 1.0 ceiling instead of 1.5/N. These tests
        // are one row each of the design's §5.1 table, and every expected value is written
        // as its arithmetic so the reader can check it without running anything.
        //
        // The shared MakeCommands() grammar cannot express these cases — its patterns are 1,
        // 2, 5 and 6 elements, none with a droppable required literal — so this region builds
        // its own fixtures. None of them registers a bare sibling of the pattern under test:
        // with one present the bare form wins selection at 1.0 and the drop never gets scored,
        // which is symptom 2 and explicitly NOT what this feature fixes.

        static VoxrCommandParser MissedLiteralParser() =>
            new VoxrCommandParser(
                BurnSlots(),
                new[]
                {
                    new VoxrCommandDefinition(
                        "time_to_target",
                        new[] { new[] { "time", "to", "target" } }
                    ),
                    new VoxrCommandDefinition(
                        "decelerate_by",
                        new[] { new[] { "decelerate", "by", "{burn_level}" } }
                    ),
                    new VoxrCommandDefinition("cease_fire", new[] { new[] { "cease", "fire" } }),
                }
            );

        [Test]
        public void MissedLiteral_ThreeElementPattern_ClearsThreshold()
        {
            // The in-headset case from the #42 cycle: "time to target" heard as "time target".
            // "time" and "target" match, "to" is dropped and counts toward the denominator
            // only: (1 + 0 + 1) / 3 = 0.667, over the default minScore of 0.6. It scored
            // (1 - 0.5 + 1) / 3 = 0.5 before and did not fire at all.
            var parser = MissedLiteralParser();

            var result = ParseOne(parser, "time target");

            Assert.AreEqual("time_to_target", result.Command.Intent);
            Assert.AreEqual(2f / 3f, result.Command.Score, 0.001f);
            Assert.GreaterOrEqual(
                result.Command.Score,
                0.6f,
                "the whole point is that it now clears the default minScore"
            );
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void MissedLiteral_SlotStillExtracted()
        {
            // The drop must not cost the argument as well as the score. "decelerate by
            // {burn_level}" heard as "decelerate hard burn": (1 + 0 + 1) / 3 = 0.667 with
            // burn_level still filled, where it scored (1 - 0.5 + 1) / 3 = 0.5 before.
            var parser = MissedLiteralParser();

            var result = ParseOne(parser, "decelerate hard burn");

            Assert.AreEqual("decelerate_by", result.Command.Intent);
            Assert.AreEqual(2f / 3f, result.Command.Score, 0.001f);
            Assert.AreEqual("hard burn", result.Command.GetSlot("burn_level"));
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void MissedLiteral_TwoElementPattern_StillRejected()
        {
            // Deliberately preserved, not an oversight: "cease fire" heard as "fire" scores
            // (0 + 1) / 2 = 0.5 — half its evidence — and stays under the gate. It was 0.25.
            // Parse itself applies no threshold, so the command IS returned here; the
            // recogniser-level counterpart proves it does not fire.
            var parser = MissedLiteralParser();

            var result = ParseOne(parser, "fire");

            Assert.AreEqual("cease_fire", result.Command.Intent);
            Assert.AreEqual(0.5f, result.Command.Score, 0.001f);
            Assert.Less(result.Command.Score, 0.6f, "half the evidence must not clear minScore");
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void MissedLiteral_LongPattern_CostIsProportional()
        {
            // The cost stays length-proportional — halved, not abolished (design fork F1).
            // Seven elements with "target" dropped: (1 + 1 + 0 + 1 + 1 + 1 + 1) / 7 = 0.857,
            // up from 5.5 / 7 = 0.786. The same single drop that a 3-element pattern feels as
            // 0.333 costs this one 0.143.
            var parser = new VoxrCommandParser(
                new[]
                {
                    new VoxrSlotDefinition("weapon", new[] { "missiles", "torpedoes" }),
                    new VoxrSlotDefinition("target", new[] { "hotel one", "alpha three" }),
                },
                new[]
                {
                    new VoxrCommandDefinition(
                        "launch_weapon",
                        new[]
                        {
                            new[]
                            {
                                "launch",
                                "{weapon}",
                                "target",
                                "{target}",
                                "on",
                                "my",
                                "mark",
                            },
                        }
                    ),
                }
            );

            var result = ParseOne(parser, "launch missiles hotel one on my mark");

            Assert.AreEqual(6f / 7f, result.Command.Score, 0.001f);
            Assert.AreEqual("missiles", result.Command.GetSlot("weapon"));
            Assert.AreEqual("hotel one", result.Command.GetSlot("target"));
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void MissedLiteral_BoundaryCase_ExactlySixTenths()
        {
            // The accepted consequence, ratified at G1: two drops on a five-element pattern
            // land on (1 + 0 + 0 + 1 + 1) / 5 = 0.60 exactly — the gate value itself — where
            // they scored (1 - 0.5 - 0.5 + 1 + 1) / 5 = 0.40 before. The gate stays >=, so
            // this fires. 3f/5f is bit-identical to the 0.6f the gate holds, so this is a real
            // equality and not a tolerance artifact; the recogniser-level counterpart is what
            // proves the gate actually admits it.
            var parser = new VoxrCommandParser(
                BurnSlots(),
                new[]
                {
                    new VoxrCommandDefinition(
                        "set_burn",
                        new[] { new[] { "set", "burn", "to", "{burn_level}", "now" } }
                    ),
                }
            );

            var result = ParseOne(parser, "set hard burn now");

            Assert.AreEqual(3f / 5f, result.Command.Score, 0.001f);
            Assert.AreEqual(
                "hard burn",
                result.Command.GetSlot("burn_level"),
                "firing on the boundary is only right because every argument is present"
            );
            LogAssert.NoUnexpectedReceived();
        }

        // --- Admission: more evidence for than against (issue #65, DR-7) ---
        //
        // Zeroing the miss penalty removed a filter that penalty was enforcing by accident:
        // a candidate missing more than it matched used to be dragged to <= 0 and discarded.
        // DR-7 states that as a rule instead. These three pin the harms the review cycle
        // reproduced when it was briefly absent — none is hypothetical, and all three were
        // measured against the real parser at both revisions before being written.

        [Test]
        public void Admission_FragmentCannotPreEmptRoundOne_SkippedWordChargeSurvives()
        {
            // The sharpest harm: a fragment that wins round 1 on EARLIEST START — which
            // IsBetterCandidate ranks above score — consumes the leading tokens, which moves
            // the origin issue #31 charges skipped words from. The genuine command then looks
            // like it started clean and scores a full 1.0.
            //
            // "alpha one" is a target value, so `approach target {target}` matches it with
            // 1 of 3 required elements (2 missed) and no longer sinks below zero on its own.
            // Under DR-7 it is refused admission, so "alpha one" stays chargeable preamble and
            // `weapons mode` scores 2 / (2 + 2) = 0.5 — under the gate, exactly as #31 intends.
            // Without DR-7 this fired mode_weapons at 1.00.
            var parser = new VoxrCommandParser(
                new[] { new VoxrSlotDefinition("target", new[] { "alpha one", "hotel one" }) },
                new[]
                {
                    new VoxrCommandDefinition(
                        "approach_target",
                        new[] { new[] { "approach", "target", "{target}" } }
                    ),
                    new VoxrCommandDefinition(
                        "mode_weapons",
                        new[] { new[] { "weapons", "mode" } }
                    ),
                }
            );

            var result = ParseOne(parser, "alpha one weapons mode");

            Assert.AreEqual("mode_weapons", result.Command.Intent);
            Assert.AreEqual(0.5f, result.Command.Score, 0.001f);
            Assert.Less(
                result.Command.Score,
                0.6f,
                "the skipped-word charge must survive — a fragment absorbing the preamble "
                    + "would hand this a clean 1.0 and fire a command nobody asked for"
            );
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Admission_FragmentCannotEvictARealCommandFromTheResultBuffer()
        {
            // The result buffer holds one slot per registered command and extraction stops
            // silently when it fills, so a fragment that takes a slot costs a real command.
            // Two commands, two slots: without DR-7 the leading "hard burn" fragment took the
            // first and `fire` — spoken and perfectly matched — was never stored.
            var parser = new VoxrCommandParser(
                BurnSlots(),
                new[]
                {
                    new VoxrCommandDefinition(
                        "set_burn",
                        new[] { new[] { "set", "burn", "to", "{burn_level}" } }
                    ),
                    new VoxrCommandDefinition("fire", new[] { new[] { "fire" } }),
                }
            );

            var results = parser.Parse("hard burn set burn to coast fire");

            Assert.AreEqual(2, results.Length, "both spoken commands must survive extraction");
            Assert.AreEqual("set_burn", results[0].Command.Intent);
            Assert.AreEqual("coast", results[0].Command.GetSlot("burn_level"));
            Assert.AreEqual(
                "fire",
                results[1].Command.Intent,
                "the second command must not be evicted by a leading fragment"
            );
            Assert.AreEqual(1f, results[1].Command.Score, 0.001f);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Admission_FragmentDoesNotBecomeAPartialMatchCandidate()
        {
            // The recogniser's partial-match branch is gated on Score > 0f, not on minScore,
            // so anything admitted here can arm a pending slot-fill and cancel one already in
            // flight. "close in" matches 2 of 5 required elements and misses 3, so DR-7 keeps
            // it out of the candidate set entirely and the question never arises.
            var parser = new VoxrCommandParser(
                new[] { new VoxrSlotDefinition("target", new[] { "alpha one", "hotel one" }) },
                new[]
                {
                    new VoxrCommandDefinition(
                        "approach_target",
                        new[] { new[] { "close", "in", "on", "target", "{target}" } }
                    ),
                }
            );

            Assert.AreEqual(
                0,
                parser.Parse("close in").Length,
                "a fragment missing more than it matched is not a candidate at all"
            );
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void MissedLiteral_DroppedDiscriminator_FiresTheFirstRegisteredSibling()
        {
            // The documented price of §5.1, found by the Phase 4 ablation and pinned here so
            // it is a known tested consequence rather than a surprise.
            //
            // Two siblings differing only in their last word — the shipped demo grammar's
            // shape (DemoGrammar.cs) — and the speaker says "switch to navigation" with
            // "navigation" dropped. The surviving evidence fits BOTH at (1 + 1 + 0) / 3 =
            // 0.667, so they tie on score, on consumed span and on literal count, and
            // registration order settles it: mode_weapons wins. Before §5.1 this scored 0.50,
            // fell under the default gate, and nothing fired at all.
            //
            // So the feature turns silence into a coin flip here. That is not a defect in the
            // change — the dropped word IS the discriminator, so no scorer can recover the
            // intent, and §5.1's whole premise is that a 3-element pattern missing one word
            // should clear the gate. It is the honest edge of that premise.
            //
            // Distinct from issue #70, which closed this shape at the EAGER gate: there the
            // speaker may still be mid-utterance and a tail rule is available. Here the
            // transcript is final, nothing more is coming, and no tail rule applies.
            var parser = new VoxrCommandParser(
                Array.Empty<VoxrSlotDefinition>(),
                new[]
                {
                    new VoxrCommandDefinition(
                        "mode_weapons",
                        new[] { new[] { "switch", "to", "weapons" } }
                    ),
                    new VoxrCommandDefinition(
                        "mode_navigation",
                        new[] { new[] { "switch", "to", "navigation" } }
                    ),
                }
            );

            var result = ParseOne(parser, "switch to");

            Assert.AreEqual(2f / 3f, result.Command.Score, 0.001f);
            Assert.GreaterOrEqual(
                result.Command.Score,
                0.6f,
                "this is the point: it now clears the default gate, where it did not before"
            );
            Assert.AreEqual(
                "mode_weapons",
                result.Command.Intent,
                "the tie falls to registration order, so the FIRST sibling wins regardless of "
                    + "which one the speaker meant"
            );
            LogAssert.NoUnexpectedReceived();
        }
    }
}
