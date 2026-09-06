using System.Collections.Generic;
using System.Globalization;
using NUnit.Framework;
using UnityEngine;
using VoXR;
using VoXR.Commands;

namespace VoXR.Tests.Editor
{
    public class VoxrCommandRecogniserDiagnosticTests
    {
        GameObject _go;
        VoxrCommandRecogniser _recogniser;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("DiagTestRecogniser");
            _recogniser = _go.AddComponent<VoxrCommandRecogniser>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null)
                Object.DestroyImmediate(_go);
        }

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

        void ConfigureSync()
        {
            _recogniser.Configure(MakeSlots(), MakeCommands());
            _recogniser.BufferWindow = 0f;
            _recogniser.CommandCooldown = 0f;
        }

        // 4.1
        [Test]
        public void LastMatchDiagnostics_UpdatedAfterParse()
        {
            ConfigureSync();
            _recogniser.InjectText("cease fire");

            var diag = _recogniser.LastMatchDiagnostics;
            Assert.AreEqual("cease fire", diag.InputText);
        }

        // 4.2
        [Test]
        public void NoMatch_CreatesSingleAttempt_WithNoMatchReason()
        {
            ConfigureSync();
            _recogniser.InjectText("hello world");

            var diag = _recogniser.LastMatchDiagnostics;
            Assert.AreEqual(1, diag.Attempts.Length);
            Assert.IsNull(diag.Attempts[0].Intent);
            Assert.AreEqual("no match", diag.Attempts[0].RejectReason);
            Assert.IsFalse(diag.Attempts[0].IsAccepted);

            // Issue #144's regression guard, extending this test rather than duplicating its
            // fixture. Since a fully-barred utterance publishes its barred attempts INSTEAD of
            // the synthetic entry, "no match" now means what it plainly says again: no round
            // produced a result and no round was barred. Nothing matched here at all, so this
            // is the branch that must still be reached — and the flag is what tells the two
            // apart in an exported log.
            Assert.IsFalse(
                diag.Attempts[0].Barred,
                "nothing matched, so nothing was refused by the bar either"
            );
            Assert.IsNull(
                diag.Attempts[0].RunnerUpIntent,
                "the synthetic entry comes from no parse round, so it has no second place"
            );
            Assert.AreEqual(-1f, diag.Attempts[0].RunnerUpScore);
        }

        // 4.3
        [Test]
        public void ScoreRejection_ReasonFormat()
        {
            ConfigureSync();
            // "launch missiles" partially matches the 5-element pattern: "target" is missed
            // and {target} is an unfilled required slot, giving (1 + 1 + 0 - 1) / 4 = 0.25.
            // The denominator is 4, not 5, because the unspoken {?quantity} is optional and
            // leaves both sides of the ratio.
            // Default minScore is 0.6, so it stays rejected — the slot miss is what sinks it,
            // and issue #65 §5.1 left RequiredSlotMissPenalty alone.
            _recogniser.InjectText("launch missiles");

            var diag = _recogniser.LastMatchDiagnostics;
            Assert.AreEqual(1, diag.Attempts.Length);
            Assert.IsFalse(diag.Attempts[0].IsAccepted);
            StringAssert.Contains("score", diag.Attempts[0].RejectReason);
            StringAssert.Contains("minScore", diag.Attempts[0].RejectReason);
        }

        // 4.4
        [Test]
        public void ConfidenceRejection_ReasonFormat()
        {
            ConfigureSync();
            // "cease fire" matches perfectly (score 1.0) but low confidence words.
            // Default minConfidence is 0.4, so 0.2 triggers rejection.
            var words = VoxrSpeechRecogniser.CreateSimulatedWords("cease fire", 0.2f);
            _recogniser.InjectText("cease fire", words);

            var diag = _recogniser.LastMatchDiagnostics;
            Assert.AreEqual(1, diag.Attempts.Length);
            Assert.IsFalse(diag.Attempts[0].IsAccepted);
            StringAssert.Contains("confidence", diag.Attempts[0].RejectReason);
            StringAssert.Contains("minConfidence", diag.Attempts[0].RejectReason);
        }

        // 4.5
        [Test]
        public void DebounceRejection_Reason()
        {
            _recogniser.Configure(MakeSlots(), MakeCommands());
            _recogniser.BufferWindow = 0f;
            _recogniser.CommandCooldown = 1.0f;

            // First injection is accepted.
            _recogniser.InjectText("cease fire");
            // Second injection within same frame → debounced.
            _recogniser.InjectText("cease fire");

            var diag = _recogniser.LastMatchDiagnostics;
            Assert.AreEqual(1, diag.Attempts.Length);
            Assert.IsFalse(diag.Attempts[0].IsAccepted);
            StringAssert.Contains("debounced", diag.Attempts[0].RejectReason);
        }

        // 4.6
        [Test]
        public void AcceptedCommand_NoRejection()
        {
            ConfigureSync();
            _recogniser.InjectText("cease fire");

            var diag = _recogniser.LastMatchDiagnostics;
            Assert.AreEqual(1, diag.Attempts.Length);
            Assert.IsTrue(diag.Attempts[0].IsAccepted);
            Assert.IsNull(diag.Attempts[0].RejectReason);
            Assert.AreEqual("cease_fire", diag.Attempts[0].Intent);
        }

        // Issue #77. Documentation~/scoring.md publishes this reason string and its accepted
        // flag in the session-log table, so pin both here — the page should fail a test rather
        // than a reader. It is the follow-up path's second outcome: a fill that leaves a
        // required slot outstanding keeps the command pending instead of firing it.
        [Test]
        public void FollowUpPartialFill_LogsStillPending_NotAccepted()
        {
            _recogniser.Configure(
                new[]
                {
                    new VoxrSlotDefinition("weapon", new[] { "missiles", "torpedoes" }),
                    new VoxrSlotDefinition("target", new[] { "hotel one", "hotel two" }),
                },
                new[]
                {
                    new VoxrCommandDefinition(
                        "launch_weapon",
                        new[]
                        {
                            new[] { "launch", "{weapon}", "at", "{target}", "on", "my", "mark" },
                        },
                        allowPartialMatch: true
                    ),
                }
            );
            _recogniser.BufferWindow = 0f;
            _recogniser.CommandCooldown = 0f;
            _recogniser.PendingTimeout = 30f;

            // Both slots stranded, so the follow-up below can fill one and still leave one.
            _recogniser.InjectText("launch at on my mark");
            Assert.IsTrue(_recogniser.HasPendingCommand, "precondition: pending with two gaps");

            _recogniser.InjectText("missiles");

            var diag = _recogniser.LastMatchDiagnostics;
            Assert.AreEqual(
                1,
                diag.Attempts.Length,
                "the follow-up path logs one synthetic attempt"
            );
            Assert.AreEqual("launch_weapon", diag.Attempts[0].Intent);
            Assert.IsFalse(diag.Attempts[0].IsAccepted, "the command did not fire");
            StringAssert.Contains("still pending", diag.Attempts[0].RejectReason);
            StringAssert.Contains(
                "target",
                diag.Attempts[0].RejectReason,
                "and it names the slot still outstanding, not the one just filled"
            );
            StringAssert.DoesNotContain("weapon", diag.Attempts[0].RejectReason);

            // The completing fill takes the other branch of the same attempt: accepted, no reason.
            _recogniser.InjectText("hotel one");

            diag = _recogniser.LastMatchDiagnostics;
            Assert.AreEqual(1, diag.Attempts.Length);
            Assert.IsTrue(diag.Attempts[0].IsAccepted);
            Assert.IsNull(diag.Attempts[0].RejectReason);
        }

        // 4.7
        [Test]
        public void PerSlotConfidence_Computed()
        {
            ConfigureSync();
            var words = new[]
            {
                new VoxrWord("shoot", 0.9f, 0f, 0.3f),
                new VoxrWord("missiles", 0.75f, 0.3f, 0.6f),
            };
            _recogniser.InjectText("shoot missiles", words);

            var diag = _recogniser.LastMatchDiagnostics;
            Assert.AreEqual(1, diag.Attempts.Length);
            Assert.IsTrue(diag.Attempts[0].IsAccepted);
            Assert.AreEqual(1, diag.Attempts[0].Slots.Length);

            var slot = diag.Attempts[0].Slots[0];
            Assert.AreEqual("weapon", slot.Name);
            Assert.AreEqual("missiles", slot.Value);
            Assert.AreEqual(0.75f, slot.Confidence, 1e-5f);
        }

        // 4.8
        [Test]
        public void PerSlotConfidence_NegativeOneForInjectedText()
        {
            ConfigureSync();
            // No word data → confidence unavailable
            _recogniser.InjectText("shoot missiles");

            var diag = _recogniser.LastMatchDiagnostics;
            Assert.AreEqual(1, diag.Attempts.Length);
            Assert.AreEqual(1, diag.Attempts[0].Slots.Length);
            Assert.AreEqual(-1f, diag.Attempts[0].Slots[0].Confidence);
        }

        // 4.9
        [Test]
        public void MultipleCommands_MultipleAttempts()
        {
            ConfigureSync();
            // Sequential extraction: "cease fire" + "shoot missiles"
            _recogniser.InjectText("cease fire shoot missiles");

            var diag = _recogniser.LastMatchDiagnostics;
            Assert.AreEqual(2, diag.Attempts.Length);
            Assert.AreEqual("cease_fire", diag.Attempts[0].Intent);
            Assert.AreEqual("launch_weapon", diag.Attempts[1].Intent);
            Assert.IsTrue(diag.Attempts[0].IsAccepted);
            Assert.IsTrue(diag.Attempts[1].IsAccepted);
        }

        // 4.10
        [Test]
        public void Frame_SetToTimeFrameCount()
        {
            ConfigureSync();
            _recogniser.InjectText("cease fire");

            var diag = _recogniser.LastMatchDiagnostics;
            Assert.AreEqual(Time.frameCount, diag.Frame);
        }

        // 4.12
        [Test]
        public void LastPartialResult_EmptyStringHandling()
        {
            // Assign SpeechRecogniser after AddComponent so the setter subscribes
            // while the component is already active (Edit Mode OnEnable is unreliable).
            var go = new GameObject("PartialResultTest");
            var speech = go.AddComponent<VoxrSpeechRecogniser>();
            var recogniser = go.AddComponent<VoxrCommandRecogniser>();
            recogniser.SpeechRecogniser = speech;

            recogniser.Configure(MakeSlots(), MakeCommands());
            recogniser.BufferWindow = 0f;
            recogniser.CommandCooldown = 0f;

            speech.InjectPartialResult("");

            Assert.AreEqual("", recogniser.LastPartialResult);
            Object.DestroyImmediate(go);
        }

        // 4.13
        [Test]
        public void DiagnosticsPublished_FiresPerUtterance_WithSenderAndDiagnostics()
        {
            ConfigureSync();

            var senders = new List<VoxrCommandRecogniser>();
            var published = new List<VoxrMatchDiagnostics>();
            void Handler(VoxrCommandRecogniser s, VoxrMatchDiagnostics d)
            {
                senders.Add(s);
                published.Add(d);
            }

            VoxrCommandRecogniser.DiagnosticsPublished += Handler;
            try
            {
                _recogniser.InjectText("cease fire");
                _recogniser.InjectText("hello world");
            }
            finally
            {
                VoxrCommandRecogniser.DiagnosticsPublished -= Handler;
            }

            Assert.AreEqual(2, published.Count);
            Assert.AreSame(_recogniser, senders[0]);
            Assert.AreEqual("cease fire", published[0].InputText);
            Assert.AreEqual("hello world", published[1].InputText);
        }

        // 4.14
        [Test]
        public void DiagnosticsPublished_MatchesLastMatchDiagnostics()
        {
            ConfigureSync();

            VoxrMatchDiagnostics published = default;
            void Handler(VoxrCommandRecogniser s, VoxrMatchDiagnostics d) => published = d;

            VoxrCommandRecogniser.DiagnosticsPublished += Handler;
            try
            {
                _recogniser.InjectText("cease fire");
            }
            finally
            {
                VoxrCommandRecogniser.DiagnosticsPublished -= Handler;
            }

            var last = _recogniser.LastMatchDiagnostics;
            Assert.AreEqual(last.InputText, published.InputText);
            Assert.AreEqual(last.Frame, published.Frame);
            Assert.AreSame(last.Attempts, published.Attempts);
        }

        // ---------- Barred rounds in the log (issue #144) ----------
        //
        // Issue #124's bar refuses a round that matched none of its pattern's first required
        // element. It logged nothing at all, so a field report could not distinguish "the bar
        // saved you from a misfire" from "the decoder heard nothing" — both read as one
        // synthetic "no match". These pin the refused round appearing, in the right place, with
        // no behaviour change around it.

        // The issue's headline acceptance criterion.
        //
        // "missiles target hotel one" drops only the anchor literal of launch_weapon's first
        // pattern, ["launch","{?quantity}","{weapon}","target","{target}"]. The tail still
        // matches — {weapon}, the literal "target", {target} — so three matched required
        // elements stand against the one missed anchor, the candidate is admissible, it wins
        // the round outright, and the bar then refuses it. That is a barred round, not a
        // failure to match, and the parser-level test of the same fixture
        // (LastBarredRounds_LeadingRequiredMiss_RecordsTheRoundWithoutAResult) is what
        // establishes it independently of this layer.
        [Test]
        public void BarredRound_PublishesABarredAttempt_AndNoNoMatchEntry()
        {
            ConfigureSync();

            var fired = new List<VoxrCommand>();
            var unrecognised = new List<string>();
            _recogniser.OnCommandRecognised += fired.Add;
            _recogniser.OnUnrecognisedSpeech += unrecognised.Add;

            _recogniser.InjectText("missiles target hotel one");

            var diag = _recogniser.LastMatchDiagnostics;
            Assert.AreEqual(1, diag.Attempts.Length, "one round ran, and it was barred");

            var a = diag.Attempts[0];
            Assert.IsTrue(a.Barred);
            Assert.IsFalse(a.IsAccepted);
            Assert.AreEqual("barred", a.RejectReason);
            Assert.AreNotEqual(
                "no match",
                a.RejectReason,
                "the synthetic entry must not be written for a round that WAS refused — saying "
                    + "nothing matched is the report that hid this shape in the first place"
            );
            Assert.AreEqual("launch_weapon", a.Intent, "the candidate the bar refused is named");
            Assert.AreEqual("launch {?quantity} {weapon} target {target}", a.Pattern);
            Assert.Greater(a.Score, 0f, "it won its round before the bar took it");
            Assert.AreEqual(
                0,
                a.Slots.Length,
                "the parser returns above the slot-array build, so a barred attempt carries none"
            );

            // No behaviour change. These two are the whole of what a barred utterance did
            // before issue #144, and both must still be exactly true.
            Assert.AreEqual(0, fired.Count, "a barred round fires no command");
            CollectionAssert.AreEqual(
                new[] { "missiles target hotel one" },
                unrecognised,
                "and OnUnrecognisedSpeech still fires — issue #124 settled that, and this is a "
                    + "diagnostics change only"
            );
        }

        [Test]
        public void BarredThenEmittingRound_AttemptsAreInRoundOrder()
        {
            ConfigureSync();

            var fired = new List<VoxrCommand>();
            _recogniser.OnCommandRecognised += fired.Add;

            // Round 1 is the barred launch_weapon above; round 2 then matches "cease fire"
            // cleanly from where round 1's consumed span ended. The equivalent of the docs'
            // canonical "target hotel one cease fire" on this fixture's grammar.
            _recogniser.InjectText("missiles target hotel one cease fire");

            var diag = _recogniser.LastMatchDiagnostics;
            Assert.AreEqual(2, diag.Attempts.Length);

            // This is what BarredRoundEntry.ResultsBefore buys. The barred rounds live in a
            // parallel array — they have no slot in the result buffer, which is what keeps the
            // _resultBuf <-> LastParseDiagnostics 1:1 contract intact — so without the count of
            // results emitted ahead of each one, every barred attempt would be appended after
            // the accepted ones and the log would read in the wrong order.
            Assert.IsTrue(diag.Attempts[0].Barred, "round 1 was refused, and comes first");
            Assert.AreEqual("launch_weapon", diag.Attempts[0].Intent);
            Assert.AreEqual("barred", diag.Attempts[0].RejectReason);

            Assert.IsFalse(diag.Attempts[1].Barred);
            Assert.IsTrue(diag.Attempts[1].IsAccepted);
            Assert.AreEqual("cease_fire", diag.Attempts[1].Intent);

            Assert.AreEqual(1, fired.Count, "the barred round changed nothing about what fires");
            Assert.AreEqual("cease_fire", fired[0].Intent);
        }

        [Test]
        public void BarredRoundAfterTheLastResult_IsDrainedAtTheTail()
        {
            ConfigureSync();

            var fired = new List<VoxrCommand>();
            _recogniser.OnCommandRecognised += fired.Add;

            // The mirror image of the test above, and the branch the head drain structurally
            // cannot reach. "cease fire" wins round 1 outright at 1.00 and emits; round 2 then
            // starts at token 2 and is the barred launch_weapon — so the barred entry carries
            // ResultsBefore == 1 while _resultCount is 1.
            //
            // The head drain runs inside `for (i = 0; i < resultCount; i++)` and matches
            // ResultsBefore == i, so with one result it only ever offers i == 0. A round barred
            // after the LAST emitting one is reached by the tail drain and by nothing else:
            // delete that drain and this test does not merely weaken, it fails on the length —
            // the attempts array is [cease_fire] and the second index does not exist.
            _recogniser.InjectText("cease fire missiles target hotel one");

            var diag = _recogniser.LastMatchDiagnostics;
            Assert.AreEqual(
                2,
                diag.Attempts.Length,
                "the emitting round and the round barred after it — a trailing barred round "
                    + "dropped here is exactly the silent loss issue #144 exists to close"
            );

            Assert.IsFalse(diag.Attempts[0].Barred, "round 1 emitted, and comes first");
            Assert.IsTrue(diag.Attempts[0].IsAccepted);
            Assert.AreEqual("cease_fire", diag.Attempts[0].Intent);

            Assert.IsTrue(diag.Attempts[1].Barred, "round 2 was refused, and comes after it");
            Assert.AreEqual("barred", diag.Attempts[1].RejectReason);
            Assert.AreEqual("launch_weapon", diag.Attempts[1].Intent);
            Assert.AreEqual(
                "launch {?quantity} {weapon} target {target}",
                diag.Attempts[1].Pattern,
                "the anchor-less tail match, refused after cease_fire had already emitted"
            );

            Assert.AreEqual(1, fired.Count, "and what fires is untouched");
            Assert.AreEqual("cease_fire", fired[0].Intent);
        }

        [Test]
        public void TwoConsecutiveBarredRounds_ShareOneIndex_AndBothAppearInRoundOrder()
        {
            ConfigureSync();

            // The head drain's inner loop is a loop, not a lookup, precisely because several
            // barred rounds can carry the same ResultsBefore — consecutive bars all fire before
            // the next result is emitted. Nothing pinned that; a `break` after the first match,
            // or a dictionary keyed by ResultsBefore, would drop every bar but one and pass
            // every other test in this file.
            //
            // Both of launch_weapon's patterns bar here, in sequence, before anything emits:
            //
            //   round 1  tokens 0-3   "missiles target hotel one"  matches pattern 0's tail,
            //                         missing only the anchor "launch"          -> barred
            //   round 2  token  4     "missiles" matches pattern 1's {weapon},
            //                         missing only the anchor "shoot"           -> barred
            //   round 3  tokens 5-6   "cease fire"                              -> fires at 1.00
            //
            // So both barred entries carry ResultsBefore == 0 and the drain at i == 0 has to
            // emit both. They are told apart by PATTERN rather than intent — same command, two
            // phrasings — which is what makes their ORDER observable at all.
            _recogniser.InjectText("missiles target hotel one missiles cease fire");

            var diag = _recogniser.LastMatchDiagnostics;
            Assert.AreEqual(
                3,
                diag.Attempts.Length,
                "two barred rounds at the same index, then the one that emitted"
            );

            Assert.IsTrue(diag.Attempts[0].Barred);
            Assert.AreEqual("barred", diag.Attempts[0].RejectReason);
            Assert.AreEqual(
                "launch {?quantity} {weapon} target {target}",
                diag.Attempts[0].Pattern,
                "round 1's refused pattern"
            );

            Assert.IsTrue(diag.Attempts[1].Barred);
            Assert.AreEqual("barred", diag.Attempts[1].RejectReason);
            Assert.AreEqual(
                "shoot {weapon}",
                diag.Attempts[1].Pattern,
                "round 2's — a DIFFERENT pattern, so this pins the order and not just the count"
            );

            Assert.IsFalse(diag.Attempts[2].Barred);
            Assert.IsTrue(diag.Attempts[2].IsAccepted);
            Assert.AreEqual("cease_fire", diag.Attempts[2].Intent);
        }

        // Reject reasons embed scores, and they are read by a human out of an exported session
        // log — often one exported on a machine whose locale is not the reader's. A
        // comma-decimal Editor used to write "score 0,25 < minScore 0,60", which is not what
        // Documentation~/scoring.md publishes and is not what a log-parsing script expects.
        //
        // WHAT THIS TEST CANNOT DO FOR ITSELF: its red state depends entirely on
        // CultureInfo.CurrentCulture actually reaching float.ToString("F2") in whatever runtime
        // the Editor is using. Where it does not — globalization-invariant mode, or a stripped
        // ICU — a broken implementation would pass here for the wrong reason. So the culture is
        // proved to have taken effect BEFORE the real assertions run, and the test declares
        // itself unable rather than reporting a green it did not earn.
        [Test]
        public void RejectReason_UnderACommaDecimalCulture_FormatsNumbersInvariantly()
        {
            var original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");

                if ((0.6f).ToString("F2") != "0,60")
                {
                    Assert.Ignore(
                        "CultureInfo.CurrentCulture does not reach float formatting in this "
                            + "runtime, so this test cannot distinguish invariant formatting "
                            + "from no formatting problem at all. Treat the invariant "
                            + "formatting in VoxrCommandRecogniser as UNVERIFIED here."
                    );
                }

                ConfigureSync();

                // The same fixture as ScoreRejection_ReasonFormat: "launch missiles" matches the
                // 5-element pattern's anchor and weapon slot and misses the rest, landing below
                // the default 0.6 minScore.
                _recogniser.InjectText("launch missiles");

                var diag = _recogniser.LastMatchDiagnostics;
                Assert.AreEqual(1, diag.Attempts.Length);
                string reason = diag.Attempts[0].RejectReason;

                StringAssert.Contains(
                    "minScore 0.60",
                    reason,
                    "the default minScore, formatted with a dot under a comma-decimal culture"
                );
                StringAssert.DoesNotContain(
                    ",",
                    reason,
                    "no comma anywhere — a decimal comma is the whole failure being pinned, and "
                        + "this reason string carries no other commas to confuse it"
                );
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }
    }
}
