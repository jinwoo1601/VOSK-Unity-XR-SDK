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

        // --- Coverage tables (issue #65 §5.2) ---
        //
        // The two tables and the pattern-start predicate, driven directly rather than
        // inferred from scores. They sit inside a triple-nested loop, so they are built once
        // per utterance and read as array indexes; testing them through Parse() alone would
        // leave the interesting cases (an orphan run stopped mid-utterance, an optional
        // leading element) reachable only by contrived grammars.

        [Test]
        public void OrphanRun_StopsAtATokenThatCouldBeginAPattern()
        {
            // The whole reason the count is a RUN and not a total. "launch" begins a pattern,
            // so cease_fire — which consumes through token 2 — is charged nothing for the
            // five tokens after it, and the multi-command utterance survives. Charging them
            // would take it from 2/2 to 2/7.
            //
            // Index 3 was 4 before issue #82 and is the one number the new terminator moves
            // here. The pair 3/4 is the whole point of the rule: at "missiles" the launch
            // pattern matches {weapon}, "target" and {target} against one miss, so a real
            // candidate begins there and the run stops; at "target" that same pattern matches
            // two and misses two, which is not more evidence for than against, so it does not.
            var parser = CreateParser();
            var tokens = "cease fire launch missiles target hotel one".Split(' ');

            parser.BuildCoverageTables(tokens);

            Assert.AreEqual(0, parser.OrphanedAfter(2), "\"launch\" begins a pattern");
            Assert.AreEqual(
                0,
                parser.OrphanedAfter(3),
                "\"missiles\": the launch pattern matches 3 required elements against 1 miss"
            );
            Assert.AreEqual(
                3,
                parser.OrphanedAfter(4),
                "\"target\": 2 matched against 2 missed is not a start — target, hotel, one"
            );
            Assert.AreEqual(
                2,
                parser.OrphanedAfter(5),
                "\"hotel\" alone fills no slot and no pattern reaches it: hotel, one"
            );
            Assert.AreEqual(1, parser.OrphanedAfter(6));
            Assert.AreEqual(0, parser.OrphanedAfter(7), "past the end");
        }

        [Test]
        public void OrphanRun_TreatsUnkAsTransparent_NotAsAStopper()
        {
            var parser = CreateParser();
            var tokens = "cease fire [unk] target".Split(' ');

            parser.BuildCoverageTables(tokens);

            Assert.AreEqual(
                1,
                parser.OrphanedAfter(2),
                "the [unk] is free but must not hide the \"target\" behind it"
            );
            Assert.AreEqual(parser.OrphanedAfter(3), parser.OrphanedAfter(2));
        }

        [Test]
        public void RecognisedPrefix_AgreesWithTheScanItReplaces()
        {
            // The leading term is the same quantity issue #31 already charges, computed by
            // subtraction instead of by walking. Pinned against the original scan so the
            // optimisation cannot drift from it.
            var parser = CreateParser();
            var tokens = "[unk] target [unk] disengage resume fire".Split(' ');

            parser.BuildCoverageTables(tokens);

            for (int from = 0; from <= tokens.Length; from++)
            {
                for (int to = from; to <= tokens.Length; to++)
                {
                    Assert.AreEqual(
                        VoxrCommandParser.CountRecognisedTokens(tokens, from, to),
                        parser.SkippedBefore(from, to),
                        $"range [{from}, {to})"
                    );
                }
            }
        }

        [Test]
        public void CanStartPattern_WalksPastLeadingOptionals_ToTheFirstRequiredElement()
        {
            // "First matchable element" is not "element zero". An omitted optional lets the
            // element behind it legitimately begin the match, so the walk continues through
            // optionals and stops at (and includes) the first required one.
            var parser = new VoxrCommandParser(
                new[]
                {
                    new VoxrSlotDefinition("weapon", new[] { "missiles" }),
                    new VoxrSlotDefinition("quantity", new[] { "two" }),
                },
                new[]
                {
                    new VoxrCommandDefinition(
                        "fire_weapon",
                        new[] { new[] { "?please", "fire", "{weapon}" } }
                    ),
                    new VoxrCommandDefinition(
                        "ready_weapon",
                        new[] { new[] { "{?quantity}", "{weapon}" } }
                    ),
                }
            );

            // No BuildCoverageTables call: CanStartPattern reads only the constructor-built
            // start caches and the token argument. It is what FILLS the tables, not a reader.
            var tokens = "please fire missiles two hotel".Split(' ');

            Assert.IsTrue(
                parser.CanStartPattern(tokens, 0),
                "the optional literal is stored stripped, so \"please\" can start a match"
            );
            Assert.IsTrue(parser.CanStartPattern(tokens, 1), "the required literal after it");
            Assert.IsTrue(
                parser.CanStartPattern(tokens, 2),
                "{weapon} is reachable first once {?quantity} is omitted"
            );
            Assert.IsTrue(parser.CanStartPattern(tokens, 3), "{?quantity} itself");
            Assert.IsFalse(parser.CanStartPattern(tokens, 4), "in no pattern at all");
        }

        [Test]
        public void CanStartPattern_NumberSequenceInitialPattern_MakesEveryDigitAStart()
        {
            // D4's degenerate case, pinned as tested behaviour rather than left as a
            // surprise: a slot-initial pattern over a permissive slot makes a large class of
            // tokens a potential start, which drives trailing orphan counts toward zero for
            // the whole grammar. That is SAFE — it reverts to pre-coverage behaviour — but it
            // silently weakens the feature, and an author has no way to see it.
            var parser = new VoxrCommandParser(
                new[] { VoxrSlotDefinition.NumberSequence("heading", minWords: 2, maxWords: 3) },
                new[]
                {
                    new VoxrCommandDefinition("set_heading", new[] { new[] { "{heading}" } }),
                    new VoxrCommandDefinition("halt", new[] { new[] { "halt" } }),
                }
            );

            var tokens = "halt two seven banana".Split(' ');

            Assert.IsTrue(parser.CanStartPattern(tokens, 0), "literal-initial pattern");
            Assert.IsTrue(parser.CanStartPattern(tokens, 1), "two digits available, minWords 2");
            Assert.IsFalse(
                parser.CanStartPattern(tokens, 2),
                "only one digit left, which cannot satisfy minWords 2"
            );
            Assert.IsFalse(parser.CanStartPattern(tokens, 3));
        }

        // --- The orphan run's terminator asks the matcher (issue #82) ---
        //
        // CanStartPattern answers "could a pattern's first matchable element match here?".
        // The matcher answers a wider question, because it tries every pattern at every
        // index and so begins patterns whose leading elements were dropped. IsAdmissibleStart
        // closes that gap; these pin both halves of it — that it opens the case it was
        // written for, and that it does NOT open the case the design refused.

        [Test]
        public void IsAdmissibleStart_OpensAPositionOnlyReachableByMissingLeadingElements()
        {
            var parser = new VoxrCommandParser(
                new[] { new VoxrSlotDefinition("target", new[] { "hotel one" }) },
                new[]
                {
                    new VoxrCommandDefinition("cease_fire", new[] { new[] { "cease", "fire" } }),
                    new VoxrCommandDefinition(
                        "approach_target",
                        new[] { new[] { "approach", "target", "{target}" } }
                    ),
                }
            );

            var tokens = "cease fire target hotel one".Split(' ');

            Assert.IsFalse(
                parser.CanStartPattern(tokens, 2),
                "no pattern's FIRST element is \"target\""
            );
            Assert.IsTrue(
                parser.IsAdmissibleStart(tokens, 2),
                "but approach target {target} matches there, missing only \"approach\""
            );
        }

        [Test]
        public void IsAdmissibleStart_RefusesAPatternReachedOnlyByMissingMoreThanItMatches()
        {
            // The anti-collapse guard, and the reason this is an ADMISSIBILITY probe rather
            // than a widening. "hard burn" is a slot value, so a crude "could any pattern
            // match anything here" test would call index 1 a start, charge the bare pattern
            // nothing, and hand the utterance straight back to it — un-fixing #42 grammar-wide.
            // Reaching {burn_level} from here costs this pattern both its literals, so it has
            // more evidence against it than for it and is no more a start here than it is a
            // candidate there.
            var parser = new VoxrCommandParser(
                new[] { new VoxrSlotDefinition("burn_level", new[] { "hard burn" }) },
                new[]
                {
                    new VoxrCommandDefinition("decelerate", new[] { new[] { "decelerate" } }),
                    new VoxrCommandDefinition(
                        "decelerate_by",
                        new[] { new[] { "decelerate", "by", "{burn_level}" } }
                    ),
                }
            );

            var tokens = "decelerate hard burn".Split(' ');

            Assert.IsFalse(parser.CanStartPattern(tokens, 1));
            Assert.IsFalse(
                parser.IsAdmissibleStart(tokens, 1),
                "reaching {burn_level} from here misses both required literals"
            );

            parser.BuildCoverageTables(tokens);
            Assert.AreEqual(2, parser.OrphanedAfter(1), "\"hard burn\" is still charged");

            // And end-to-end: the #42 inversion still holds at the default weight.
            var result = ParseOne(parser, "decelerate hard burn");
            Assert.AreEqual("decelerate_by", result.Command.Intent);
            Assert.AreEqual("hard burn", result.Command.GetSlot("burn_level"));
        }

        [Test]
        public void IsAdmissibleStart_RefusesAPatternWithAsMuchMissedAsMatched()
        {
            // The case the test above does NOT cover, and the one that caught a real
            // regression in review. The guard cannot be DR-7's own `missed <= matched`: it has
            // to be strictly `missed < matched`.
            //
            // This is the pair `command-recognition.md` prescribes as the SAFE remedy for the
            // #42 hazard — the required "by" replaced with an optional "?by". Probed from
            // "hard", `decelerate ?by {burn_level}` misses one required element ("decelerate")
            // and matches one ({burn_level}); the optional counts toward neither side. Under
            // `missed <= matched` that is admissible, the stranded value terminates the BARE
            // pattern's own orphan run, and the bare command wins at 1/(1+0) = 1.00 over
            // 2/(2+1) = 0.67 — firing with no argument at all, which is #42 restored on the
            // grammar the docs recommend.
            //
            // A trailing token is required to expose it: without one the slot-filled pattern
            // consumes to the end and wins on span regardless.
            var parser = new VoxrCommandParser(
                new[] { new VoxrSlotDefinition("burn_level", new[] { "hard burn" }) },
                new[]
                {
                    new VoxrCommandDefinition("decelerate", new[] { new[] { "decelerate" } }),
                    new VoxrCommandDefinition(
                        "decelerate_by",
                        new[] { new[] { "decelerate", "?by", "{burn_level}" } }
                    ),
                }
            );

            var tokens = "decelerate hard burn please".Split(' ');

            Assert.IsFalse(
                parser.IsAdmissibleStart(tokens, 1),
                "one matched against one missed is not more evidence for than against"
            );

            parser.BuildCoverageTables(tokens);
            Assert.AreEqual(3, parser.OrphanedAfter(1), "hard, burn, please");

            var result = ParseOne(parser, "decelerate hard burn please");
            Assert.AreEqual("decelerate_by", result.Command.Intent, "not the bare pattern");
            Assert.AreEqual("hard burn", result.Command.GetSlot("burn_level"));
        }

        [Test]
        public void IsAdmissibleStart_SkipsTheProbeWhenCoverageIsDisabled()
        {
            // At coverageWeight 0 every orphan count is multiplied by zero, so the probe sweep
            // cannot reach any score and is pure waste — and on the eager path that waste is
            // per partial result, not per utterance. Both halves are pinned here because the
            // short-circuit makes the TABLE weight-dependent even though behaviour is not.
            var tokens = "cease fire launch missiles target hotel one".Split(' ');

            var weighted = CreateParser();
            weighted.BuildCoverageTables(tokens);
            Assert.AreEqual(
                0,
                weighted.OrphanedAfter(3),
                "at the default weight the probe admits \"missiles\""
            );

            var unweighted = new VoxrCommandParser(MakeSlots(), MakeCommands(), 0f);
            unweighted.BuildCoverageTables(tokens);
            Assert.IsFalse(unweighted.IsAdmissibleStart(tokens, 3), "the sweep is skipped");
            Assert.AreEqual(
                4,
                unweighted.OrphanedAfter(3),
                "so the table falls back to CanStartPattern's narrower answer"
            );

            // ...and none of that is observable through scoring, which is the point.
            var weightedResults = weighted.Parse("cease fire launch missiles target hotel one");
            var unweightedResults = unweighted.Parse("cease fire launch missiles target hotel one");

            Assert.AreEqual(weightedResults.Length, unweightedResults.Length);
            for (int i = 0; i < weightedResults.Length; i++)
            {
                Assert.AreEqual(
                    weightedResults[i].Command.Intent,
                    unweightedResults[i].Command.Intent
                );
                Assert.AreEqual(
                    weightedResults[i].Command.Score,
                    unweightedResults[i].Command.Score,
                    0.001f
                );
            }
        }

        [Test]
        public void NumberSequenceProbe_DoesNotLeakItsPlaceholderIntoARealMatch()
        {
            // The probe walks whole patterns, so it matches NumberSequence slots whose joined
            // value it then throws away. It therefore asks TryMatchNumberSequence to skip
            // building that string — the allocation CanStartPattern avoids by construction.
            // The stand-in it gets back must never survive into a parsed command.
            var parser = new VoxrCommandParser(
                new[] { VoxrSlotDefinition.NumberSequence("heading", minWords: 1, maxWords: 3) },
                new[]
                {
                    new VoxrCommandDefinition(
                        "set_heading",
                        new[] { new[] { "orient", "heading", "{heading}" } }
                    ),
                    new VoxrCommandDefinition("halt", new[] { new[] { "halt" } }),
                }
            );

            var tokens = "orient heading two seven zero".Split(' ');

            // Index 2 is where the probe runs the number matcher: no pattern starts with the
            // slot, so the cheap test declines and the sweep walks set_heading from "two".
            Assert.IsFalse(parser.IsAdmissibleStart(tokens, 2));

            var result = ParseOne(parser, "orient heading two seven zero");
            Assert.AreEqual("two seven zero", result.Command.GetSlot("heading"));

            // And again where a preceding command makes the probe run before the real match.
            var chained = parser.Parse("halt orient heading two seven zero");
            Assert.AreEqual(2, chained.Length);
            Assert.AreEqual("two seven zero", chained[1].Command.GetSlot("heading"));
        }

        [Test]
        public void IsAdmissibleStart_LeavesOneAnchoringLiteralEnoughToTameAPermissiveSlot()
        {
            // KNOWN_LIMITATIONS.md tells authors that a slot-initial pattern over an
            // open-ended slot weakens trailing coverage grammar-wide, and that the remedy is
            // to "anchor them behind a literal". Under `missed <= matched` that remedy stopped
            // working — ["heading", "{heading}"] probed from any digit misses one and matches
            // one — so the limitation silently widened from slot-initial to slot-second and
            // the documented workaround bought nothing.
            var parser = new VoxrCommandParser(
                new[] { VoxrSlotDefinition.NumberSequence("heading", minWords: 1, maxWords: 3) },
                new[]
                {
                    new VoxrCommandDefinition(
                        "set_heading",
                        new[] { new[] { "heading", "{heading}" } }
                    ),
                    new VoxrCommandDefinition("halt", new[] { new[] { "halt" } }),
                }
            );

            var tokens = "halt two seven zero".Split(' ');

            Assert.IsFalse(parser.CanStartPattern(tokens, 1));
            Assert.IsFalse(parser.IsAdmissibleStart(tokens, 1), "the literal still anchors it");

            parser.BuildCoverageTables(tokens);
            Assert.AreEqual(3, parser.OrphanedAfter(1), "the digits are still charged");
        }

        [Test]
        public void IsAdmissibleStart_NeverOverrulesCanStartPattern()
        {
            // F11's protected property, in the form this method could have broken it: the
            // probe only ever ADDS claims. A pattern that begins at a token keeps terminating
            // the run there whatever DR-7 would say about the candidate anchored on it, so
            // coverage never becomes a function of another candidate's verdict.
            var parser = CreateParser();

            foreach (
                var text in new[]
                {
                    "cease fire launch missiles target hotel one",
                    "target disengage please",
                    "shoot missiles target hotel one",
                    "close distance cqb target alpha three",
                }
            )
            {
                var tokens = text.Split(' ');
                for (int i = 0; i < tokens.Length; i++)
                {
                    if (parser.CanStartPattern(tokens, i))
                    {
                        Assert.IsTrue(
                            parser.IsAdmissibleStart(tokens, i),
                            $"\"{text}\" token {i} (\"{tokens[i]}\")"
                        );
                    }
                }
            }
        }

        // --- [unk] handling on the trailing side (issue #65 §5.2) ---
        //
        // Written before the trailing coverage term existed, and deliberately kept in that
        // form. Both tests are stated as EQUALITIES rather than values, which is what let
        // them stand on both sides of the change: before, every score here was 1.0 because
        // nothing after a match was charged; now the charged pairs move together. What they
        // discriminate is an implementation that gets [unk] wrong — invisible either way if
        // the assertion is a bare number.

        [Test]
        public void TrailingUnk_DoesNotTerminateTheOrphanRun()
        {
            // [unk] is the decoder's marker for a token it could not place in the grammar,
            // so no pattern element can equal it. It is free — but it must also be
            // TRANSPARENT: if it stopped the scan of what a match left unexplained, a single
            // noise token would shield every real leftover behind it. "X [unk] Y" therefore
            // has to cost exactly what "X Y" costs.
            //
            // "target" is the orphan here: it appears in the grammar (as the required
            // literal in "launch ... target {target}") but begins no pattern, and every
            // candidate anchored on it scores <= 0, so it is left over rather than becoming
            // a second command.
            var parser = CreateParser();

            var clean = ParseOne(parser, "cease fire target");
            var noisy = ParseOne(parser, "cease fire [unk] target");

            Assert.AreEqual("cease_fire", clean.Command.Intent);
            Assert.AreEqual("cease_fire", noisy.Command.Intent);
            Assert.AreEqual(
                clean.Command.Score,
                noisy.Command.Score,
                0.001f,
                "an [unk] sitting between the match and a leftover token must not change the charge"
            );
        }

        [Test]
        public void UnkFlankingTheMatch_IsFreeOnBothSides()
        {
            // The leading half is issue #31's existing rule (CountRecognisedTokens skips
            // [unk]); the trailing half is the new side. Pinned together so the two stay
            // symmetric — one weight, one rule, not two mechanisms.
            var parser = CreateParser();

            var bare = ParseOne(parser, "cease fire");
            var flanked = ParseOne(parser, "[unk] [unk] cease fire [unk]");

            Assert.AreEqual(1.0f, bare.Command.Score, 0.001f);
            Assert.AreEqual(
                1.0f,
                flanked.Command.Score,
                0.001f,
                "out-of-grammar filler before and after a complete match costs nothing"
            );
        }

        // "fire {?mode}" leaves EndIdx past a trailing [unk] it never consumed — the skip
        // loop runs before EVERY element, including a trailing optional that then matches
        // nothing — while ConsumedEndIdx stays at the last token actually matched. "now" is
        // in the grammar as the tail literal of the track pattern, so it begins no pattern
        // and every candidate anchored on it scores <= 0.
        static VoxrCommandParser CreateTrailingOptionalParser()
        {
            return new VoxrCommandParser(
                new[]
                {
                    new VoxrSlotDefinition("mode", new[] { "silent" }),
                    new VoxrSlotDefinition("target", new[] { "hotel one" }),
                },
                new[]
                {
                    new VoxrCommandDefinition("fire", new[] { new[] { "fire", "{?mode}" } }),
                    new VoxrCommandDefinition(
                        "track",
                        new[] { new[] { "track", "{target}", "now" } }
                    ),
                }
            );
        }

        [Test]
        public void TrailingUnk_AbsorbedIntoEndIdx_ShedsNoLeftover()
        {
            // A pattern must not be able to buy itself a cheaper score by swallowing noise:
            // what it left unexplained is measured from the last token it actually MATCHED,
            // not from wherever the [unk] skip happened to leave the cursor.
            //
            // Note what this can and cannot catch. Because [unk] is transparent (the test
            // above), the count from ConsumedEndIdx and the count from EndIdx are provably
            // equal whenever the gap between them is all-[unk] — which it always is, since
            // the only thing that advances the cursor without recording a match is that skip
            // loop. So this pins the CONJUNCTION: it fails if [unk] ever stops being
            // transparent while the origin stays at ConsumedEndIdx, which is the shape that
            // would let absorption pay.
            var parser = CreateTrailingOptionalParser();

            var clean = ParseOne(parser, "fire now");
            var absorbing = ParseOne(parser, "fire [unk] now");

            Assert.AreEqual("fire", clean.Command.Intent);
            Assert.AreEqual("fire", absorbing.Command.Intent);
            Assert.AreEqual(
                clean.Command.Score,
                absorbing.Command.Score,
                0.001f,
                "trailing [unk] the pattern never matched must not reduce what it is charged for"
            );
        }

        [Test]
        public void TrailingOptional_MatchesPastUnk_ProvingTheEndIdxGapIsReal()
        {
            // Guards the fixture above rather than the feature: it shows {?mode} really is
            // evaluated at the token AFTER the [unk], which is why "fire [unk] now" leaves
            // EndIdx one past ConsumedEndIdx. Without this, a change to the skip loop could
            // silently turn the previous test into a comparison of two identical shapes.
            var parser = CreateTrailingOptionalParser();

            var absorbed = ParseOne(parser, "fire [unk] silent");

            Assert.AreEqual("fire", absorbed.Command.Intent);
            Assert.AreEqual("silent", absorbed.Command.GetSlot("mode"));
            Assert.AreEqual(1.0f, absorbed.Command.Score, 0.001f);
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
        public void SpanTieBreak_LongerSpanPatternNowWinsOnScore()
        {
            // Span sits ABOVE literal count, so it also settles equal-score candidates whose
            // literal counts differ — outcomes literal count used to decide on its own,
            // deterministically, in either declaration order.
            //
            // NO LONGER A TIE, since issue #65 §5.2. "one" is left unexplained by the
            // 3-literal pattern, so it scores 3/(3+1) = 0.75 against the slot pattern's
            // 3/3 = 1.0 and loses on score before span is ever consulted. The assertion below
            // still passes and now proves nothing about the span key — see
            // SpanTieBreak_StillDecidesAGenuineScoreTie for the version that does.
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
                "the slot pattern wins on score now — the literal one strands \"one\""
            );
            Assert.AreEqual("hotel one", result.Command.GetSlot("target"));
        }

        [Test]
        public void SpanTieBreak_LongerSpanCommandNowWinsOnScore_AcrossCommands()
        {
            // The comparison runs across the whole command list, so a span tie changes which
            // *intent* fires, not merely which pattern index within one command.
            //
            // NO LONGER A TIE either, for the same reason: go_dir strands "pole" and scores
            // 2/(2+1) = 0.667 against go_place's 2/2 = 1.0. Kept because the cross-command
            // outcome is still worth pinning, but the span key is no longer what produces it.
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
                "go_place wins outright on score — go_dir is charged for the \"pole\" it strands"
            );
            Assert.AreEqual("north pole", result.Command.GetSlot("place"));
        }

        [Test]
        public void SpanTieBreak_StillDecidesAGenuineScoreTie()
        {
            // F10: the consumed-span key (issue #41) demotes to a real tie-break now that the
            // score carries coverage — preserved, not superseded. The two tests above used to
            // be its coverage and are now decided on score, so this restores a case where the
            // scores are genuinely equal and span is what breaks them.
            //
            // The trick is that a tie needs both candidates to leave nothing chargeable
            // behind. Registering "one niner" makes "one" a token that could begin a pattern,
            // so the orphan run after the shorter match is empty and both candidates reach a
            // full 1.0. That is established by parsing each pattern ALONE below rather than
            // being assumed — if either stopped reaching 1.0 the tie would evaporate and the
            // combined assertion would go vacuous again without anything failing.
            //
            // Span sits above literal count, so the 2-literal/4-token pattern must beat the
            // 3-literal/3-token one. Remove the span key and literal count would flip it.
            var target = new VoxrSlotDefinition("target", new[] { "hotel one" });
            var counting = new VoxrCommandDefinition(
                "count_off",
                new[] { new[] { "one", "niner" } }
            );

            var literalOnly = new VoxrCommandParser(
                new[] { target },
                new[]
                {
                    new VoxrCommandDefinition("fire_at", new[] { new[] { "fire", "at", "hotel" } }),
                    counting,
                }
            );
            var slotted = new VoxrCommandParser(
                new[] { target },
                new[]
                {
                    new VoxrCommandDefinition(
                        "fire_at",
                        new[] { new[] { "fire", "at", "{target}" } }
                    ),
                    counting,
                }
            );

            Assert.AreEqual(
                1.0f,
                literalOnly.Parse("fire at hotel one")[0].Command.Score,
                0.001f,
                "the shorter pattern is charged nothing — \"one\" could begin another pattern"
            );
            Assert.AreEqual(
                1.0f,
                slotted.Parse("fire at hotel one")[0].Command.Score,
                0.001f,
                "and the longer one explains the whole utterance"
            );

            var both = new VoxrCommandParser(
                new[] { target },
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
                    counting,
                }
            );

            var result = ParseOne(both, "fire at hotel one");

            Assert.AreEqual(
                1,
                result.Command.MatchedPatternIndex,
                "equal scores, so the longer consumed span decides — over the higher literal count"
            );
            Assert.AreEqual("hotel one", result.Command.GetSlot("target"));
        }

        [Test]
        public void SpanTieBreak_GenuineTie_AlsoDecidesBetweenCommands()
        {
            // The same restoration across two intents rather than two patterns of one, since
            // that is the half SpanTieBreak_ChoosesBetweenCommands_NotJustPatterns used to
            // cover. "pole star" makes "pole" a possible start, so go_dir keeps its 1.0.
            var slots = new[]
            {
                new VoxrSlotDefinition("dir", new[] { "north" }),
                new VoxrSlotDefinition("place", new[] { "north pole" }),
            };
            var poleStar = new VoxrCommandDefinition(
                "pole_star",
                new[] { new[] { "pole", "star" } }
            );

            var dirOnly = new VoxrCommandParser(
                slots,
                new[]
                {
                    new VoxrCommandDefinition("go_dir", new[] { new[] { "go", "{dir}" } }),
                    poleStar,
                }
            );

            Assert.AreEqual(
                1.0f,
                dirOnly.Parse("go north pole")[0].Command.Score,
                0.001f,
                "go_dir strands nothing chargeable, so the tie is real"
            );

            var both = new VoxrCommandParser(
                slots,
                new[]
                {
                    new VoxrCommandDefinition("go_dir", new[] { new[] { "go", "{dir}" } }),
                    new VoxrCommandDefinition("go_place", new[] { new[] { "go", "{place}" } }),
                    poleStar,
                }
            );

            var result = ParseOne(both, "go north pole");

            Assert.AreEqual(
                "go_place",
                result.Command.Intent,
                "the longer-span command wins the tie even though it is declared second"
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
        public void RequiredLiteralDropped_SlotFilledPatternNowWins()
        {
            // The behaviour the warning names, INVERTED by issue #65 §5.2 — the same fix as
            // the two cross-intent cases above, reached here within a single intent, so the
            // sibling that wins is a pattern index rather than a different command.
            //
            // VOSK drops short unstressed function words more than any other token. When "by"
            // goes, the slot-filled pattern is still charged for the miss — 2 / 3 = 0.667 —
            // but the bare pattern is now charged for the two tokens it cannot explain:
            // 1 / (1 + 2) = 0.333. The burn level survives.
            LogAssert.Expect(UnityEngine.LogType.Warning, new Regex("required literal \"by\""));
            var parser = new VoxrCommandParser(BurnSlots(), DecelerateCommands("by"));

            var result = ParseOne(parser, "decelerate hard burn");

            Assert.AreEqual(
                1,
                result.Command.MatchedPatternIndex,
                "the slot-filled pattern wins on coverage despite the dropped literal"
            );
            Assert.AreEqual(
                "hard burn",
                result.Command.GetSlot("burn_level"),
                "the spoken burn level reaches the handler instead of being discarded"
            );
            Assert.AreEqual(2f / 3f, result.Command.Score, 0.001f);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void OptionalLiteralBeforeSlot_KeepsTheSlotWhenTheLiteralIsDropped()
        {
            // The remedy the warning recommends: with the literal optional the slot-filled
            // pattern scores 1.0 whether or not the word was spoken.
            //
            // It used to win by taking the consumed-span tie-break (issue #41) over a bare
            // form that also scored 1.0. Since issue #65 §5.2 it wins outright on score —
            // the bare form is charged for the burn level it cannot explain (1/(1+2) = 0.333)
            // — so span is no longer consulted here. The remedy still earns its place: it
            // reaches 1.0 rather than 2/3, and it is the only thing that fixes the residual
            // case where the stranded value's first word could itself begin a pattern.
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
            // bare pattern's 1.0, so the elevation went the same way the burn level did.
            //
            // Issue #65 §5.2 is what finally reverses that comparison — the bare pattern is
            // now charged 1/(1+2) = 0.333 for the two tokens it strands — but the detector
            // still fires, because the hazard survives wherever the stranded value's first
            // word could begin some other pattern. This test only checks that the shape is
            // reported at construction, which is unchanged either way.
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

            // INVERTED by issue #65 §5.2, and the arithmetic is the argument rather than the
            // emitted output. Bare "decelerate" explains one of three spoken tokens, so it is
            // charged for the two it leaves orphaned: 1 / (1 + 2) = 0.333. The longer pattern
            // drops "by" — credit 1 for "decelerate" and 1 for the slot, nothing for the
            // missed literal, over a denominator of 3 — and leaves nothing unexplained:
            // 2 / (3 + 0) = 0.667. A perfect match of too little now loses to an imperfect
            // match of everything, which is the whole of issue #42.
            Assert.AreEqual(
                "decelerate_by",
                result.Command.Intent,
                "coverage demotes the bare form below its slot-filled sibling"
            );
            Assert.AreEqual("hard burn", result.Command.GetSlot("burn_level"));
            Assert.AreEqual(2f / 3f, result.Command.Score, 0.001f);
            Assert.Greater(
                result.Command.Score,
                0.6f,
                "and it clears the default minScore, so the command actually fires"
            );
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

            // INVERTED for the same reason. "fire {?quantity} {weapon}" matches perfectly
            // across two of the four tokens: 2 / (2 + 2) = 0.5. "fire {weapon} at {target}"
            // drops only the "at" and accounts for everything: 3 / (4 + 0) = 0.75. The bare
            // form's perfect 1.0 no longer protects it, because what coverage measures is
            // how much of the utterance the pattern explains, not how neatly it matched the
            // part it chose.
            Assert.AreEqual(
                1,
                result.Command.MatchedPatternIndex,
                "the longer pattern wins and the spoken target survives"
            );
            Assert.AreEqual("hotel one", result.Command.GetSlot("target"));
            Assert.AreEqual(0.75f, result.Command.Score, 0.001f);
            LogAssert.NoUnexpectedReceived();
        }

        // --- Coverage inside selection (issue #65 §5.2) ---
        //
        // The two tests above are the fix itself, inverted in place. These are the
        // consequences of it — the guard that must NOT move, the knob that turns it off, the
        // hazard that survives it, and the change in behaviour it buys at a price.

        [Test]
        public void Coverage_SequentialExtraction_ChargesNothingForALaterCommand()
        {
            // The fork-F5 guard, and the reason the trailing count is a RUN that stops at the
            // first token which could begin a pattern rather than a total of everything left
            // over. "launch" begins one, so cease_fire is charged nothing and keeps 2/2 =
            // 1.00. Charging every trailing token instead would give it 2/7 = 0.29, sink it
            // below minScore, and destroy multi-command utterances outright — which is
            // exactly why design §6 rejected that form.
            var parser = CreateParser();

            var results = parser.Parse("cease fire launch missiles target hotel one");

            Assert.AreEqual(2, results.Length);
            Assert.AreEqual("cease_fire", results[0].Command.Intent);
            Assert.AreEqual(1.0f, results[0].Command.Score, 0.001f);
            Assert.AreEqual("launch_weapon", results[1].Command.Intent);
            Assert.AreEqual(1.0f, results[1].Command.Score, 0.001f);
            Assert.AreEqual("hotel one", results[1].Command.GetSlot("target"));
        }

        // --- Numbers Documentation~/scoring.md publishes (issue #83) ---
        //
        // The page traces worked examples end to end and quotes exact scores. Nothing pinned
        // them before: the winner's score reaches VoxrCommand.Score, but a worked example
        // also walks the LOSING candidates, and TryMatchScored is private. So a scorer change
        // could leave this suite green while silently falsifying the reference — which is the
        // drift issue #83 was filed to clean up in the first place. These tests exist so the
        // page fails a test rather than a reader.

        [Test]
        public void Coverage_SequentialExtraction_SparesTheFirstCommandWhenASecondLosesItsWord()
        {
            // The other half of the test above, and scoring.md §7 D. The orphan run stops
            // wherever the MATCHER could begin, and the matcher begins a pattern whose leading
            // elements were dropped: nothing STARTS with "target", but
            // `approach target {target}` matches from it, missing one required element against
            // two matched, so the run terminates there and cease_fire keeps 2/2 = 1.00.
            //
            // Until issue #82 the run tested only what patterns start with, so cease_fire was
            // charged 2/(2+3) = 0.40 for three tokens the very next extraction round explained
            // — the command spoken cleanly was the one rejected while the damaged one fired.
            var slots = new[] { new VoxrSlotDefinition("target", new[] { "hotel one" }) };
            var commands = new[]
            {
                new VoxrCommandDefinition("cease_fire", new[] { new[] { "cease", "fire" } }),
                new VoxrCommandDefinition(
                    "approach_target",
                    new[] { new[] { "approach", "target", "{target}" } }
                ),
            };
            var parser = new VoxrCommandParser(slots, commands);

            var results = parser.Parse("cease fire target hotel one");

            Assert.AreEqual(2, results.Length);
            Assert.AreEqual("cease_fire", results[0].Command.Intent);
            Assert.AreEqual(
                1.0f,
                results[0].Command.Score,
                0.001f,
                "not charged: approach target {target} is matchable from \"target\""
            );
            Assert.AreEqual("approach_target", results[1].Command.Intent);
            Assert.AreEqual(2f / 3f, results[1].Command.Score, 0.001f);
            Assert.AreEqual("hotel one", results[1].Command.GetSlot("target"));

            // Control: spoken in full, "approach" terminates the run and both keep 1.00.
            var control = parser.Parse("cease fire approach target hotel one");

            Assert.AreEqual(2, control.Length);
            Assert.AreEqual(1.0f, control[0].Command.Score, 0.001f);
            Assert.AreEqual(1.0f, control[1].Command.Score, 0.001f);
        }

        [Test]
        public void Coverage_LosingCandidateTerms_MatchWhatTheWorkedExamplesPublish()
        {
            // scoring.md §7 A publishes two losing-candidate scores for
            // "launch missiles target hotel one" — start 1 at 3/(4+1) = 0.60 and start 2 at
            // 1/(4+2) = 0.17 — and §3 publishes the bare intercept form at 3/(3+2) = 0.60.
            // None is reachable through VoxrCommand.Score, so the coverage TERMS they rest on
            // are pinned directly through the same probes the admission test uses. The
            // fidelity halves are fixed by the element tables above.
            var launchParser = new VoxrCommandParser(
                new[]
                {
                    new VoxrSlotDefinition("weapon", new[] { "missiles" }),
                    new VoxrSlotDefinition("target", new[] { "hotel one" }),
                },
                new[]
                {
                    new VoxrCommandDefinition(
                        "launch_weapon",
                        new[] { new[] { "launch", "{weapon}", "target", "{target}" } }
                    ),
                }
            );
            var launchTokens = new[] { "launch", "missiles", "target", "hotel", "one" };
            launchParser.BuildCoverageTables(launchTokens);

            Assert.AreEqual(1, launchParser.SkippedBefore(0, 1), "§7 A start 1 skips \"launch\"");
            Assert.AreEqual(2, launchParser.SkippedBefore(0, 2), "§7 A start 2 skips two tokens");
            Assert.AreEqual(0, launchParser.OrphanedAfter(5), "the winner consumes to the end");
            Assert.AreEqual(
                1.0f,
                launchParser.Parse("launch missiles target hotel one")[0].Command.Score,
                0.001f
            );

            // §3: the bare form abandons "hard burn", and nothing in THIS grammar begins a
            // match there — which is what moves the #41 pair from a span tie-break to a score
            // difference. Where a standalone command does begin on the tail, coverage charges
            // nothing and the span key still decides; that case is the SpanTieBreak_* tests.
            var interceptParser = new VoxrCommandParser(
                new[]
                {
                    new VoxrSlotDefinition("track", new[] { "hotel one" }),
                    new VoxrSlotDefinition("burn_level", new[] { "hard burn" }),
                },
                new[]
                {
                    new VoxrCommandDefinition(
                        "intercept",
                        new[]
                        {
                            new[] { "intercept", "track", "{track}" },
                            new[] { "intercept", "track", "{track}", "{burn_level}" },
                        }
                    ),
                }
            );
            var interceptTokens = new[] { "intercept", "track", "hotel", "one", "hard", "burn" };
            interceptParser.BuildCoverageTables(interceptTokens);

            Assert.AreEqual(
                2,
                interceptParser.OrphanedAfter(4),
                "\"hard burn\" begins no pattern here, so the bare form pays for both tokens"
            );

            var interceptResults = interceptParser.Parse("intercept track hotel one hard burn");

            Assert.AreEqual(1, interceptResults.Length, "one command, not a split order");
            Assert.AreEqual(1.0f, interceptResults[0].Command.Score, 0.001f);
            Assert.AreEqual("hard burn", interceptResults[0].Command.GetSlot("burn_level"));
        }

        [Test]
        public void Coverage_LeavesTheStrandedArgumentHazard_WhenTheValueBeginsAPattern()
        {
            // scoring.md §7 B and command-recognition.md both state that coverage closes the
            // #42 discarded-argument hazard only in its COMMON case. The residue: the orphan
            // run terminates at the first token that could begin a match, so when the stranded
            // value's own first word begins one, the bare candidate is charged nothing and
            // strands the value exactly as it did before #65 — at the DEFAULT weight. This is
            // why WarnOnDroppableRequiredLiteral was not narrowed when coverage shipped, and
            // the docs now say so, so the claim needs a pin.
            var slots = new[] { new VoxrSlotDefinition("burn_level", new[] { "hard burn" }) };
            var bare = new[] { "decelerate" };
            var filled = new[] { "decelerate", "by", "{burn_level}" };

            LogAssert.Expect(UnityEngine.LogType.Warning, new Regex("required literal \"by\""));
            var control = new VoxrCommandParser(
                slots,
                new[] { new VoxrCommandDefinition("decelerate", new[] { bare, filled }) }
            );

            var controlResults = control.Parse("decelerate hard burn");

            Assert.AreEqual(2f / 3f, controlResults[0].Command.Score, 0.001f);
            Assert.AreEqual("hard burn", controlResults[0].Command.GetSlot("burn_level"));

            // Register anything that starts on "hard" and the charge disappears.
            LogAssert.Expect(UnityEngine.LogType.Warning, new Regex("required literal \"by\""));
            var withHardStop = new VoxrCommandParser(
                slots,
                new[]
                {
                    new VoxrCommandDefinition("decelerate", new[] { bare, filled }),
                    new VoxrCommandDefinition("hard_stop", new[] { new[] { "hard", "stop" } }),
                }
            );

            var residual = withHardStop.Parse("decelerate hard burn");

            Assert.AreEqual("decelerate", residual[0].Command.Intent);
            Assert.AreEqual(
                1.0f,
                residual[0].Command.Score,
                0.001f,
                "the bare form pays nothing, so it wins exactly as it did before #65"
            );
            Assert.IsFalse(
                residual[0].Command.HasSlot("burn_level"),
                "and the spoken burn level is discarded — the #42 hazard in full"
            );

            // The remedy still reaches the residue, which is why the warning stands.
            var optional = new VoxrCommandParser(
                slots,
                new[]
                {
                    new VoxrCommandDefinition(
                        "decelerate",
                        new[] { bare, new[] { "decelerate", "?by", "{burn_level}" } }
                    ),
                    new VoxrCommandDefinition("hard_stop", new[] { new[] { "hard", "stop" } }),
                }
            );

            var fixedResults = optional.Parse("decelerate hard burn");

            Assert.AreEqual(1.0f, fixedResults[0].Command.Score, 0.001f);
            Assert.AreEqual("hard burn", fixedResults[0].Command.GetSlot("burn_level"));
        }

        [Test]
        public void Coverage_ALeadingOptionalLiteral_BecomesAPatternStartForTheWholeGrammar()
        {
            // The start-set walk continues past a pattern's leading OPTIONAL elements and
            // stops at the first required one, because an omitted optional lets the element
            // behind it legitimately begin the match. So ["?please","fire"] puts BOTH words
            // into one grammar-wide set, and a stray "please" then terminates the orphan run
            // for every candidate — including commands that never mentioned it. scoring.md §2
            // lists this as the third consequence of the conservative start test.
            var disengage = new VoxrCommandDefinition(
                "cease_fire",
                new[] { new[] { "disengage" } }
            );

            var withoutOptional = new VoxrCommandParser(
                Array.Empty<VoxrSlotDefinition>(),
                new[] { disengage, new VoxrCommandDefinition("fire", new[] { new[] { "fire" } }) }
            );

            Assert.AreEqual(
                1f / 3f,
                withoutOptional.Parse("disengage please now")[0].Command.Score,
                0.001f,
                "\"please now\" begins nothing, so both tokens are charged"
            );

            var withOptional = new VoxrCommandParser(
                Array.Empty<VoxrSlotDefinition>(),
                new[]
                {
                    disengage,
                    new VoxrCommandDefinition("fire", new[] { new[] { "?please", "fire" } }),
                }
            );

            Assert.IsTrue(
                withOptional.CanStartPattern(new[] { "please" }, 0),
                "the optional's stripped form joins the start set"
            );
            Assert.AreEqual(
                1.0f,
                withOptional.Parse("disengage please now")[0].Command.Score,
                0.001f,
                "so an unrelated command's leading optional un-charges cease_fire's tail"
            );
        }

        [Test]
        public void Coverage_WeightZero_RevertsBothSidesTogether()
        {
            // One weight governs leading and trailing alike, so setting it to 0 reduces the
            // score to rawScore / denominator exactly and symptom 2 comes back. Under the old
            // field name that was a hidden coupling — a user who zeroed the skipped-word
            // penalty also silently disabled the issue #42 fix without being told. It is the
            // same coupling now, but the field admits to it.
            LogAssert.Expect(UnityEngine.LogType.Warning, new Regex("required literal \"by\""));
            var parser = new VoxrCommandParser(BurnSlots(), DecelerateCommands("by"), 0f);

            var result = ParseOne(parser, "decelerate hard burn");

            Assert.AreEqual(0, result.Command.MatchedPatternIndex, "the bare form wins again");
            Assert.AreEqual(1.0f, result.Command.Score, 0.001f);
            Assert.IsFalse(result.Command.HasSlot("burn_level"));
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Coverage_ResidualHazard_WhenTheStrandedValueBeginsAPattern()
        {
            // What §5.2 does NOT fix, pinned so it is a known limit rather than a surprise.
            // The orphan run stops at the first token that could begin some pattern. Register
            // ["hard","stop"] and "hard" becomes such a token, so bare "decelerate" is charged
            // nothing, keeps its 1.0, and strands the burn level exactly as before — while the
            // slot-filled sibling still sits at 2/3.
            //
            // This is why WarnOnDroppableRequiredLiteral survives this feature: its stated
            // rationale ("nothing normalized to 1.0 can beat it") is now false in general, but
            // the hazard it warns about is real in precisely this residue — which is also why
            // the warning below is expected rather than incidental.
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
                    // Three elements, so on "hard burn" it matches 1 required and misses 2 —
                    // DR-7 REFUSES it. That is deliberate: the orphan test is defined over
                    // registered patterns, not over admitted candidates, so "hard" must
                    // terminate the run even though no candidate starting there survives. An
                    // earlier two-element version of this fixture was DR-7-admitted, so it
                    // passed identically under both definitions and pinned neither.
                    new VoxrCommandDefinition(
                        "hard_stop",
                        new[] { new[] { "hard", "stop", "now" } }
                    ),
                }
            );

            var results = parser.Parse("decelerate hard burn");

            Assert.AreEqual(1, results.Length, "the hard_stop candidate is refused by DR-7");
            Assert.AreEqual("decelerate", results[0].Command.Intent);
            Assert.AreEqual(1.0f, results[0].Command.Score, 0.001f);
            Assert.IsFalse(results[0].Command.HasSlot("burn_level"));
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Coverage_LeadingTerm_ReordersSiblingsAtTheSameStart()
        {
            // The leading half of "coverage is computed before candidates are compared" — and
            // the only shape that can demonstrate it. Earliest start outranks score, and
            // SkippedBefore is the same constant for every candidate at a given start index,
            // so the leading term can only decide between siblings that START TOGETHER and
            // whose denominators differ.
            //
            // Without such a case the whole leading half can be reverted to post-selection
            // application with the suite staying green — verified by building that mutant and
            // finding zero failures across the suite and 1095 utterances.
            //
            // "burn_now" makes "hard" a pattern start so both trailing terms are zero;
            // "set target mode" makes "target" in-grammar filler that begins nothing. Two
            // leading skips then separate 1/(1+2) = 0.333 from 2/(3+2) = 0.400, inverting the
            // 1.0-vs-0.667 order the un-charged scores would have given.
            LogAssert.Expect(UnityEngine.LogType.Warning, new Regex("required literal \"by\""));

            var parser = new VoxrCommandParser(
                new[] { new VoxrSlotDefinition("burn", new[] { "hard" }) },
                new[]
                {
                    new VoxrCommandDefinition("decelerate", new[] { new[] { "decelerate" } }),
                    new VoxrCommandDefinition(
                        "decelerate_by",
                        new[] { new[] { "decelerate", "by", "{burn}" } }
                    ),
                    new VoxrCommandDefinition("burn_now", new[] { new[] { "{burn}", "now" } }),
                    new VoxrCommandDefinition(
                        "set_target_mode",
                        new[] { new[] { "set", "target", "mode" } }
                    ),
                }
            );

            var results = parser.Parse("target target decelerate hard");

            Assert.AreEqual(
                1,
                results.Length,
                "the bare form would strand \"hard\" and split this into two commands"
            );
            Assert.AreEqual("decelerate_by", results[0].Command.Intent);
            Assert.AreEqual("hard", results[0].Command.GetSlot("burn"));
            Assert.AreEqual(2f / 5f, results[0].Command.Score, 0.001f);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Coverage_MisPredictedTokenCharge_StopsAtTheNextPatternStart()
        {
            // A3 charges the mis-predicted token and then hands back to the ordinary run — it
            // is an exception at ONE position, not a licence to charge everything after.
            //
            // "switch to navigation" misses its final element against "weapons", so A3 charges
            // that token; the run then stops at "halt", which begins a pattern. 2/(3+1) = 0.5.
            // Charging the whole tail instead would give 2/(3+2) = 0.4 — which is exactly what
            // the second parse shows, where nothing after the mis-predicted token is a start.
            var parser = new VoxrCommandParser(
                Array.Empty<VoxrSlotDefinition>(),
                new[]
                {
                    new VoxrCommandDefinition(
                        "mode_navigation",
                        new[] { new[] { "switch", "to", "navigation" } }
                    ),
                    new VoxrCommandDefinition("halt", new[] { new[] { "halt" } }),
                }
            );

            Assert.AreEqual(
                0.5f,
                parser.Parse("switch to weapons halt")[0].Command.Score,
                0.001f,
                "the run stops at \"halt\", so only the mis-predicted token is charged"
            );
            Assert.AreEqual(
                0.4f,
                parser.Parse("switch to weapons stuff")[0].Command.Score,
                0.001f,
                "with no start to stop at, the run continues and charges both"
            );
        }

        [Test]
        public void Coverage_FractionalWeight_ScalesBothTermsAlike()
        {
            // The only assertions on the weight were at 0 and 1, where many inequivalent
            // formulas agree — Ceil(w), w squared, or a weight applied to one term only all
            // pass. One leading skip ("zulu") and one trailing orphan ("yankee") over a
            // one-element pattern make the arithmetic 1/(1 + 2w), which separates them.
            var commands = new[] { new VoxrCommandDefinition("solo", new[] { new[] { "alpha" } }) };

            foreach (var (weight, expected) in new[] { (0f, 1f), (0.5f, 0.5f), (1f, 1f / 3f) })
            {
                var parser = new VoxrCommandParser(
                    Array.Empty<VoxrSlotDefinition>(),
                    commands,
                    weight
                );

                Assert.AreEqual(
                    expected,
                    ParseOne(parser, "zulu alpha yankee").Command.Score,
                    0.001f,
                    $"1 / (1 + 2 x {weight})"
                );
            }
        }

        [Test]
        public void Coverage_FollowUpVocabulary_IsNotChargedAsAnOrphan()
        {
            // The recogniser puts confirm/cancel words in the DECODER's grammar, so VOSK
            // returns them as real tokens rather than [unk] — and they begin no pattern. Left
            // out of the parser's view they read as orphans, and "disengage, yes" drops from
            // 1.0 to 0.5, under minScore, firing nothing: a working utterance broken by the
            // package's own vocabulary.
            //
            // The two parsers below differ only in whether the parser was told what the
            // decoder was told, which is the whole of the fix.
            var commands = new[]
            {
                new VoxrCommandDefinition("cease_fire", new[] { new[] { "disengage" } }),
            };

            var informed = new VoxrCommandParser(
                Array.Empty<VoxrSlotDefinition>(),
                commands,
                VoxrCommandParser.DefaultCoverageWeight,
                new[] { "yes", "confirm", "cancel" }
            );
            var uninformed = new VoxrCommandParser(Array.Empty<VoxrSlotDefinition>(), commands);

            Assert.AreEqual(
                1.0f,
                ParseOne(informed, "disengage yes").Command.Score,
                0.001f,
                "a follow-up word can legitimately begin something, so it ends the orphan run"
            );
            Assert.AreEqual(
                0.5f,
                ParseOne(uninformed, "disengage yes").Command.Score,
                0.001f,
                "and this is what it costs when the parser is not told — the defect being fixed"
            );
        }

        [Test]
        public void Coverage_BarePatternWithNoSibling_FallsBelowTheThreshold()
        {
            // Requirements §8's open question, pinned as a measured consequence rather than
            // left to be discovered. The demotion that fixes symptom 2 does not check whether
            // a sibling exists to win instead: a grammar registering only "decelerate", asked
            // to hear "decelerate hard burn", now scores 1/(1+2) = 0.333 and fires NOTHING
            // where it used to fire at 1.0.
            //
            // Defensible — the utterance contains two words the grammar cannot explain, and
            // this is the same logic issue #31 already applies on the leading side. But it is
            // a real change for single-pattern grammars whose users add natural trailing words
            // ("decelerate now"), and the locked design never names it.
            var parser = new VoxrCommandParser(
                BurnSlots(),
                new[] { new VoxrCommandDefinition("decelerate", new[] { new[] { "decelerate" } }) }
            );

            var result = ParseOne(parser, "decelerate hard burn");

            Assert.AreEqual(1f / 3f, result.Command.Score, 0.001f);
            Assert.Less(
                result.Command.Score,
                0.6f,
                "below the default minScore, so nothing fires at all"
            );
        }

        // Two patterns sharing a prefix and differing only in their final element, where that
        // element is itself a pattern start. This is ordinary command-grammar authoring — the
        // shipped demo grammar has exactly this shape in "switch to weapons" / "switch to
        // navigation" alongside "weapons mode" — and it is what forced Amendment A3.
        static VoxrCommandParser CreateModeSwitchParser()
        {
            return new VoxrCommandParser(
                Array.Empty<VoxrSlotDefinition>(),
                new[]
                {
                    new VoxrCommandDefinition(
                        "mode_weapons",
                        new[] { new[] { "weapons", "mode" }, new[] { "switch", "to", "weapons" } }
                    ),
                    new VoxrCommandDefinition(
                        "mode_navigation",
                        new[]
                        {
                            new[] { "navigation", "mode" },
                            new[] { "switch", "to", "navigation" },
                        }
                    ),
                }
            );
        }

        [Test]
        public void Coverage_MisPredictedToken_IsChargedNotExcused()
        {
            // Amendment A3, pinned by the case that produced it. The orphan run may not be
            // terminated by a token the candidate's OWN next required element just failed to
            // match — otherwise a pattern is rewarded for matching LESS.
            //
            // "switch to navigation" misses its final element against this utterance, so its
            // consumed span stops at "weapons". That token begins ["weapons","mode"], so
            // under the unamended rule its orphan run terminated at zero and it scored a tidy
            // 2/3 = 0.667. "switch to weapons" matched that element, moving its origin past
            // the very token that would have terminated its own run, so it paid for "target
            // hotel": 3/(3+2) = 0.6. The WRONG command won by 0.067, cleared minScore, and
            // fired.
            //
            // Charging the mis-predicted token puts navigation at 2/(3+3) = 0.333 and leaves
            // weapons at 0.6, which is both correct and above the gate.
            var parser = CreateModeSwitchParser();

            var results = parser.Parse("switch to weapons target hotel");

            Assert.AreEqual(
                "mode_weapons",
                results[0].Command.Intent,
                "the command the speaker actually said must win"
            );
            Assert.AreEqual(3f / 5f, results[0].Command.Score, 0.001f);
            Assert.GreaterOrEqual(
                results[0].Command.Score,
                0.6f,
                "and it must still clear the default minScore"
            );
        }

        [Test]
        public void Coverage_MisPredictedToken_ChargeIsSymmetricAcrossThePair()
        {
            // The same utterance with the two commands swapped. Pinned separately because a
            // rule that happened to favour whichever pattern was registered first would pass
            // the test above and still be wrong.
            var parser = CreateModeSwitchParser();

            var results = parser.Parse("switch to navigation target hotel");

            Assert.AreEqual("mode_navigation", results[0].Command.Intent);
            Assert.AreEqual(3f / 5f, results[0].Command.Score, 0.001f);
        }

        [Test]
        public void Coverage_MisPredictedToken_IsChargedEvenWhenItIsAnUnkRunAway()
        {
            // The [unk] exemption survives A3. The token the candidate mis-predicted is the
            // first REAL one at or after its consumed end, not the [unk] sitting there — so
            // "switch to [unk] target hotel" charges "target" and "hotel" (2), never the
            // [unk] as well (3).
            //
            // Pinned on the SCORE rather than the intent: both candidates tie at 2/(3+2) here
            // and the winner is decided by registration order, so an intent assertion would
            // pin the tie-break instead of the exemption. Charging the [unk] would give
            // 2/(3+3) = 0.333, which this catches.
            var parser = CreateModeSwitchParser();

            var results = parser.Parse("switch to [unk] target hotel");

            Assert.AreEqual(
                2f / 5f,
                results[0].Command.Score,
                0.001f,
                "the [unk] is skipped over, not charged alongside the real leftovers"
            );
        }

        [Test]
        public void Coverage_MisPredictedTokenCharge_DoesNotBiteACompleteMatch()
        {
            // The exception applies only where a required element actually failed. A pattern
            // that matched everything it asked for is charged by the ordinary run rule, so
            // the utterance the grammar was written for is untouched.
            var parser = CreateModeSwitchParser();

            Assert.AreEqual(
                1.0f,
                ParseOne(parser, "switch to weapons").Command.Score,
                0.001f,
                "a complete match still scores a clean 1.0"
            );
        }

        [Test]
        public void Coverage_CannotLiftACandidateTheAdmissionRuleRefused()
        {
            // DR-7 is a unary count filter — more required elements missed than matched — and
            // it never reads the score. Coverage is a ranking term ON the score. A filter and
            // a sort key do not compete, so no weight can un-reject a DR-7 casualty, and the
            // two rules stay orthogonal however the weight is set.
            //
            // ["alpha","bravo","charlie"] heard as "zulu alpha yankee" matches 1 required
            // element and misses 2, so DR-7 refuses it — while its score stays above zero, so
            // the Score <= 0 floor (checked first, and which would mask what this pins) is not
            // what stops it.
            //
            // The filler on both sides is what makes the loop mean something: one leading skip
            // and one trailing orphan, so the candidate's score genuinely moves with the
            // weight — 1/3 at 0, 1/5 at 1, 1/13 at 5 — while admission does not budge. An
            // earlier version of this test used a fixture whose coverage was identically zero,
            // so all three iterations were the same arithmetic and a rule that DID couple
            // admission to coverage would have passed it.
            var commands = new[]
            {
                new VoxrCommandDefinition(
                    "triple",
                    new[] { new[] { "alpha", "bravo", "charlie" } }
                ),
            };

            foreach (float weight in new[] { 0f, 1f, 5f })
            {
                var parser = new VoxrCommandParser(
                    Array.Empty<VoxrSlotDefinition>(),
                    commands,
                    weight
                );

                Assert.AreEqual(
                    0,
                    parser.Parse("zulu alpha yankee").Length,
                    $"admission is independent of the score at coverageWeight {weight}"
                );
            }

            // Direct pin that the charge really is live on this fixture, so a future change
            // cannot re-zero coverage and silently restore the vacuity described above.
            var probe = new VoxrCommandParser(Array.Empty<VoxrSlotDefinition>(), commands);
            probe.BuildCoverageTables(new[] { "zulu", "alpha", "yankee" });

            Assert.AreEqual(1, probe.SkippedBefore(0, 1), "\"zulu\" is a leading skip");
            Assert.AreEqual(1, probe.OrphanedAfter(2), "\"yankee\" is a trailing orphan");
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
            // The cost stays length-proportional — reduced by a third, not abolished
            // (1.5/N to 1/N; design fork F1).
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
                "landing on the boundary is only acceptable because every argument is present "
                    + "— the recogniser-level counterpart is what shows it then fires"
            );
            LogAssert.NoUnexpectedReceived();
        }

        // --- Admission: more evidence for than against (issue #65, DR-7) ---
        //
        // Zeroing the miss penalty removed a filter the penalty was enforcing by accident —
        // though a weaker one than DR-7: at -0.5 the score sank to <= 0 only once misses
        // reached TWICE the matches, where DR-7 refuses at more misses than matches. The band
        // between is refused deliberately; there the old model was incoherent, since ADDING
        // debris to an utterance could raise a real command's score and flip it to firing.
        //
        // The first three tests pin the harms the review cycle reproduced when DR-7 was
        // briefly absent, the fourth pins its optional-element clause, and the last is a
        // §5.1 consequence rather than an admission one. None is hypothetical: every one was
        // measured against the real parser at both revisions before being written.

        [Test]
        public void Admission_FragmentCannotPreEmptRoundOne_SkippedWordChargeSurvives()
        {
            // The sharpest harm: a fragment that wins round 1 on EARLIEST START — which
            // CompareCandidate ranks above score — consumes the leading tokens, which moves
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
        public void Admission_OptionalElementsCountTowardNeitherSide()
        {
            // DR-7's second clause, which the three harm tests above cannot reach because
            // their fixtures are optional-free. Both halves are pinned here, and they fail in
            // opposite directions, so neither can be satisfied by accident.
            //
            // A matched optional is NOT evidence FOR. "alpha one two" fills both optional
            // slots and matches one required literal against two missed, so DR-7 refuses it —
            // even though the score would be (1 + 1 + 1 + 0 + 0) / 5 = 0.60, exactly the
            // default gate. This is the rule's sharpest edge: it can refuse a candidate the
            // score would have admitted. It needs more than 2.0 of matched-optional credit to
            // bite, which no pattern in this package carries, but the behaviour is deliberate
            // and belongs on the record rather than discovered later.
            var matchedOptionals = new VoxrCommandParser(
                new[]
                {
                    new VoxrSlotDefinition("q1", new[] { "one", "two" }),
                    new VoxrSlotDefinition("q2", new[] { "one", "two" }),
                },
                new[]
                {
                    new VoxrCommandDefinition(
                        "alpha_cmd",
                        new[] { new[] { "alpha", "{?q1}", "{?q2}", "bravo", "charlie" } }
                    ),
                }
            );

            Assert.AreEqual(
                0,
                matchedOptionals.Parse("alpha one two").Length,
                "filling optionals is not evidence that the required elements were spoken"
            );

            // An omitted optional is NOT evidence AGAINST. "launch missiles" matches "launch"
            // and {weapon} and misses "target" and {target} — two against two, which DR-7
            // admits — and the unspoken {?quantity} must not tip that to three. The score is
            // (1 + 1 + 0 - 1) / 4 = 0.25, the optional leaving both sides of the ratio.
            var omittedOptional = new VoxrCommandParser(
                new[]
                {
                    new VoxrSlotDefinition("quantity", new[] { "all", "one" }),
                    new VoxrSlotDefinition("weapon", new[] { "missiles" }),
                    new VoxrSlotDefinition("target", new[] { "hotel one" }),
                },
                new[]
                {
                    new VoxrCommandDefinition(
                        "launch_weapon",
                        new[]
                        {
                            new[] { "launch", "{?quantity}", "{weapon}", "target", "{target}" },
                        }
                    ),
                }
            );

            var result = ParseOne(omittedOptional, "launch missiles");
            Assert.AreEqual(0.25f, result.Command.Score, 0.001f);
            Assert.AreEqual("missiles", result.Command.GetSlot("weapon"));
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
            //
            // This grammar is the #74 example itself, so since that issue's backlog item 1 it
            // is also reported at construction — the author now learns about the coin flip
            // before shipping, which is the whole point of that scan. The expectation is added
            // rather than the assertion below weakened: the warning is correct here.
            LogAssert.Expect(UnityEngine.LogType.Warning, new Regex("differ only at element 3"));
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

        // ---------- Sibling discriminator detection (issue #74, design §5.1/§5.2/§5.5) ----------
        //
        // The construction-time half of the sibling-tie design. The test above pins the runtime
        // consequence — a coin flip resolved by registration order — and these pin that the
        // shape is now REPORTED to the author, statically, before a word is ever spoken.
        //
        // The predicate tests call FindSiblingSets directly rather than reading log text, so a
        // reworded message breaks the four message tests below and nothing else.

        static VoxrCommandDefinition Sib(string intent, params string[][] patterns) =>
            new VoxrCommandDefinition(intent, patterns);

        static string[] SibP(params string[] elements) => elements;

        static VoxrCommandDefinition[] SwitchSiblings() =>
            new[]
            {
                Sib("mode_weapons", SibP("switch", "to", "weapons")),
                Sib("mode_navigation", SibP("switch", "to", "navigation")),
            };

        [Test]
        public void SiblingSets_TrailingDiscriminator_IsDetected()
        {
            var sets = VoxrCommandParser.FindSiblingSets(SwitchSiblings());

            Assert.AreEqual(1, sets.Count);
            Assert.AreEqual(2, sets[0].DiscriminatorIndex, "0-based; the message adds one");
            Assert.AreEqual(2, sets[0].Members.Length);
            Assert.AreEqual("mode_weapons", sets[0].Members[0].Intent);
            Assert.AreEqual("weapons", sets[0].Members[0].Value);
            Assert.AreEqual("mode_navigation", sets[0].Members[1].Intent);
            Assert.AreEqual("navigation", sets[0].Members[1].Value);
        }

        [Test]
        public void SiblingSets_MedialDiscriminator_IsDetected()
        {
            // DR-1 puts the discriminator at ANY position, and this is why. A trailing-only
            // definition would exclude the medial case, which is the MORE dangerous of the two:
            // the trailing one is at least refused at the eager gate by issue #70's tail rule,
            // while the medial one commits early there as well (design §2.8, confirmed in
            // VoxrEagerCommitTests). Narrowing to the reported example would have missed it.
            var sets = VoxrCommandParser.FindSiblingSets(
                new[]
                {
                    Sib("set_mode", SibP("set", "{ship}", "mode", "on")),
                    Sib("set_level", SibP("set", "{ship}", "level", "on")),
                }
            );

            Assert.AreEqual(1, sets.Count);
            Assert.AreEqual(2, sets[0].DiscriminatorIndex);
            Assert.AreEqual("mode", sets[0].Members[0].Value);
            Assert.AreEqual("level", sets[0].Members[1].Value);
        }

        [Test]
        public void SiblingSets_ThreeWay_IsOneSetNotThreePairs()
        {
            // The unit is the SET, not the pair. Three intents differing at one shared position
            // are one hazard with three answers, which is also what makes the discriminating
            // values usable as a disambiguation vocabulary later.
            var sets = VoxrCommandParser.FindSiblingSets(
                new[]
                {
                    Sib("autopilot_on", SibP("set", "auto", "pilot", "on")),
                    Sib("autopilot_off", SibP("set", "auto", "pilot", "off")),
                    Sib("autopilot_standby", SibP("set", "auto", "pilot", "standby")),
                }
            );

            Assert.AreEqual(1, sets.Count, "one set, not three pairs");
            Assert.AreEqual(3, sets[0].Members.Length);
            CollectionAssert.AreEqual(
                new[] { "on", "off", "standby" },
                Array.ConvertAll(sets[0].Members, m => m.Value)
            );
        }

        [Test]
        public void SiblingSets_OptionalLiteralInTheFrame_IsNotASet()
        {
            // An included "?to" consumes the same token a required "to" does, so it is tempting
            // to treat the two frames as equal — and an earlier draft did. But consumption is
            // not scoring: a matched optional literal credits OptionalLiteralScore to BOTH
            // sides where a required one credits MatchScore, and (r-0.5)/(d-0.5) < r/d for
            // r < d. On "switch to" these score 1.5/2.5 = 0.60 and 2/3 = 0.667, so selection
            // separates them on its first key and never reaches registration order.
            //
            // Detecting this pair would therefore assert a tie that does not happen — the same
            // false-positive class the empty-frame and same-intent rules exclude.
            var sets = VoxrCommandParser.FindSiblingSets(
                new[]
                {
                    Sib("mode_weapons", SibP("switch", "?to", "weapons")),
                    Sib("mode_navigation", SibP("switch", "to", "navigation")),
                }
            );

            Assert.AreEqual(
                0,
                sets.Count,
                "an optional literal does not score like a required one"
            );
        }

        [Test]
        public void SiblingSets_OptionalSlotInTheFrame_StillMatches()
        {
            // The other half of the asymmetry, and the reason NormalizeElement still folds slot
            // decoration: a matched slot credits MatchScore whether it was written {ship} or
            // {?ship}, so these two DO tie on the dropped word and the set is real.
            var sets = VoxrCommandParser.FindSiblingSets(
                new[]
                {
                    Sib("set_mode", SibP("set", "{?ship}", "mode", "on")),
                    Sib("set_level", SibP("set", "{ship}", "level", "on")),
                }
            );

            Assert.AreEqual(1, sets.Count, "optional slots are score-neutral, so the tie is real");
            Assert.AreEqual(2, sets[0].DiscriminatorIndex);
        }

        [Test]
        public void SiblingSets_DifferingAtAnOptionalLiteral_IsNotASet()
        {
            // At the DISCRIMINATOR the "?" is load-bearing, which is the other half of the
            // asymmetry above. An optional discriminating word means the author already said
            // the pattern matches with or without it, so these are duplicates, not siblings.
            var sets = VoxrCommandParser.FindSiblingSets(
                new[]
                {
                    Sib("light_on", SibP("turn", "light", "?on")),
                    Sib("light_off", SibP("turn", "light", "?off")),
                }
            );

            Assert.AreEqual(0, sets.Count);
        }

        [Test]
        public void SiblingSets_NonSiblingShapes_AreNotSets()
        {
            Assert.AreEqual(
                0,
                VoxrCommandParser
                    .FindSiblingSets(
                        new[]
                        {
                            Sib("a", SibP("switch", "to", "weapons")),
                            Sib("b", SibP("switch", "weapons")),
                        }
                    )
                    .Count,
                "unequal length"
            );

            Assert.AreEqual(
                0,
                VoxrCommandParser
                    .FindSiblingSets(
                        new[]
                        {
                            Sib("a", SibP("switch", "to", "weapons")),
                            Sib("b", SibP("switch", "at", "navigation")),
                        }
                    )
                    .Count,
                "two differences is not one"
            );

            Assert.AreEqual(
                0,
                VoxrCommandParser
                    .FindSiblingSets(
                        new[]
                        {
                            Sib("a", SibP("fire", "{weapon}")),
                            Sib("b", SibP("fire", "{target}")),
                        }
                    )
                    .Count,
                "a slot is not a required literal, so a differing slot is not a discriminator"
            );

            Assert.AreEqual(
                0,
                VoxrCommandParser
                    .FindSiblingSets(
                        new[]
                        {
                            Sib("a", SibP("switch", "to", "weapons")),
                            Sib("b", SibP("switch", "to", "weapons")),
                        }
                    )
                    .Count,
                "identical patterns differ at ZERO positions — an authoring error, not a tie"
            );
        }

        [Test]
        public void SiblingSets_SingleElementPatterns_AreSuppressed()
        {
            // These satisfy the relation — equal length, one differing required literal — but
            // the frame is empty, so if the word is dropped NOTHING matches: both candidates
            // score 0 and are rejected outright. There is no tie to fall through to
            // registration order, so warning about one would be telling the author something
            // untrue. The demo grammar contains this pair.
            var sets = VoxrCommandParser.FindSiblingSets(
                new[] { Sib("cease_fire", SibP("disengage")), Sib("resume_fire", SibP("reengage")) }
            );

            Assert.AreEqual(0, sets.Count, "an empty frame leaves no remainder to tie on");
        }

        [Test]
        public void SiblingSets_OnePatternsOwnForms_AreNotSiblingsOfEachOther()
        {
            // A pattern cannot be ambiguous with itself, and since frame comparison stopped
            // folding optional literals it cannot even look as though it is: two same-length
            // forms of one pattern differ only in which optionals they include, and with the
            // "?" preserved those positions never compare equal. The shift that would once
            // have aligned a required literal against a different one — including "?one" while
            // omitting "?two" — now yields ["?one","one","two","three"] against
            // ["one","two","?two","three"], which differ at three positions, not one.
            var sets = VoxrCommandParser.FindSiblingSets(
                new[] { Sib("count", SibP("?one", "one", "two", "?two", "three")) }
            );

            Assert.AreEqual(0, sets.Count, "a pattern cannot be ambiguous with itself");
        }

        [Test]
        public void SiblingSets_RemainderOfOnlyOptionals_IsNotASet()
        {
            // The frame is non-empty, so the length gate passes — but nothing in it credits
            // MatchedRequired. An optional literal contributes to score and span and never to
            // that counter, so with the discriminator dropped both members are 0 matched
            // against 1 missed and the admission rule refuses BOTH before any comparison key.
            // Nothing fires; there is no intent to get wrong.
            var sets = VoxrCommandParser.FindSiblingSets(
                new[]
                {
                    Sib("mode_weapons", SibP("?please", "weapons")),
                    Sib("mode_navigation", SibP("?please", "navigation")),
                }
            );

            Assert.AreEqual(0, sets.Count, "a remainder that credits nothing cannot tie");
        }

        [Test]
        public void SiblingSets_OptionalSlotLeavingNoRequiredEvidence_IsNotASet()
        {
            // The asymmetric half of the same rule, and the limit of folding {?ship} onto
            // {ship}. The fold is right about SCORE — a matched slot credits MatchScore either
            // way — but only the required one credits MatchedRequired. Here the frame's sole
            // other element IS that slot, so dropping the discriminator leaves set_mode at
            // 0 matched / 1 missed (refused) and set_level at 1 / 1 (admitted). They score the
            // same and never compete: one is gone before selection compares anything.
            var sets = VoxrCommandParser.FindSiblingSets(
                new[]
                {
                    Sib("set_mode", SibP("{?ship}", "mode")),
                    Sib("set_level", SibP("{ship}", "level")),
                }
            );

            Assert.AreEqual(0, sets.Count, "score-equivalent is not admission-equivalent");
        }

        // ---------- Duplicate-valued members are kept, not dropped (issue #90) ----------

        [Test]
        public void SiblingSets_DuplicateValuedMember_IsRetained()
        {
            // Issue #90's example. The gate used to keep one member per distinct value, so
            // set_b was dropped and never named — even though set_b <-> set_c is exactly the
            // hazard set_a <-> set_c is. All three are now members; two distinct values.
            var sets = VoxrCommandParser.FindSiblingSets(
                new[]
                {
                    Sib("set_a", SibP("set", "{ship}", "mode", "on")),
                    Sib("set_b", SibP("set", "{ship}", "mode", "on")),
                    Sib("set_c", SibP("set", "{ship}", "level", "on")),
                }
            );

            Assert.AreEqual(1, sets.Count);
            Assert.AreEqual(3, sets[0].Members.Length, "the duplicate-valued member is kept");
            CollectionAssert.AreEqual(
                new[] { "set_a", "set_b", "set_c" },
                Array.ConvertAll(sets[0].Members, m => m.Intent)
            );
            CollectionAssert.AreEqual(
                new[] { "mode", "mode", "level" },
                Array.ConvertAll(sets[0].Members, m => m.Value)
            );
        }

        [Test]
        public void SiblingSets_ShadowedCrossIntentMember_IsNoLongerSuppressed()
        {
            // Issue #90's sharper variant, and the one that reaches the runtime. set_mode
            // contributes BOTH values, so the old first-wins-by-value dedup dropped set_level
            // entirely; the survivors then shared one intent and the same-intent filter
            // suppressed the set outright — an under-report becoming no report at all. The
            // real hazard, set_mode's "mode" pattern against set_level, was invisible.
            var sets = VoxrCommandParser.FindSiblingSets(
                new[]
                {
                    Sib(
                        "set_mode",
                        SibP("set", "{ship}", "mode", "on"),
                        SibP("set", "{ship}", "level", "on")
                    ),
                    Sib("set_level", SibP("set", "{ship}", "level", "on")),
                }
            );

            Assert.AreEqual(1, sets.Count);
            Assert.AreEqual(3, sets[0].Members.Length);
            Assert.IsFalse(
                VoxrCommandParser.IsSingleIntent(sets[0]),
                "set_level must survive, or the same-intent filter hides a real hazard"
            );
        }

        [Test]
        public void SiblingSets_IdenticalPatternsAcrossIntents_AreStillNotASet()
        {
            // Requirements F8 still holds after issue #90: two patterns reaching the SAME
            // literal are duplicates of each other, not siblings. Keeping members did not
            // widen the relation — the "at least two distinct values" gate is what excludes
            // them now, in place of the old per-value dedup.
            var sets = VoxrCommandParser.FindSiblingSets(
                new[]
                {
                    Sib("set_a", SibP("set", "{ship}", "mode", "on")),
                    Sib("set_b", SibP("set", "{ship}", "mode", "on")),
                }
            );

            Assert.AreEqual(0, sets.Count);
        }

        [Test]
        public void SiblingSets_DuplicatedOptionalElement_DoesNotDoubleCountAPattern()
        {
            // ExpandOptionals enumerates 2^optionals subsets without deduplicating, so a
            // pattern carrying the same optional twice yields the identical expanded form from
            // two different masks. Both reach one bucket with the same (command, pattern,
            // value). The old per-value dedup hid that; keeping members exposes it, and without
            // the exact-duplicate guard the warning would name one pattern twice.
            // The rival must share the frame, or "dup" never forms a set at all and this test
            // passes without exercising anything.
            var sets = VoxrCommandParser.FindSiblingSets(
                new[]
                {
                    Sib("dup", SibP("set", "?now", "?now", "mode", "on")),
                    Sib("other", SibP("set", "?now", "level", "on")),
                }
            );

            Assert.AreEqual(1, sets.Count, "the fixture must actually produce a set");
            Assert.AreEqual(
                2,
                sets[0].Members.Length,
                "'dup' reaches this bucket from two identical expansions and must be named once"
            );

            var seen = new List<string>();
            foreach (var m in sets[0].Members)
            {
                string key = m.CommandIndex + ":" + m.PatternIndex;
                Assert.IsFalse(seen.Contains(key), "one pattern reached this set twice: " + key);
                seen.Add(key);
            }
        }

        [Test]
        public void SiblingSets_OneHazardUnderTwoFrames_WithAnExtraMember_StaysTwoSets()
        {
            // A consequence of keeping members, accepted rather than fixed (architecture §6.4).
            // IndexOfSameMembers collapses two frames of one hazard by comparing member lists,
            // and that comparison only worked because the old gate normalised every bucket to
            // one member per value. Here "set * on" collects a, b AND c while "set ?now * on"
            // collects only a and b, so the lists differ and both sets survive.
            //
            // Two warnings for what an author may read as one problem — accepted because the
            // sets genuinely implicate different patterns, and collapsing them would have to
            // discard c, which is issue #90 again one level up. Pinned so the next maintainer
            // meets a decision rather than a bug.
            var sets = VoxrCommandParser.FindSiblingSets(
                new[]
                {
                    Sib("a", SibP("set", "?now", "mode", "on")),
                    Sib("b", SibP("set", "?now", "level", "on")),
                    Sib("c", SibP("set", "mode", "on")),
                }
            );

            Assert.AreEqual(2, sets.Count);
            CollectionAssert.AreEquivalent(
                new[] { 2, 3 },
                Array.ConvertAll(sets.ToArray(), s => s.Members.Length),
                "the shorter frame picks up the third pattern; the longer one does not"
            );
        }

        [Test]
        public void SiblingWarning_CrossIntent_NamesIntentsPatternsAndValues()
        {
            LogAssert.Expect(
                UnityEngine.LogType.Warning,
                new Regex(
                    "Intents 'mode_weapons' and 'mode_navigation' have patterns "
                        + "\"switch to weapons\" and \"switch to navigation\" that differ only "
                        + "at element 3 \\(\"weapons\" or \"navigation\"\\).*"
                        + "the wrong intent can fire"
                )
            );

            var parser = new VoxrCommandParser(Array.Empty<VoxrSlotDefinition>(), SwitchSiblings());

            Assert.IsNotNull(parser, "the shape is a warning, not an error");
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void SiblingSets_SameIntent_IsDetectedButNotWarnedAbout()
        {
            // Within one intent the wrong INTENT cannot fire — the same command is dispatched
            // whichever pattern wins, and the "tie" is between two phrasings the author
            // deliberately made equivalent. Measured over the demo grammar (design §7.3), all
            // six same-intent sets were ordinary synonym authoring, so warning about these
            // would have made the scan noise on the package's own sample grammar.
            //
            // Detected but not reported, and the split matters: the RELATION stays exactly as
            // DR-1 defines it, so a later consumer that does care about a same-intent tie still
            // sees one. Only the author-facing warning is filtered.
            var commands = new[]
            {
                Sib(
                    "set_mode",
                    SibP("set", "auto", "pilot", "on"),
                    SibP("set", "auto", "pilot", "off")
                ),
            };

            Assert.AreEqual(
                1,
                VoxrCommandParser.FindSiblingSets(commands).Count,
                "the primitive still reports it — items downstream may want it"
            );

            var parser = new VoxrCommandParser(Array.Empty<VoxrSlotDefinition>(), commands);

            Assert.IsNotNull(parser);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void SiblingWarning_OneIntentContributingTwoPatterns_NamesItOnce()
        {
            // Two patterns of cease_fire both tie with resume_fire's, so cease_fire contributes
            // two of the three members. Naming the intent once per PATTERN would print
            // "Intents 'cease_fire', 'cease_fire' and 'resume_fire'", so intents are
            // deduplicated for display while every pattern is still listed.
            //
            // Three elements rather than the demo grammar's two, deliberately: at two the tie
            // scores 0.5 and is suppressed as unreachable, which would leave this test pinning
            // nothing.
            LogAssert.Expect(
                UnityEngine.LogType.Warning,
                new Regex(
                    "Intents 'cease_fire' and 'resume_fire' have patterns \"cease the fire\", "
                        + "\"hold the fire\" and \"resume the fire\" that differ only at "
                        + "element 1 \\(\"cease\", \"hold\" or \"resume\"\\)"
                )
            );

            var parser = new VoxrCommandParser(
                Array.Empty<VoxrSlotDefinition>(),
                new[]
                {
                    Sib("cease_fire", SibP("cease", "the", "fire"), SibP("hold", "the", "fire")),
                    Sib("resume_fire", SibP("resume", "the", "fire")),
                }
            );

            Assert.IsNotNull(parser);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void SiblingSets_DemoGrammar_VolumeAndOrderAreStable()
        {
            // The design gates "warning on by default" on measured volume over a real grammar
            // (§7.3), and the human's ruling to suppress same-intent sets rests on that split.
            // Pinned here so the evidence is reproducible from the branch and so a later change
            // that makes this scan noisier cannot pass unnoticed — the earlier measurement was
            // taken from a HAND TRANSCRIPTION of this grammar and was wrong because of it, so
            // this reads the shipped definitions directly.
            //
            // It also pins emission ORDER, which nothing else does: the scan walks a first-seen
            // key list precisely because Dictionary iteration order is unspecified, and without
            // an assertion over a multi-set grammar that guarantee is untested.
            var sets = VoxrCommandParser.FindSiblingSets(DemoGrammar.AllCommands());

            int cross = 0;
            var crossFrames = new List<string>();
            foreach (var set in sets)
            {
                // The SHIPPED filter, not a copy of the rule — this split is the evidence the
                // default-on ruling rests on, so it has to track what the scan actually does.
                if (VoxrCommandParser.IsSingleIntent(set))
                    continue;

                cross++;
                crossFrames.Add(
                    $"{set.Members[0].Intent}@{set.DiscriminatorIndex + 1}:"
                        + string.Join("/", Array.ConvertAll(set.Members, m => m.Value))
                );
            }

            Assert.AreEqual(
                11,
                sets.Count,
                "total sets the relation admits in the shipped demo grammar"
            );
            Assert.AreEqual(5, cross, "…of which these are cross-intent");
            Assert.AreEqual(
                6,
                sets.Count - cross,
                "…and these are same-intent synonym authoring, suppressed by the human's ruling"
            );

            // Registration order, not hash order.
            CollectionAssert.AreEqual(
                new[]
                {
                    "cease_fire@1:cease/resume",
                    "cease_fire@1:stop/resume",
                    "mode_weapons@1:weapons/navigation",
                    "mode_weapons@3:weapons/navigation",
                    "mode_all@1:enable/disable",
                },
                crossFrames,
                "emission order must be stable across runs"
            );
        }

        [Test]
        public void SiblingWarning_DemoGrammar_WarnsOnlyOnTheReachableTie()
        {
            // The number that actually matters for the default-on ruling, and it is NOT the
            // cross-intent count above. Four of those five have two-element frames, which drop
            // to 0.5 when the discriminator goes — under the default gate, so both siblings are
            // rejected and nothing fires. Only "switch to weapons"/"switch to navigation"
            // reaches 2/3 = 0.667 and can actually coin-flip.
            //
            // So the shipped sample emits ONE warning, not five. Asserted through construction
            // rather than through FindSiblingSets, because the reachability rule lives in the
            // warning: LogAssert fails the test on any warning beyond the one expected here.
            // Pre-existing and unrelated: the demo grammar's quantity slot aliases "a" to "one",
            // and the single-character-alias validation has always warned about it. This is the
            // first test to put the demo SLOTS through the constructor, so it is the first to
            // see it. Declared rather than filtered, so the NoUnexpectedReceived below still
            // means "exactly one sibling warning".
            LogAssert.Expect(
                UnityEngine.LogType.Warning,
                new Regex("single-character alias \"a\"")
            );
            LogAssert.Expect(
                UnityEngine.LogType.Warning,
                new Regex(
                    "Intents 'mode_weapons' and 'mode_navigation' have patterns "
                        + "\"switch to weapons\" and \"switch to navigation\""
                )
            );

            var parser = new VoxrCommandParser(DemoGrammar.AllSlots(), DemoGrammar.AllCommands());

            Assert.IsNotNull(parser);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void SiblingWarning_SameHazardFromTwoExpansions_WarnsOnce()
        {
            // One pattern pair, but an optional element in front of the discriminator gives
            // each of them two forms, so the scan meets the SAME hazard under two different
            // frames — "please switch to *" and "switch to *". Bucketing alone cannot collapse
            // those, so the dedup is keyed on the members rather than on the frame that
            // happened to reveal them. LogAssert fails on a second unexpected warning, so the
            // count is half of what this pins.
            //
            // The other half is WHICH frame survives. It has to be the longest — the reading
            // closest to what the author wrote — or the message would report the discriminator
            // at element 3, its position in the form that silently dropped "?please", and an
            // author counting elements in their own pattern would land on "to" instead of the
            // word the warning is about.
            LogAssert.Expect(
                UnityEngine.LogType.Warning,
                new Regex(
                    "patterns \"\\?please switch to weapons\" and "
                        + "\"\\?please switch to navigation\" that differ only at element 4"
                )
            );

            var parser = new VoxrCommandParser(
                Array.Empty<VoxrSlotDefinition>(),
                new[]
                {
                    Sib("mode_weapons", SibP("?please", "switch", "to", "weapons")),
                    Sib("mode_navigation", SibP("?please", "switch", "to", "navigation")),
                }
            );

            Assert.IsNotNull(parser);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void SiblingWarning_DiscriminatorCollidingWithCancel_IsAlsoReported()
        {
            // Follow-up handling checks the cancel vocabulary before anything else, so a
            // discriminating value that IS cancel vocabulary would be swallowed by cancel and
            // that choice made unreachable once disambiguation ships. Cancel keeps precedence —
            // safety wins — so the author is told at build time instead.
            LogAssert.Expect(UnityEngine.LogType.Warning, new Regex("differ only at element 3"));
            LogAssert.Expect(
                UnityEngine.LogType.Warning,
                new Regex(
                    "carries the discriminating value \"negative\" at element 3, which is also "
                        + "in the default cancel vocabulary"
                )
            );

            var parser = new VoxrCommandParser(
                Array.Empty<VoxrSlotDefinition>(),
                new[]
                {
                    Sib("answer_affirmative", SibP("mark", "contact", "friendly")),
                    Sib("answer_negative", SibP("mark", "contact", "negative")),
                }
            );

            Assert.IsNotNull(parser);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void SiblingWarning_DiscriminatorsClearOfCancel_ReportNoCollision()
        {
            // The other half: the collision report must not fire on ordinary values, or it is
            // noise attached to every sibling set. LogAssert.NoUnexpectedReceived is what pins
            // it — a second warning here would fail the test.
            LogAssert.Expect(UnityEngine.LogType.Warning, new Regex("differ only at element 3"));

            var parser = new VoxrCommandParser(
                Array.Empty<VoxrSlotDefinition>(),
                new[]
                {
                    Sib("mark_alpha", SibP("mark", "contact", "alpha")),
                    Sib("mark_bravo", SibP("mark", "contact", "bravo")),
                }
            );

            Assert.IsNotNull(parser);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void SiblingWarning_TwoElementFrame_IsDetectedButNotWarnedAbout()
        {
            // Losing one required element out of a frame worth D leaves (D-1)/D, so a
            // two-element pattern drops to 0.5 — under the default minScore, which rejects
            // BOTH siblings. MissedLiteral_TwoElementPattern_StillRejected pins the score and
            // its recogniser-level counterpart pins that nothing fires. So the tie the message
            // describes is unreachable at shipped settings and reporting it would tell the
            // author something untrue.
            //
            // Still DETECTED: the relation admits it, and a consumer that applies its own
            // threshold may care. Only the author-facing warning is withheld.
            var commands = new[]
            {
                Sib("cease_fire", SibP("cease", "fire")),
                Sib("resume_fire", SibP("resume", "fire")),
            };

            Assert.AreEqual(
                1,
                VoxrCommandParser.FindSiblingSets(commands).Count,
                "the relation still admits it"
            );

            var parser = new VoxrCommandParser(Array.Empty<VoxrSlotDefinition>(), commands);

            Assert.IsNotNull(parser);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void SiblingWarning_OptionalLiteralCarriesTheFrameOverTheGate()
        {
            // An optional literal weighs OptionalLiteralScore on both sides, so it lifts the
            // frame's total without lifting it as far as a required element would: two
            // required plus one optional gives 1.5/2.5 = 0.60, landing exactly on the default
            // gate rather than under it. Pinned because the reachability rule has to weigh
            // elements, not count them — counting would suppress this real tie.
            LogAssert.Expect(UnityEngine.LogType.Warning, new Regex("differ only at element 3"));

            var parser = new VoxrCommandParser(
                Array.Empty<VoxrSlotDefinition>(),
                new[]
                {
                    Sib("mode_weapons", SibP("switch", "?to", "weapons")),
                    Sib("mode_navigation", SibP("switch", "?to", "navigation")),
                }
            );

            Assert.IsNotNull(parser);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void SiblingWarning_ReachabilityGateTracksTheRecogniserDefault()
        {
            // The scan judges reachability against a copy of the recogniser's default, because
            // the parser constructor is never handed the configured threshold. If the shipped
            // default moves and this copy does not, the scan starts warning about ties that no
            // longer clear the gate, or goes quiet on ties that newly do.
            var field = typeof(VoxrCommandRecogniser).GetField(
                "minScore",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
            );
            Assert.IsNotNull(field, "VoxrCommandRecogniser.minScore");

            var recogniser = new UnityEngine.GameObject(
                "gate"
            ).AddComponent<VoxrCommandRecogniser>();
            try
            {
                Assert.AreEqual(
                    0.6f,
                    (float)field.GetValue(recogniser),
                    0.0001f,
                    "the sibling scan's DefaultMinScore mirrors this value — update both together"
                );
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(recogniser.gameObject);
            }
        }

        [Test]
        public void SiblingWarning_OmittedOptionalIsNotedInTheQuotedPattern()
        {
            // When the surviving frame is shorter than a member's authored pattern, the element
            // number indexes the FORM and not the text being quoted, so the message says so.
            // Here the optional sits on one side only, so no full-length expansion is a sibling
            // and the longest-frame rule cannot rescue the alignment: element 3 of the quoted
            // "?please switch to weapons" is "to", not "weapons".
            LogAssert.Expect(
                UnityEngine.LogType.Warning,
                new Regex(
                    "\"\\?please switch to weapons\" \\(with its optional elements omitted\\)"
                )
            );

            var parser = new VoxrCommandParser(
                Array.Empty<VoxrSlotDefinition>(),
                new[]
                {
                    Sib("mode_weapons", SibP("?please", "switch", "to", "weapons")),
                    Sib("mode_navigation", SibP("switch", "to", "navigation")),
                }
            );

            Assert.IsNotNull(parser);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void SiblingScan_IsEditorOnly()
        {
            // The whole "costs a player build nothing" claim rests on one attribute, and
            // deleting it would break no other test — the scan would simply start running in
            // built players, silently, where its output cannot be seen. Pinned by reflection
            // the way the coverage-weight rename is.
            var scan = typeof(VoxrCommandParser).GetMethod(
                "WarnOnSiblingDiscriminator",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
            );

            Assert.IsNotNull(scan, "the construction-time sibling scan");
            var conditionals = scan.GetCustomAttributes(
                typeof(System.Diagnostics.ConditionalAttribute),
                inherit: false
            );
            Assert.AreEqual(1, conditionals.Length, "the scan must carry [Conditional]");
            Assert.AreEqual(
                "UNITY_EDITOR",
                ((System.Diagnostics.ConditionalAttribute)conditionals[0]).ConditionString,
                "so the call — and therefore the whole scan — is elided in a player build"
            );
        }

        [Test]
        public void SiblingWarning_LeavesTheDroppableLiteralWarningAlone()
        {
            // Two hazards, two scans, two messages. The issue #42 scan needs a strictly longer
            // pattern, an element-prefix relation and a stranded SLOT; issue #81 has just
            // narrowed it to cut false positives. This grammar carries that shape and NOT the
            // sibling one — its patterns are of length 1 and 3, so no two forms are even
            // comparable — and it must still produce exactly the one warning it always did.
            LogAssert.Expect(UnityEngine.LogType.Warning, new Regex("required literal \"by\""));

            var parser = new VoxrCommandParser(BurnSlots(), DecelerateCommands("by"));

            Assert.IsNotNull(parser);
            LogAssert.NoUnexpectedReceived();
        }

        // ---------- Three-state candidate ordering (issue #74, design DR-3) ----------
        //
        // These assert the comparator directly rather than through a selection result, because
        // two of its outcomes cannot be observed from one: a candidate refused by the score
        // floor and one refused by DR-7's admission rule both simply fail to win, and a TIE is
        // invisible from the winner alone — which is the whole defect DR-3 addresses.
        //
        // The keys, in order: start index (lower wins), score (higher), consumed span (higher),
        // literal count (higher). Equal on all four is Tied.

        // Sentinels as the two selection loops actually initialise them. Named rather than
        // inlined so the "no incumbent" tests below are obviously about that state.
        const float NoIncumbentScore = float.MinValue;
        const int NoIncumbentStartIdx = int.MaxValue;

        static VoxrCommandParser.MatchResult Cand(
            float score = 0.75f,
            int consumedEndIdx = 4,
            int literalCount = 2,
            int matchedRequired = 3,
            int missedRequired = 0
        ) =>
            new VoxrCommandParser.MatchResult
            {
                Score = score,
                ConsumedEndIdx = consumedEndIdx,
                LiteralCount = literalCount,
                MatchedRequired = matchedRequired,
                MissedRequired = missedRequired,
            };

        // The incumbent every "beaten by" case below is compared against: start 0, score 0.75,
        // span 4, two literals.
        static VoxrCommandParser.CandidateOrder Against(
            VoxrCommandParser.MatchResult candidate,
            int startIdx = 0
        ) => VoxrCommandParser.CompareCandidate(candidate, startIdx, 0.75f, 0, 4, 2);

        [Test]
        public void CompareCandidate_ZeroScore_IsWorse()
        {
            // The floor runs before everything, including admission.
            Assert.AreEqual(VoxrCommandParser.CandidateOrder.Worse, Against(Cand(score: 0f)));
        }

        [Test]
        public void CompareCandidate_MissedRequiredExceedsMatched_IsWorse()
        {
            // DR-7 admission (issue #65). Refused before any comparison key, which is what lets
            // the eager gate treat "was a tie recorded?" as already excluding inadmissible
            // rivals — see TryEagerCommit's sibling condition.
            Assert.AreEqual(
                VoxrCommandParser.CandidateOrder.Worse,
                Against(Cand(matchedRequired: 1, missedRequired: 2))
            );
        }

        [Test]
        public void CompareCandidate_NoIncumbent_IsBetter()
        {
            Assert.AreEqual(
                VoxrCommandParser.CandidateOrder.Better,
                VoxrCommandParser.CompareCandidate(
                    Cand(),
                    0,
                    NoIncumbentScore,
                    NoIncumbentStartIdx,
                    0,
                    -1
                )
            );
        }

        [Test]
        public void CompareCandidate_NoIncumbent_IsNeverTied()
        {
            // The trap this whole enum invites: reached by falling through the equality chain
            // instead of testing the sentinel first, a first candidate whose keys happen to
            // equal the initial values would report Tied and record a rival that does not
            // exist. Keys set to the sentinels deliberately.
            Assert.AreEqual(
                VoxrCommandParser.CandidateOrder.Better,
                VoxrCommandParser.CompareCandidate(
                    Cand(consumedEndIdx: 0, literalCount: -1),
                    NoIncumbentStartIdx,
                    NoIncumbentScore,
                    NoIncumbentStartIdx,
                    0,
                    -1
                )
            );
        }

        [Test]
        public void CompareCandidate_StartIndex_OutranksEveryOtherKey()
        {
            // Earlier start wins even while losing on all three lower keys...
            Assert.AreEqual(
                VoxrCommandParser.CandidateOrder.Better,
                VoxrCommandParser.CompareCandidate(
                    Cand(score: 0.1f, consumedEndIdx: 1, literalCount: 0),
                    0,
                    0.75f,
                    3,
                    4,
                    2
                )
            );
            // ...and a later start loses even while winning on all three.
            Assert.AreEqual(
                VoxrCommandParser.CandidateOrder.Worse,
                VoxrCommandParser.CompareCandidate(
                    Cand(score: 1f, consumedEndIdx: 9, literalCount: 5),
                    3,
                    0.75f,
                    0,
                    4,
                    2
                )
            );
        }

        [Test]
        public void CompareCandidate_Score_DecidesWhenStartsAgree()
        {
            Assert.AreEqual(VoxrCommandParser.CandidateOrder.Better, Against(Cand(score: 0.8f)));
            Assert.AreEqual(VoxrCommandParser.CandidateOrder.Worse, Against(Cand(score: 0.7f)));
        }

        [Test]
        public void CompareCandidate_ConsumedSpan_DecidesWhenScoresAgree()
        {
            // Issue #41's key, and note it sits ABOVE literal count.
            Assert.AreEqual(
                VoxrCommandParser.CandidateOrder.Better,
                Against(Cand(consumedEndIdx: 5, literalCount: 0))
            );
            Assert.AreEqual(
                VoxrCommandParser.CandidateOrder.Worse,
                Against(Cand(consumedEndIdx: 3, literalCount: 9))
            );
        }

        [Test]
        public void CompareCandidate_LiteralCount_DecidesLast()
        {
            Assert.AreEqual(
                VoxrCommandParser.CandidateOrder.Better,
                Against(Cand(literalCount: 3))
            );
            Assert.AreEqual(VoxrCommandParser.CandidateOrder.Worse, Against(Cand(literalCount: 1)));
        }

        [Test]
        public void CompareCandidate_EveryKeyEqual_IsTied()
        {
            // The outcome that did not exist before DR-3. The old bool ended at a strict > and
            // returned false here, indistinguishable from a loss, so the incumbent kept the win
            // on registration order alone and nothing recorded that it had been a coin flip.
            Assert.AreEqual(VoxrCommandParser.CandidateOrder.Tied, Against(Cand()));
        }
    }
}
