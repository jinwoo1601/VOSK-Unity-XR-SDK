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

        // ---------- The tied rival (issue #74 item 2, widened by issue #95) ----------
        //
        // The flush fires the same command whether or not a rival tied it, so this record
        // changes no behaviour. It exists because the coin flip is otherwise invisible: the
        // winner is correct by every rule the parser has, and a 0.75 in the log looks healthy.
        //
        // Item 2 recorded a tie only when the rival was a SIBLING, which left a non-sibling tie
        // — an authoring hazard — indistinguishable from no tie at all. Since issue #95 any tie
        // is recorded and TiedRivalIsSibling says which kind it was.

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
                diag[0].TiedRivalIntent,
                "the rival that made the winner a coin flip"
            );
            Assert.AreEqual(0, diag[0].TiedRivalPatternIndex);
            Assert.IsTrue(diag[0].TiedRivalIsSibling, "one dropped word apart, and two intents");
            Assert.AreEqual(
                "set_level (pattern 0)",
                diag[0].DescribeTiedRival(),
                "how the Editor surfaces name it"
            );
        }

        [Test]
        public void LastParseDiagnostics_NoTie_RecordsNoRival()
        {
            var parser = CreateParser();
            parser.Parse("shoot missiles", null);

            var diag = parser.LastParseDiagnostics;
            Assert.AreEqual(1, diag.Length);
            Assert.IsNull(diag[0].TiedRivalIntent);
            Assert.AreEqual(-1, diag[0].TiedRivalPatternIndex);
            Assert.IsFalse(diag[0].TiedRivalIsSibling);
            Assert.IsNull(
                diag[0].DescribeTiedRival(),
                "nothing tied, so the Editor surfaces print no tie line"
            );
        }

        [Test]
        public void LastParseDiagnostics_SameIntentTie_RecordsANonSiblingRival()
        {
            // Two phrasings of one command tie. The same command dispatches either way, so this
            // is not the speech ambiguity the runtime can ask about — the per-PAIR intent test
            // keeps it out of the choice vocabulary, and out of the record entirely until issue
            // #95. It is still a coin flip between two patterns, and the author of a grammar
            // where one phrasing can never win is entitled to see that it was a tie rather than
            // a clean victory, so it is recorded and flagged non-sibling.
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
            Assert.AreEqual("set_mode", diag[0].TiedRivalIntent, "its own second phrasing");
            Assert.AreEqual(1, diag[0].TiedRivalPatternIndex);
            Assert.IsFalse(
                diag[0].TiedRivalIsSibling,
                "one intent, so there is no question the speaker could answer"
            );

            Assert.AreEqual(
                0,
                parser.TiedSiblingBuffer[0].RivalCount,
                "and the runtime choice list stays sibling-only"
            );
        }

        [Test]
        public void LastParseDiagnostics_DuplicatePatterns_RecordANonSiblingRival()
        {
            // Issue #95's headline case, and the one nothing else in the package reports. Two
            // intents carry the SAME pattern, so every utterance that matches one matches the
            // other identically and set_level can never fire. They are not siblings — siblings
            // differ at exactly one position and carry two discriminating values, and these
            // differ nowhere — so the sibling set is never emitted, the construction-time
            // warning stays silent, and before this the diagnostic recorded nothing either. The
            // author saw a clean 1.00 PASS on a grammar with a dead command in it.
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
                        new[] { new[] { "set", "{ship}", "mode", "on" } }
                    ),
                }
            );

            var results = parser.Parse("set alpha mode on", null);

            Assert.AreEqual(1, results.Length);
            Assert.AreEqual("set_mode", results[0].Command.Intent, "first-registered wins");

            var diag = parser.LastParseDiagnostics;
            Assert.AreEqual(1, diag.Length);
            Assert.AreEqual(
                "set_level",
                diag[0].TiedRivalIntent,
                "the duplicate that can never fire is named"
            );
            Assert.AreEqual(0, diag[0].TiedRivalPatternIndex);
            Assert.IsFalse(
                diag[0].TiedRivalIsSibling,
                "an authoring error, not speech ambiguity — there is no dropped word to ask about"
            );

            Assert.AreEqual(
                0,
                parser.TiedSiblingBuffer[0].RivalCount,
                "and it stays out of the runtime paths, exactly as design §5.3 requires"
            );
        }

        [Test]
        public void LastParseDiagnostics_ShadowedRival_IsTheCrossIntentOne()
        {
            // The winner's FIRST tied rival here is set_mode's own second pattern, which shares
            // its intent. Recording that one would name a rival the speaker never had a choice
            // about. The per-pair intent test skips it and the scan continues to set_level,
            // which is the real hazard — and the one issue #90's retention keeps in the set at
            // all.
            //
            // Since issue #95 that same-intent rival IS recorded — as a non-sibling — so this
            // now also pins the precedence between the two: a sibling rival displaces a
            // non-sibling exemplar however late it arrives.
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
                diag[0].TiedRivalIntent,
                "a same-intent rival must not shadow the cross-intent one"
            );
            Assert.IsTrue(diag[0].TiedRivalIsSibling, "and it is recorded as the sibling it is");
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
                diag[0].TiedRivalIntent,
                "a rival sharing the winner's intent across two commands is still not a rival"
            );
        }

        [Test]
        public void LastParseDiagnostics_TruncatedSiblingTie_StillNamesTheRival()
        {
            // Past MaxWarningExpansion (6 optionals) a pattern is never expanded, so the sibling
            // relation is established by comparing required elements instead — which proves the
            // tie but names no set, and therefore no discriminating word. Issue #74 item 3 uses
            // that to refuse the pair as a runtime CHOICE: there is no question to ask.
            //
            // The diagnostic is a different question — "was the winner decided by a coin flip,
            // and against whom?" — and on this shape the answer is still yes. Deriving it from
            // the choice list made it silently report null here, which the review caught and
            // this pins: it reads a separate exemplar set on any sibling tie, offerable or not.
            var parser = new VoxrCommandParser(
                Array.Empty<VoxrSlotDefinition>(),
                new[]
                {
                    new VoxrCommandDefinition(
                        "shields_up",
                        new[]
                        {
                            new[]
                            {
                                "engage",
                                "?please",
                                "?now",
                                "?sir",
                                "?kindly",
                                "?quickly",
                                "?really",
                                "?just",
                                "shields",
                                "online",
                            },
                        }
                    ),
                    new VoxrCommandDefinition(
                        "weapons_up",
                        new[] { new[] { "engage", "weapons", "online" } }
                    ),
                }
            );

            parser.Parse("engage online", null);

            var diag = parser.LastParseDiagnostics;
            Assert.AreEqual(1, diag.Length);
            Assert.AreEqual(
                "weapons_up",
                diag[0].TiedRivalIntent,
                "the coin flip is still reported, exactly as it was before item 3"
            );
            Assert.AreEqual(0, diag[0].TiedRivalPatternIndex);
            Assert.IsTrue(
                diag[0].TiedRivalIsSibling,
                "provably siblings — unnameable is not the same as not a sibling"
            );

            // …and it is still refused as a choice, because it cannot be phrased as one.
            Assert.AreEqual(
                0,
                parser.TiedSiblingBuffer[0].RivalCount,
                "a tie we cannot phrase a question about is not one we ask about"
            );
        }

        [Test]
        public void LastParseDiagnostics_TieInRoundOne_DoesNotLeakIntoRoundTwo()
        {
            // The first multi-round test in which round 1 actually RECORDS a rival. The only
            // multi-round diagnostic test before it parsed a grammar with no sibling sets, so
            // nothing was ever recorded in either round and the hand-off between rounds went
            // unexercised (issue #93).
            //
            // What this does NOT pin, said plainly because issue #93 asked for it and the
            // premise did not survive contact: ParseInternal declares its tie locals inside the
            // extraction loop, and hoisting them OUT is unobservable. The clear-on-adopt block
            // in the Better branch resets all eight of them whenever a new incumbent is taken,
            // and a round that appends a diagnostic entry has taken one by definition —
            // bestScore starts at float.MinValue, so the first admissible candidate always
            // adopts, and a round that adopts nothing breaks before appending. The per-round
            // declaration is belt-and-braces over clear-on-adopt, not the rule enforcing it.
            // Confirmed by hoisting the three exemplar locals out of the loop and re-running
            // both suites green. So this test pins the BEHAVIOUR — round 2 reports the tie it
            // had, which is none — and no test can pin the declaration site.
            //
            // Two rounds, and only the first is a coin flip. "set alpha on" drops the
            // discriminator and ties set_mode against set_level; "cease fire" that follows it
            // is unambiguous. The tied round comes FIRST, which is the order a leak would show
            // in, and what puts it there is the start-index key: CompareCandidate returns on
            // `startIdx != bestStartIdx` before it ever reads a score, so the sibling pair
            // adopted at index 0 refuses "cease fire" at index 3 without the two scores being
            // compared at all. Score enters only as the > 0 admission floor.
            //
            // Said precisely because the arithmetic invites the wrong reading: the pair does
            // also outscore "cease fire" here (0.75 against 0.4, which pays coverage for the
            // three tokens it skips to reach itself), and that is a coincidence this fixture
            // does not rest on. A change that sank the pair below 0.4 would NOT reorder the
            // rounds; one that pushed it to zero would, by dropping it from the round entirely.
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
                    new VoxrCommandDefinition("cease_fire", new[] { new[] { "cease", "fire" } }),
                }
            );

            var results = parser.Parse("set alpha on cease fire", null);

            Assert.AreEqual(2, results.Length, "two commands extracted, in two rounds");
            Assert.AreEqual("set_mode", results[0].Command.Intent);
            Assert.AreEqual("cease_fire", results[1].Command.Intent);

            var diag = parser.LastParseDiagnostics;
            Assert.AreEqual(2, diag.Length);
            Assert.AreEqual(
                "set_level",
                diag[0].TiedRivalIntent,
                "round 1 is the coin flip, and is still reported as one"
            );

            Assert.IsNull(
                diag[1].TiedRivalIntent,
                "round 2 had one candidate — a rival held over from round 1 would name a "
                    + "pattern that never competed for this command"
            );
            Assert.AreEqual(-1, diag[1].TiedRivalPatternIndex);
            Assert.IsFalse(diag[1].TiedRivalIsSibling);
            Assert.IsNull(diag[1].DescribeTiedRival());
        }

        // ---------- The barred round and the runner-up (issue #144) ----------
        //
        // Neither field changes what fires. They exist because the session log could not
        // answer two questions a field report has to answer: what the leading-required-miss
        // bar refused (issue #124 left the round completely silent, which made the bar's cost
        // unmeasurable), and what the round's second choice was.

        [Test]
        public void LastBarredRounds_LeadingRequiredMiss_RecordsTheRoundWithoutAResult()
        {
            // launch_weapon's first pattern is ["launch","{?quantity}","{weapon}","target",
            // "{target}"], so its anchor is the required literal "launch". Dropping just that
            // word leaves the whole tail matching: {weapon}=missiles, the literal "target" and
            // {target}="hotel one" are three matched required elements against the one missed
            // anchor, so the candidate is admissible (missed <= matched, CompareCandidate) and
            // scores 3/4 with no coverage charge — it starts at the round origin and consumes
            // every token. It therefore WINS the round and is then refused by the bar, which is
            // the shape this pins. A grammar that merely failed to match would record nothing
            // here at all, so the two are not interchangeable.
            var parser = CreateParser();
            var results = parser.Parse("missiles target hotel one", null);

            var barred = parser.LastBarredRounds;
            Assert.IsNotNull(barred);
            Assert.AreEqual(1, barred.Length, "one round ran, and the bar refused it");
            Assert.AreEqual("launch_weapon", barred[0].Intent);
            Assert.AreEqual(
                "launch {?quantity} {weapon} target {target}",
                barred[0].PatternString,
                "the pattern the bar refused, joined exactly as an emitting round's is"
            );
            Assert.Greater(
                barred[0].Score,
                0f,
                "a barred round WON its selection — a zero score would mean it never competed"
            );
            Assert.AreEqual(0, barred[0].StartIdx);
            Assert.AreEqual(4, barred[0].EndIdx, "half-open, and the span it consumed");
            Assert.AreEqual(0, barred[0].ResultsBefore, "nothing had emitted ahead of it");

            // The real point. A barred round is recorded WITHOUT becoming a result: the
            // _resultBuf <-> LastParseDiagnostics 1:1 alignment is what both the recogniser's
            // BuildAttempt(cmd, parseDiag, i, ...) and VoxrBatchTestRunner index by, so putting
            // the barred round into LastParseDiagnostics would have shifted every entry after
            // it onto the wrong command. If this pair ever goes non-zero, that contract broke.
            Assert.AreEqual(0, results.Length, "the bar produced no command");
            Assert.AreEqual(
                0,
                parser.LastParseDiagnostics.Length,
                "and no diagnostic entry, because there is no result for one to describe"
            );
        }

        [Test]
        public void LastBarredRounds_AfterACleanParse_IsEmptyNotStale()
        {
            // Every exit from a parse must leave LastBarredRounds describing THAT parse. The
            // failure this pins is silent and reads as a real finding: an utterance that barred
            // nothing would report the previous utterance's refused round, and a consumer
            // holding a zero result count alongside it has no way to tell.
            var parser = CreateParser();

            parser.Parse("missiles target hotel one", null);
            Assert.AreEqual(1, parser.LastBarredRounds.Length, "precondition: a round was barred");

            var results = parser.Parse("shoot missiles", null);
            Assert.AreEqual(1, results.Length, "precondition: this one matches cleanly");
            Assert.IsNotNull(parser.LastBarredRounds);
            Assert.AreEqual(
                0,
                parser.LastBarredRounds.Length,
                "a parse that barred nothing must not report the previous parse's barred round"
            );

            // …and again through the early return in Parse(string, VoxrWord[]) itself, which
            // clears the array without ever entering ParseInternal. Re-barred first so the
            // array being cleared is genuinely non-empty going in.
            parser.Parse("missiles target hotel one", null);
            Assert.AreEqual(1, parser.LastBarredRounds.Length, "precondition: barred again");

            parser.Parse("   ", null);
            Assert.IsNotNull(
                parser.LastBarredRounds,
                "the whitespace early return must leave an array, not null"
            );
            Assert.AreEqual(0, parser.LastBarredRounds.Length);
        }

        [Test]
        public void LastParseDiagnostics_TwoCandidates_NamesTheRunnerUpAndItsScore()
        {
            // Two candidates at the same start index, both consuming the whole utterance, with
            // different scores — so second place is unambiguous and neither carries a coverage
            // charge (SkippedBefore is 0 at the round origin, and the trailing orphan run is 0
            // at tokens.Length).
            //
            //   set_mode  ["set","{ship}","on"]          all three match      -> 3/3 = 1.00
            //   set_level ["set","{ship}","level","on"]  "level" missed
            //                                            medially, "on" then
            //                                            matches              -> 3/4 = 0.75
            //
            // The miss is MEDIAL on purpose: a later match resets requiredAfterLastMatch, so
            // the candidate does not take the forced-orphan charge and the arithmetic above is
            // the whole of its score.
            var parser = new VoxrCommandParser(
                ShipSlots(),
                new[]
                {
                    new VoxrCommandDefinition(
                        "set_mode",
                        new[] { new[] { "set", "{ship}", "on" } }
                    ),
                    new VoxrCommandDefinition(
                        "set_level",
                        new[] { new[] { "set", "{ship}", "level", "on" } }
                    ),
                }
            );

            var results = parser.Parse("set alpha on", null);

            Assert.AreEqual(1, results.Length);
            Assert.AreEqual("set_mode", results[0].Command.Intent, "the full match wins");

            var diag = parser.LastParseDiagnostics;
            Assert.AreEqual(1, diag.Length);
            Assert.AreEqual(
                "set_level",
                diag[0].RunnerUpIntent,
                "what would have won had set_mode not been there"
            );
            Assert.AreEqual(
                0.75f,
                diag[0].RunnerUpScore,
                1e-4f,
                "the RUNNER-UP's score — copying the winner's would read 1.00 here"
            );
            Assert.Less(
                diag[0].RunnerUpScore,
                results[0].Command.Score,
                "and it is second place, not the winner under another name"
            );

            // Nothing tied: 0.75 is not 1.00. The two records answer different questions and
            // this is the half that shows a runner-up is recorded however far behind it
            // finished, where a tied rival would not be recorded at all.
            Assert.IsNull(diag[0].TiedRivalIntent);
        }

        [Test]
        public void LastParseDiagnostics_SingleCandidate_RecordsNoRunnerUp()
        {
            // The negative. Second place must be absent, not zero: a runner-up slot seeded with
            // float.MinValue and published unguarded would put a nonsense score in the log, and
            // -1 is what every consumer tests for.
            //
            // A one-element pattern over a one-token utterance is the only shape with a single
            // candidate — the selection loop tries every pattern at every start index, so even
            // "cease fire" offers ["cease","fire"] started at token 1 as an admissible second.
            var parser = new VoxrCommandParser(
                Array.Empty<VoxrSlotDefinition>(),
                new[] { new VoxrCommandDefinition("engage", new[] { new[] { "engage" } }) }
            );

            var results = parser.Parse("engage", null);

            Assert.AreEqual(1, results.Length);

            var diag = parser.LastParseDiagnostics;
            Assert.AreEqual(1, diag.Length);
            Assert.IsNull(diag[0].RunnerUpIntent, "one candidate, so there is no second");
            Assert.AreEqual(-1f, diag[0].RunnerUpScore);
        }

        [Test]
        public void LastParseDiagnostics_RunnerUpInRoundOne_DoesNotLeakIntoRoundTwo()
        {
            // The same hand-off LastParseDiagnostics_TieInRoundOne_DoesNotLeakIntoRoundTwo pins
            // for the tied rival, for the runner-up: selection restarts per extraction round, so
            // round 1's second place is not a fact about round 2. Hoisting the runner-up locals
            // out of the round loop compiles and passes every single-round test above.
            //
            // Round 1 is "set alpha on", where set_level is second at 0.75 behind set_mode's
            // 1.00. Round 2 is "engage", whose only other candidates — the two set_* patterns
            // probed at token 3 — miss their anchor AND their required {ship} slot, so both are
            // refused by the score floor before the runner-up slot is ever offered them.
            var parser = new VoxrCommandParser(
                ShipSlots(),
                new[]
                {
                    new VoxrCommandDefinition(
                        "set_mode",
                        new[] { new[] { "set", "{ship}", "on" } }
                    ),
                    new VoxrCommandDefinition(
                        "set_level",
                        new[] { new[] { "set", "{ship}", "level", "on" } }
                    ),
                    new VoxrCommandDefinition("engage", new[] { new[] { "engage" } }),
                }
            );

            var results = parser.Parse("set alpha on engage", null);

            Assert.AreEqual(2, results.Length, "two commands extracted, in two rounds");
            Assert.AreEqual("set_mode", results[0].Command.Intent);
            Assert.AreEqual("engage", results[1].Command.Intent);

            var diag = parser.LastParseDiagnostics;
            Assert.AreEqual(2, diag.Length);
            Assert.AreEqual("set_level", diag[0].RunnerUpIntent, "round 1 had a second choice");

            Assert.IsNull(
                diag[1].RunnerUpIntent,
                "round 2 had one candidate — a runner-up held over from round 1 would name a "
                    + "pattern that never competed for this command"
            );
            Assert.AreEqual(-1f, diag[1].RunnerUpScore);
        }

        [Test]
        public void LastParseDiagnostics_TiedRivalAndRunnerUp_AreRecordedIndependently()
        {
            // The two fields are computed separately and neither suppresses the other, which is
            // only observable where they DISAGREE — and this fixture is the one shape that
            // makes them disagree, so it is worth saying why it does.
            //
            // It is LastParseDiagnostics_ShadowedRival_IsTheCrossIntentOne's grammar. On
            // "set alpha on" three candidates tie at 0.75 from token 0: set_mode pattern 0
            // (the winner on registration order), set_mode pattern 1, and set_level pattern 0.
            //
            //   - The TIED RIVAL is an exemplar chosen by KIND, not by rank: a sibling rival
            //     displaces a non-sibling one however late it arrives, so set_mode's own second
            //     phrasing is passed over for set_level, the cross-intent hazard.
            //   - The RUNNER-UP is chosen by RANK alone, by the same CompareCandidate order
            //     selection used. set_mode pattern 1 at token 0 reaches the slot first and
            //     outranks what was there (the same patterns probed at token 1, which lose on
            //     start index); set_level then ties it on every key, and Tied is not Better, so
            //     the slot is not handed over. Second by rank is set_mode pattern 1.
            //
            // So one field names set_level and the other names set_mode on the same round.
            // Deriving either from the other would collapse them onto one answer here.
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

            var results = parser.Parse("set alpha on", null);

            Assert.AreEqual(1, results.Length);
            Assert.AreEqual("set_mode", results[0].Command.Intent);

            var diag = parser.LastParseDiagnostics;
            Assert.AreEqual(1, diag.Length);
            Assert.AreEqual(
                "set_level",
                diag[0].TiedRivalIntent,
                "the tie exemplar still prefers the cross-intent rival"
            );
            Assert.IsTrue(diag[0].TiedRivalIsSibling);

            Assert.AreEqual(
                "set_mode",
                diag[0].RunnerUpIntent,
                "and second by RANK is the winner's own second phrasing, which tied first"
            );
            Assert.AreEqual(
                results[0].Command.Score,
                diag[0].RunnerUpScore,
                1e-6f,
                "an exact tie, so the runner-up carries the winner's score — pinned against the "
                    + "winner rather than a literal so a scoring change cannot make it stale"
            );
        }
    }
}
