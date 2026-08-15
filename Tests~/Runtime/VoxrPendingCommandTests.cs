using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VoXR.Commands;

namespace VoXR.Tests.Runtime
{
    public class VoxrPendingCommandTests
    {
        GameObject _go;
        VoxrCommandRecogniser _recogniser;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("TestPendingCommands");
            _recogniser = _go.AddComponent<VoxrCommandRecogniser>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null)
                UnityEngine.Object.DestroyImmediate(_go);
        }

        // -------- Fixtures --------

        static VoxrSlotDefinition[] MakeSlots()
        {
            return new[]
            {
                new VoxrSlotDefinition("target",
                    new[] { "hotel one", "hotel two", "alpha one" },
                    new Dictionary<string, string>
                    {
                        { "h one", "hotel one" },
                        { "h two", "hotel two" },
                    }),
                new VoxrSlotDefinition("weapon",
                    new[] { "missiles", "torpedoes" }),
            };
        }

        static VoxrCommandDefinition[] MakeCommands(
            bool allowPartial = false, bool requiresConfirm = false)
        {
            return new[]
            {
                new VoxrCommandDefinition("launch_weapon", new[]
                {
                    new[] { "launch", "{weapon}", "target", "{target}" },
                }, allowPartial, requiresConfirm),
                new VoxrCommandDefinition("cease_fire", new[]
                {
                    new[] { "cease", "fire" },
                }),
            };
        }

        void ConfigureSync(bool allowPartial = false, bool requiresConfirm = false)
        {
            _recogniser.Configure(MakeSlots(), MakeCommands(allowPartial, requiresConfirm));
            _recogniser.BufferWindow = 0f;
            _recogniser.CommandCooldown = 0f;
            _recogniser.PendingTimeout = 30f; // Long timeout so tests control resolution
        }

        // ======== AllowPartialMatch Basics ========

        [Test]
        public void PartialMatch_WithoutFlag_Rejected()
        {
            ConfigureSync(allowPartial: false);

            string unrecognised = null;
            _recogniser.OnUnrecognisedSpeech += text => unrecognised = text;
            VoxrCommand? received = null;
            _recogniser.OnCommandRecognised += cmd => received = cmd;

            // "launch missiles" is missing {target} — score below minScore
            _recogniser.InjectText("launch missiles");

            Assert.IsFalse(received.HasValue, "Partial match should be rejected without AllowPartialMatch");
            Assert.IsNotNull(unrecognised);
        }

        [Test]
        public void PartialMatch_WithFlag_EntersPending()
        {
            ConfigureSync(allowPartial: true);

            VoxrCommand? pending = null;
            _recogniser.OnCommandPending += cmd => pending = cmd;
            VoxrCommand? recognised = null;
            _recogniser.OnCommandRecognised += cmd => recognised = cmd;

            _recogniser.InjectText("launch missiles target");

            Assert.IsTrue(pending.HasValue, "Partial match should enter pending");
            Assert.AreEqual("launch_weapon", pending.Value.Intent);
            Assert.IsTrue(pending.Value.HasSlot("weapon"));
            Assert.AreEqual("missiles", pending.Value.GetSlot("weapon"));
            Assert.IsFalse(recognised.HasValue, "Should not fire OnCommandRecognised yet");
            Assert.IsTrue(_recogniser.HasPendingCommand);
        }

        [Test]
        public void PartialMatch_AboveGateButIncomplete_EntersPendingInsteadOfFiring()
        {
            // Issue #73's routing half. The pending path is entered from BELOW minScore, so a
            // slot-missing candidate that cleared the gate never reached it — allowPartialMatch
            // was silently inapplicable to exactly the commands that scored well enough to fire
            // incomplete.
            //
            // The candidate sits clear of the gate rather than on it (issue #76): eight required
            // elements, seven matched and the trailing {target} stranded, so (7 x 1 - 1) / 8 =
            // 0.75. On the gate value itself the routing assertion would fail open — a scoring
            // change that dropped the candidate below minScore would still route it to pending,
            // for the ordinary below-gate reason, and prove nothing about completeness.
            _recogniser.Configure(
                new[]
                {
                    new VoxrSlotDefinition("target", new[] { "hotel one", "hotel two" }),
                    new VoxrSlotDefinition("weapon", new[] { "missiles", "torpedoes" }),
                    new VoxrSlotDefinition("quantity", new[] { "all", "one", "two" }),
                    new VoxrSlotDefinition("tube", new[] { "one", "two", "three" }),
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
                                "{quantity}",
                                "{weapon}",
                                "from",
                                "tube",
                                "{tube}",
                                "at",
                                "{target}",
                            },
                        },
                        allowPartialMatch: true
                    ),
                }
            );
            _recogniser.BufferWindow = 0f;
            _recogniser.CommandCooldown = 0f;
            _recogniser.PendingTimeout = 30f;

            VoxrCommand? pending = null;
            _recogniser.OnCommandPending += cmd => pending = cmd;
            VoxrCommand? recognised = null;
            _recogniser.OnCommandRecognised += cmd => recognised = cmd;

            _recogniser.InjectText("launch all missiles from tube three at");

            Assert.IsFalse(
                recognised.HasValue,
                "an incomplete command must not fire above the gate"
            );
            Assert.IsTrue(
                pending.HasValue,
                "it goes to slot-fill, which is where it always belonged"
            );
            // The pending command carries the parse score through untouched, so this pins the
            // hand-derived 0.75 without a second parser: it is the candidate that was routed.
            Assert.AreEqual(
                6f / 8f,
                pending.Value.Score,
                0.001f,
                "the hand-derived score no longer holds — re-derive it and argue the new value"
            );
            Assert.GreaterOrEqual(
                pending.Value.Score,
                _recogniser.MinScore,
                "and it must clear the gate the recogniser is running, or completeness is not "
                    + "what routed it"
            );
            Assert.AreEqual("missiles", pending.Value.GetSlot("weapon"));
            Assert.AreEqual("three", pending.Value.GetSlot("tube"));
            Assert.IsTrue(_recogniser.HasPendingCommand);

            _recogniser.InjectText("hotel one");

            Assert.IsTrue(recognised.HasValue, "and the follow-up completes it");
            Assert.AreEqual("hotel one", recognised.Value.GetSlot("target"));
        }

        [Test]
        public void IncompleteNewCommand_DoesNotCancelALivePending()
        {
            // The Step 4 half of #73, and the reason the completeness term has to be read twice.
            // hasCompleteNewCommand cancels any live pending command outright. Once an incomplete
            // command stops firing, letting it still set that flag would take the user's
            // half-finished command away and put nothing at all in its place.
            //
            // set_burn deliberately does NOT opt into partial matching, so the incomplete second
            // utterance is rejected rather than entering pending itself — which is what isolates
            // this to the cancellation and keeps it from passing for the wrong reason.
            //
            // Its pattern is eight elements rather than five (issue #76) so the incomplete
            // candidate lands clear of the gate at (7 x 1 - 1) / 8 = 0.75. On the gate value
            // itself this test fails open: a scoring change that pushed the candidate below
            // minScore would still leave the pending command alive — rejected on score, never
            // reaching the completeness term that Step 4 exists to apply.
            var slots = new[]
            {
                new VoxrSlotDefinition("target", new[] { "hotel one", "hotel two" }),
                new VoxrSlotDefinition("weapon", new[] { "missiles", "torpedoes" }),
                new VoxrSlotDefinition("burn_level", new[] { "coast", "hard burn" }),
            };
            var commands = new[]
            {
                new VoxrCommandDefinition(
                    "launch_weapon",
                    new[] { new[] { "launch", "{weapon}", "target", "{target}" } },
                    allowPartialMatch: true
                ),
                new VoxrCommandDefinition(
                    "set_burn",
                    new[]
                    {
                        new[] { "helm", "set", "burn", "to", "{burn_level}", "on", "my", "mark" },
                    }
                ),
            };

            // Nothing fires and nothing pends for set_burn, so no event carries its score —
            // pin it against the same grammar directly, or the assertions below cannot tell
            // "refused as incomplete" from "never cleared the gate".
            var probe = new VoxrCommandParser(slots, commands).Parse("helm set burn to on my mark");
            Assert.AreEqual(1, probe.Length);
            Assert.AreEqual("set_burn", probe[0].Command.Intent);
            Assert.AreEqual(
                6f / 8f,
                probe[0].Command.Score,
                0.001f,
                "the hand-derived score no longer holds — re-derive it and argue the new value"
            );
            Assert.GreaterOrEqual(
                probe[0].Command.Score,
                _recogniser.MinScore,
                "and it must clear the gate the recogniser is running"
            );
            Assert.IsFalse(probe[0].Command.HasSlot("burn_level"), "with {burn_level} stranded");

            _recogniser.Configure(slots, commands);
            _recogniser.BufferWindow = 0f;
            _recogniser.CommandCooldown = 0f;
            _recogniser.PendingTimeout = 30f;

            _recogniser.InjectText("launch missiles target");
            Assert.IsTrue(_recogniser.HasPendingCommand, "precondition: a pending command is live");

            int cancelledCount = 0;
            VoxrCommand? recognised = null;
            _recogniser.OnCommandCancelled += _ => cancelledCount++;
            _recogniser.OnCommandRecognised += cmd => recognised = cmd;

            _recogniser.InjectText("helm set burn to on my mark");

            Assert.IsFalse(recognised.HasValue, "the incomplete command must not fire");
            Assert.AreEqual(
                0,
                cancelledCount,
                "and must not evict a pending command it cannot replace"
            );
            Assert.IsTrue(_recogniser.HasPendingCommand);

            VoxrCommand? confirmed = null;
            _recogniser.OnCommandConfirmed += cmd => confirmed = cmd;
            _recogniser.InjectText("hotel one");

            Assert.IsTrue(confirmed.HasValue, "the pending command is still there to be completed");
            Assert.AreEqual("hotel one", confirmed.Value.GetSlot("target"));
        }

        [Test]
        public void PartialMatch_AllSlotsFilled_FiresNormally()
        {
            ConfigureSync(allowPartial: true);

            VoxrCommand? pending = null;
            _recogniser.OnCommandPending += cmd => pending = cmd;
            VoxrCommand? recognised = null;
            _recogniser.OnCommandRecognised += cmd => recognised = cmd;

            _recogniser.InjectText("launch missiles target hotel one");

            Assert.IsFalse(pending.HasValue, "Fully matched should not enter pending");
            Assert.IsTrue(recognised.HasValue, "Should fire normally");
            Assert.AreEqual("launch_weapon", recognised.Value.Intent);
        }

        [Test]
        public void PartialMatch_FollowUp_FillsSlot()
        {
            ConfigureSync(allowPartial: true);

            VoxrCommand? confirmed = null;
            _recogniser.OnCommandConfirmed += cmd => confirmed = cmd;
            VoxrCommand? recognised = null;
            _recogniser.OnCommandRecognised += cmd => recognised = cmd;

            _recogniser.InjectText("launch missiles target");
            Assert.IsTrue(_recogniser.HasPendingCommand);

            // Follow-up fills the missing {target} slot
            _recogniser.InjectText("hotel one");

            Assert.IsTrue(confirmed.HasValue, "Follow-up should confirm pending");
            Assert.AreEqual("launch_weapon", confirmed.Value.Intent);
            Assert.AreEqual("hotel one", confirmed.Value.GetSlot("target"));
            Assert.AreEqual("missiles", confirmed.Value.GetSlot("weapon"));
            Assert.IsTrue(recognised.HasValue, "Should also fire OnCommandRecognised");
            Assert.IsFalse(_recogniser.HasPendingCommand);
        }

        [Test]
        public void PartialMatch_FollowUp_WrongSlotValue_StaysPending()
        {
            ConfigureSync(allowPartial: true);

            VoxrCommand? confirmed = null;
            _recogniser.OnCommandConfirmed += cmd => confirmed = cmd;

            _recogniser.InjectText("launch missiles target");
            Assert.IsTrue(_recogniser.HasPendingCommand);

            // Follow-up with unrecognised text
            _recogniser.InjectText("something random");

            Assert.IsFalse(confirmed.HasValue, "Random speech should not complete pending");
            Assert.IsTrue(_recogniser.HasPendingCommand, "Should still be pending");
        }

        [Test]
        public void PartialMatch_TimeoutCancel_FiresCancelled()
        {
            ConfigureSync(allowPartial: true);
            _recogniser.PendingTimeout = 0.01f; // Very short timeout

            VoxrCommand? cancelled = null;
            _recogniser.OnCommandCancelled += cmd => cancelled = cmd;

            _recogniser.InjectText("launch missiles target");
            Assert.IsTrue(_recogniser.HasPendingCommand);

            // Simulate time passing via manual Update call
            // We need to wait for timeout — use reflection to set CreatedTime in the past
            ForceTimeoutNow();

            Assert.IsTrue(cancelled.HasValue, "Should fire OnCommandCancelled on timeout");
            Assert.AreEqual("launch_weapon", cancelled.Value.Intent);
            Assert.IsFalse(_recogniser.HasPendingCommand);
        }

        [Test]
        public void PartialMatch_TimeoutFireAsIs_FiresWithPartialSlots()
        {
            ConfigureSync(allowPartial: true);
            _recogniser.PendingTimeout = 0.01f;
            _recogniser.PendingTimeoutBehavior = VoxrPendingTimeoutBehavior.FireAsIs;

            VoxrCommand? confirmed = null;
            _recogniser.OnCommandConfirmed += cmd => confirmed = cmd;
            VoxrCommand? recognised = null;
            _recogniser.OnCommandRecognised += cmd => recognised = cmd;

            _recogniser.InjectText("launch missiles target");
            Assert.IsTrue(_recogniser.HasPendingCommand);

            ForceTimeoutNow();

            Assert.IsTrue(confirmed.HasValue, "Should fire OnCommandConfirmed");
            Assert.IsTrue(recognised.HasValue, "Should fire OnCommandRecognised");
            Assert.AreEqual("missiles", confirmed.Value.GetSlot("weapon"));
            Assert.IsFalse(confirmed.Value.HasSlot("target"), "Target should be unfilled");
            Assert.IsFalse(_recogniser.HasPendingCommand);
        }

        // ======== RequiresConfirmation Basics ========

        [Test]
        public void RequiresConfirmation_FullMatch_EntersPending()
        {
            ConfigureSync(requiresConfirm: true);

            VoxrCommand? pending = null;
            _recogniser.OnCommandPending += cmd => pending = cmd;
            VoxrCommand? recognised = null;
            _recogniser.OnCommandRecognised += cmd => recognised = cmd;

            _recogniser.InjectText("launch missiles target hotel one");

            Assert.IsTrue(pending.HasValue, "Should enter pending for confirmation");
            Assert.AreEqual("launch_weapon", pending.Value.Intent);
            Assert.IsFalse(recognised.HasValue, "Should not fire yet");
            Assert.IsTrue(_recogniser.HasPendingCommand);
        }

        [Test]
        public void RequiresConfirmation_Confirm_Fires()
        {
            ConfigureSync(requiresConfirm: true);

            VoxrCommand? confirmed = null;
            _recogniser.OnCommandConfirmed += cmd => confirmed = cmd;
            VoxrCommand? recognised = null;
            _recogniser.OnCommandRecognised += cmd => recognised = cmd;

            _recogniser.InjectText("launch missiles target hotel one");
            Assert.IsTrue(_recogniser.HasPendingCommand);

            _recogniser.InjectText("confirm");

            Assert.IsTrue(confirmed.HasValue, "Should fire OnCommandConfirmed");
            Assert.IsTrue(recognised.HasValue, "Should fire OnCommandRecognised");
            Assert.AreEqual("launch_weapon", confirmed.Value.Intent);
            Assert.IsFalse(_recogniser.HasPendingCommand);
        }

        [Test]
        public void RequiresConfirmation_Cancel_Cancels()
        {
            ConfigureSync(requiresConfirm: true);

            VoxrCommand? cancelled = null;
            _recogniser.OnCommandCancelled += cmd => cancelled = cmd;

            _recogniser.InjectText("launch missiles target hotel one");
            _recogniser.InjectText("cancel");

            Assert.IsTrue(cancelled.HasValue, "Should fire OnCommandCancelled");
            Assert.AreEqual("launch_weapon", cancelled.Value.Intent);
            Assert.IsFalse(_recogniser.HasPendingCommand);
        }

        [Test]
        public void RequiresConfirmation_AffirmativeConfirms()
        {
            ConfigureSync(requiresConfirm: true);

            VoxrCommand? confirmed = null;
            _recogniser.OnCommandConfirmed += cmd => confirmed = cmd;

            _recogniser.InjectText("launch missiles target hotel one");
            _recogniser.InjectText("affirmative");

            Assert.IsTrue(confirmed.HasValue, "Synonym 'affirmative' should confirm");
        }

        [Test]
        public void RequiresConfirmation_BelayThat_Cancels()
        {
            ConfigureSync(requiresConfirm: true);

            VoxrCommand? cancelled = null;
            _recogniser.OnCommandCancelled += cmd => cancelled = cmd;

            _recogniser.InjectText("launch missiles target hotel one");
            _recogniser.InjectText("belay that");

            Assert.IsTrue(cancelled.HasValue, "Multi-word 'belay that' should cancel");
        }

        [Test]
        public void RequiresConfirmation_CustomVocabulary()
        {
            ConfigureSync(requiresConfirm: true);
            _recogniser.ConfirmVocabulary = new[] { "execute" };
            _recogniser.CancelVocabulary = new[] { "stand down" };

            VoxrCommand? confirmed = null;
            _recogniser.OnCommandConfirmed += cmd => confirmed = cmd;
            VoxrCommand? cancelled = null;
            _recogniser.OnCommandCancelled += cmd => cancelled = cmd;

            // Default "confirm" should NOT work with custom vocabulary
            _recogniser.InjectText("launch missiles target hotel one");
            _recogniser.InjectText("confirm");
            Assert.IsFalse(confirmed.HasValue, "Default 'confirm' should not work with custom vocab");

            // Custom "execute" should work
            _recogniser.CancelPendingCommand(); // Reset
            cancelled = null;
            _recogniser.InjectText("launch missiles target hotel one");
            _recogniser.InjectText("execute");
            Assert.IsTrue(confirmed.HasValue, "Custom 'execute' should confirm");
        }

        // ======== Combined AllowPartialMatch + RequiresConfirmation ========

        [Test]
        public void Combined_PartialThenFollowUpThenConfirm()
        {
            ConfigureSync(allowPartial: true, requiresConfirm: true);

            var events = new List<string>();
            _recogniser.OnCommandPending += cmd => events.Add($"pending:{cmd.Intent}");
            _recogniser.OnCommandConfirmed += cmd => events.Add($"confirmed:{cmd.Intent}");
            _recogniser.OnCommandRecognised += cmd => events.Add($"recognised:{cmd.Intent}");

            // Step 1: Partial match enters pending
            _recogniser.InjectText("launch missiles target");
            Assert.AreEqual(1, events.Count);
            Assert.AreEqual("pending:launch_weapon", events[0]);

            // Step 2: Follow-up fills slot — but RequiresConfirmation means it re-enters pending
            _recogniser.InjectText("hotel one");
            Assert.AreEqual(2, events.Count);
            Assert.AreEqual("pending:launch_weapon", events[1]);
            Assert.IsTrue(_recogniser.HasPendingCommand);

            // Step 3: Confirm fires
            _recogniser.InjectText("confirm");
            Assert.AreEqual(4, events.Count);
            Assert.AreEqual("confirmed:launch_weapon", events[2]);
            Assert.AreEqual("recognised:launch_weapon", events[3]);
            Assert.IsFalse(_recogniser.HasPendingCommand);
        }

        // ======== Arbitration ========

        [Test]
        public void Pending_NewCompleteCommand_PreemptsPending()
        {
            ConfigureSync(requiresConfirm: true);

            VoxrCommand? cancelled = null;
            _recogniser.OnCommandCancelled += cmd => cancelled = cmd;
            VoxrCommand? recognised = null;
            _recogniser.OnCommandRecognised += cmd => recognised = cmd;

            // launch_weapon enters pending (requires confirmation)
            _recogniser.InjectText("launch missiles target hotel one");
            Assert.IsTrue(_recogniser.HasPendingCommand);

            // "cease fire" is a complete command — should preempt pending
            _recogniser.InjectText("cease fire");

            Assert.IsTrue(cancelled.HasValue, "Pending should be cancelled");
            Assert.AreEqual("launch_weapon", cancelled.Value.Intent);
            Assert.IsTrue(recognised.HasValue, "New command should fire");
            Assert.AreEqual("cease_fire", recognised.Value.Intent);
            Assert.IsFalse(_recogniser.HasPendingCommand);
        }

        [Test]
        public void Pending_FollowUpOnly_FollowUpWins()
        {
            ConfigureSync(allowPartial: true);

            VoxrCommand? confirmed = null;
            _recogniser.OnCommandConfirmed += cmd => confirmed = cmd;

            _recogniser.InjectText("launch missiles target");
            _recogniser.InjectText("hotel one");

            Assert.IsTrue(confirmed.HasValue, "Follow-up should complete pending");
            Assert.AreEqual("hotel one", confirmed.Value.GetSlot("target"));
        }

        [Test]
        public void ConfirmCancel_NoPending_PassesThrough()
        {
            ConfigureSync();

            string unrecognised = null;
            _recogniser.OnUnrecognisedSpeech += text => unrecognised = text;

            _recogniser.InjectText("confirm");

            Assert.IsNotNull(unrecognised,
                "'confirm' with no pending should pass through as unrecognised");
        }

        // ======== Deferred Grammar Rebuild ========

        [Test]
        public void RebuildGrammar_DuringPending_Deferred()
        {
            ConfigureSync(requiresConfirm: true);

            _recogniser.InjectText("launch missiles target hotel one");
            Assert.IsTrue(_recogniser.HasPendingCommand);

            string grammarBefore = _recogniser.TestGrammarJson;

            // This should be deferred
            _recogniser.RebuildGrammar();

            string grammarDuring = _recogniser.TestGrammarJson;
            Assert.AreEqual(grammarBefore, grammarDuring,
                "Grammar should not change during pending state");
        }

        [Test]
        public void RebuildParser_DuringPending_ExecutesImmediately()
        {
            ConfigureSync(requiresConfirm: true);

            _recogniser.InjectText("launch missiles target hotel one");
            Assert.IsTrue(_recogniser.HasPendingCommand);

            // RebuildParser should not throw and should execute
            Assert.DoesNotThrow(() => _recogniser.RebuildParser());
        }

        [Test]
        public void DeferredRebuild_DrainsAfterPendingResolves()
        {
            ConfigureSync(requiresConfirm: true);

            _recogniser.InjectText("launch missiles target hotel one");
            Assert.IsTrue(_recogniser.HasPendingCommand);

            _recogniser.RebuildGrammar(); // Deferred
            Assert.IsTrue(_recogniser.TestGrammarRebuildDeferred, "Should be deferred");

            _recogniser.InjectText("confirm"); // Resolves pending

            Assert.IsFalse(_recogniser.TestGrammarRebuildDeferred,
                "Deferred flag should be cleared after pending resolves");
        }

        // ======== Public API ========

        [Test]
        public void CancelPendingCommand_Cancels()
        {
            ConfigureSync(requiresConfirm: true);

            VoxrCommand? cancelled = null;
            _recogniser.OnCommandCancelled += cmd => cancelled = cmd;

            _recogniser.InjectText("launch missiles target hotel one");
            _recogniser.CancelPendingCommand();

            Assert.IsTrue(cancelled.HasValue, "Should fire OnCommandCancelled");
            Assert.IsFalse(_recogniser.HasPendingCommand);
        }

        [Test]
        public void CancelPendingCommand_NoPending_NoOp()
        {
            ConfigureSync();

            VoxrCommand? cancelled = null;
            _recogniser.OnCommandCancelled += cmd => cancelled = cmd;

            _recogniser.CancelPendingCommand();

            Assert.IsFalse(cancelled.HasValue, "Should not fire when no pending");
        }

        [Test]
        public void HasPendingCommand_Property()
        {
            ConfigureSync(requiresConfirm: true);

            Assert.IsFalse(_recogniser.HasPendingCommand);

            _recogniser.InjectText("launch missiles target hotel one");
            Assert.IsTrue(_recogniser.HasPendingCommand);

            _recogniser.InjectText("confirm");
            Assert.IsFalse(_recogniser.HasPendingCommand);
        }

        [Test]
        public void PendingCommand_Property()
        {
            ConfigureSync(requiresConfirm: true);

            Assert.IsNull(_recogniser.PendingCommand);

            _recogniser.InjectText("launch missiles target hotel one");
            Assert.IsNotNull(_recogniser.PendingCommand);
            Assert.AreEqual("launch_weapon", _recogniser.PendingCommand.Value.Intent);

            _recogniser.InjectText("cancel");
            Assert.IsNull(_recogniser.PendingCommand);
        }

        // ======== Lifecycle ========

        [Test]
        public void SetActiveSets_CancelsPending()
        {
            var slots = MakeSlots();
            var sets = new[]
            {
                new VoxrCommandSet("combat", MakeCommands(requiresConfirm: true)),
            };

            _recogniser.Configure(slots, sets);
            _recogniser.SetActiveSets("combat");
            _recogniser.BufferWindow = 0f;
            _recogniser.CommandCooldown = 0f;
            _recogniser.PendingTimeout = 30f;

            VoxrCommand? cancelled = null;
            _recogniser.OnCommandCancelled += cmd => cancelled = cmd;

            _recogniser.InjectText("launch missiles target hotel one");
            Assert.IsTrue(_recogniser.HasPendingCommand);

            _recogniser.SetActiveSets("combat");

            Assert.IsTrue(cancelled.HasValue, "SetActiveSets should cancel pending");
            Assert.IsFalse(_recogniser.HasPendingCommand);
        }

        [Test]
        public void Configure_CancelsPending()
        {
            ConfigureSync(requiresConfirm: true);

            VoxrCommand? cancelled = null;
            _recogniser.OnCommandCancelled += cmd => cancelled = cmd;

            _recogniser.InjectText("launch missiles target hotel one");
            Assert.IsTrue(_recogniser.HasPendingCommand);

            // Reconfigure
            _recogniser.Configure(MakeSlots(), MakeCommands());

            Assert.IsTrue(cancelled.HasValue, "Configure should cancel pending");
            Assert.IsFalse(_recogniser.HasPendingCommand);
        }

        // ======== Backward Compatibility ========

        [Test]
        public void DefaultDefinition_BothFlagsFalse()
        {
            var def = new VoxrCommandDefinition("test", new[] { new[] { "test" } });
            Assert.IsFalse(def.AllowPartialMatch);
            Assert.IsFalse(def.RequiresConfirmation);
        }

        [Test]
        public void NormalCommand_UnchangedBehavior()
        {
            ConfigureSync(); // No flags

            VoxrCommand? recognised = null;
            _recogniser.OnCommandRecognised += cmd => recognised = cmd;
            VoxrCommand? pending = null;
            _recogniser.OnCommandPending += cmd => pending = cmd;

            _recogniser.InjectText("launch missiles target hotel one");

            Assert.IsTrue(recognised.HasValue, "Normal command should fire as before");
            Assert.IsFalse(pending.HasValue, "Normal command should not enter pending");
        }

        [Test]
        public void CeaseFireCommand_StillFiresNormally()
        {
            ConfigureSync(allowPartial: true); // Only launch_weapon has partial

            VoxrCommand? recognised = null;
            _recogniser.OnCommandRecognised += cmd => recognised = cmd;

            _recogniser.InjectText("cease fire");

            Assert.IsTrue(recognised.HasValue);
            Assert.AreEqual("cease_fire", recognised.Value.Intent);
        }

        // ======== Edge Cases ========

        [Test]
        public void NewPending_CancelsExistingPending()
        {
            // Use two commands that both allow partial
            var slots = MakeSlots();
            var commands = new[]
            {
                new VoxrCommandDefinition("launch_weapon", new[]
                {
                    new[] { "launch", "{weapon}", "target", "{target}" },
                }, allowPartialMatch: true),
                new VoxrCommandDefinition("engage_target", new[]
                {
                    new[] { "engage", "{target}" },
                }, allowPartialMatch: true),
            };

            _recogniser.Configure(slots, commands);
            _recogniser.BufferWindow = 0f;
            _recogniser.CommandCooldown = 0f;
            _recogniser.PendingTimeout = 30f;

            var cancelledIntents = new List<string>();
            _recogniser.OnCommandCancelled += cmd => cancelledIntents.Add(cmd.Intent);

            var pendingIntents = new List<string>();
            _recogniser.OnCommandPending += cmd => pendingIntents.Add(cmd.Intent);

            // First partial enters pending
            _recogniser.InjectText("launch missiles target");
            Assert.AreEqual(1, pendingIntents.Count);
            Assert.AreEqual("launch_weapon", pendingIntents[0]);

            // Second partial (different weapon) replaces the first pending
            _recogniser.InjectText("launch torpedoes target");
            Assert.AreEqual(2, pendingIntents.Count);
            Assert.AreEqual("launch_weapon", pendingIntents[1]);
            Assert.AreEqual(1, cancelledIntents.Count, "First pending should be cancelled");
            Assert.AreEqual("launch_weapon", cancelledIntents[0]);
        }

        [Test]
        public void FollowUp_WithAlias_Works()
        {
            ConfigureSync(allowPartial: true);

            VoxrCommand? confirmed = null;
            _recogniser.OnCommandConfirmed += cmd => confirmed = cmd;

            _recogniser.InjectText("launch missiles target");
            _recogniser.InjectText("h one");

            Assert.IsTrue(confirmed.HasValue, "Alias 'h one' should fill target slot");
            Assert.AreEqual("hotel one", confirmed.Value.GetSlot("target"));
        }

        [Test]
        public void MatchedPatternIndex_PopulatedCorrectly()
        {
            ConfigureSync(allowPartial: true);

            VoxrCommand? pending = null;
            _recogniser.OnCommandPending += cmd => pending = cmd;

            _recogniser.InjectText("launch missiles target");

            Assert.IsTrue(pending.HasValue);
            Assert.AreEqual(0, pending.Value.MatchedPatternIndex,
                "Should match pattern index 0");
        }

        // -------- Admission and the partial path (issue #65, DR-7) --------

        [Test]
        public void PartialMatch_SparseFragment_DoesNotArmPending()
        {
            // The partial path is gated on `Score > 0f`, not on minScore — so it is the one
            // consumer keyed directly to the floor issue #65 §5.1 moved candidates across.
            // Zeroing the miss penalty put fragments over that floor, and each one arriving
            // here arms a slot-fill prompt and cancels any pending already in flight. DR-7 is
            // what keeps them out, and nothing else in the suite exercises this path.
            //
            // Seven required elements. "set burn now" matches three (set, burn, now) and
            // misses four (the, level, to, and the {burn_level} slot), so DR-7 refuses it.
            // Without the rule it scores (1 + 0 + 1 + 0 + 0 - 1 + 1) / 7 = 0.286 — under
            // minScore, above zero, with an unfilled required slot: precisely the shape that
            // enters pending.
            _recogniser.Configure(
                new[] { new VoxrSlotDefinition("burn_level", new[] { "coast", "hard burn" }) },
                new[]
                {
                    new VoxrCommandDefinition(
                        "set_burn",
                        new[]
                        {
                            new[] { "set", "the", "burn", "level", "to", "{burn_level}", "now" },
                        },
                        allowPartialMatch: true
                    ),
                }
            );
            _recogniser.BufferWindow = 0f;
            _recogniser.CommandCooldown = 0f;
            _recogniser.PendingTimeout = 30f;

            int pendingCount = 0;
            string unrecognised = null;
            _recogniser.OnCommandPending += _ => pendingCount++;
            _recogniser.OnUnrecognisedSpeech += text => unrecognised = text;

            _recogniser.InjectText("set burn now");

            Assert.AreEqual(0, pendingCount, "a fragment must not arm a slot-fill prompt");
            Assert.IsFalse(_recogniser.HasPendingCommand);
            Assert.AreEqual("set burn now", unrecognised);

            // Control, so the refusal above cannot be a mis-wired fixture: one more matched
            // literal and the same missed slot gives four matched against three missed, which
            // DR-7 admits, at (4 - 1) / 7 = 0.429 — still under minScore, so it enters pending
            // exactly as the partial path intends.
            _recogniser.InjectText("set the burn now");

            Assert.AreEqual(1, pendingCount, "a candidate with more evidence than gaps still arms");
            Assert.IsTrue(_recogniser.HasPendingCommand);
        }

        // -------- The follow-up exit and the two opt-in exits (issue #77) --------

        // Every test below needs a pending with TWO unfilled required slots, which is what makes
        // the follow-up exit's gap reachable at all: with one unfilled slot a follow-up either
        // completes the command or fills nothing, so a result that is still incomplete never
        // appears. Nothing else in the suite constructs this shape.
        //
        // "launch at on my mark" matches five required literals and strands both slots — five
        // matched against two missed, which DR-7 admits. Incompleteness is what routes it to
        // pending; it also happens to land below minScore, but that term is neither necessary
        // nor sufficient here, so do not "fix" this fixture by adjusting its score.
        void ConfigureTwoSlotPartial(bool requiresConfirm = false)
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
                        allowPartialMatch: true,
                        requiresConfirmation: requiresConfirm
                    ),
                }
            );
            _recogniser.BufferWindow = 0f;
            _recogniser.CommandCooldown = 0f;
            _recogniser.PendingTimeout = 30f;
        }

        void AssertBothArgumentsPending()
        {
            Assert.IsTrue(_recogniser.HasPendingCommand, "precondition: a pending command is live");
            Assert.IsFalse(
                _recogniser.PendingCommand.Value.HasSlot("weapon"),
                "precondition: {weapon} is unfilled"
            );
            Assert.IsFalse(
                _recogniser.PendingCommand.Value.HasSlot("target"),
                "precondition: and so is {target} — two unfilled required slots, not one"
            );
        }

        [Test]
        public void FollowUp_FillingOnlyOneOfTwoRequiredSlots_StaysPending()
        {
            // Issue #77 case 1. TryFollowUpSlotFill walks the unfilled slots in order, breaks at
            // the first it cannot fill, and returns a command as soon as ONE new slot is filled.
            // With two unfilled it therefore hands Step 5 a command still missing an argument —
            // and Step 5 fired it, which is precisely the shape #73 refuses on the flush path,
            // reached by the path #73 routes those commands to.
            ConfigureTwoSlotPartial();

            // The whole payload, not just the intent: the re-fire's argument is what a prompt
            // reads, and it is a separate surface from the handler state the assertions below
            // check. Recording only the intent would let a regression that re-announced the
            // STALE pre-fill command pass every assertion in this file.
            var pendingPayloads = new List<VoxrCommand>();
            _recogniser.OnCommandPending += pendingPayloads.Add;
            VoxrCommand? recognised = null;
            _recogniser.OnCommandRecognised += cmd => recognised = cmd;
            VoxrCommand? confirmed = null;
            _recogniser.OnCommandConfirmed += cmd => confirmed = cmd;
            int cancelledCount = 0;
            _recogniser.OnCommandCancelled += _ => cancelledCount++;

            _recogniser.InjectText("launch at on my mark");
            AssertBothArgumentsPending();
            Assert.AreEqual(1, pendingPayloads.Count);

            // Fills {weapon} and nothing else — {target} has no candidate in this utterance.
            _recogniser.InjectText("missiles");

            Assert.IsFalse(
                recognised.HasValue,
                "a command still missing an argument must not fire out of the slot-fill exit"
            );
            Assert.IsFalse(confirmed.HasValue);
            Assert.AreEqual(0, cancelledCount, "and the half-finished command is not discarded");

            // The refusal has to keep the fill, or it is a refusal to make progress: without this
            // the test would pass just as well on a slot-fill that matched nothing at all.
            Assert.IsTrue(_recogniser.HasPendingCommand, "the pending stays live");
            Assert.AreEqual(
                "missiles",
                _recogniser.PendingCommand.Value.GetSlot("weapon"),
                "carrying the slot this utterance did fill"
            );
            Assert.IsFalse(
                _recogniser.PendingCommand.Value.HasSlot("target"),
                "and still waiting on the one it did not"
            );
            Assert.AreEqual(
                2,
                pendingPayloads.Count,
                "and it re-announces itself, so a prompt can show what is still missing"
            );
            Assert.AreEqual(
                "missiles",
                pendingPayloads[1].GetSlot("weapon"),
                "the re-announcement carries the UPDATED command, not the pre-fill one — a "
                    + "prompt reads this payload, so re-announcing the stale command would name "
                    + "a slot the user has already supplied"
            );
            Assert.IsFalse(
                pendingPayloads[1].HasSlot("target"),
                "and still reports the slot that is genuinely outstanding"
            );

            // The remaining slot arrives in a third utterance, and only now does it fire.
            _recogniser.InjectText("hotel one");

            Assert.IsTrue(confirmed.HasValue, "the completed command fires");
            Assert.AreEqual("missiles", confirmed.Value.GetSlot("weapon"));
            Assert.AreEqual(
                "hotel one",
                confirmed.Value.GetSlot("target"),
                "with the slot filled two utterances earlier still attached"
            );
            Assert.IsTrue(recognised.HasValue);
            Assert.IsFalse(_recogniser.HasPendingCommand);
        }

        [Test]
        public void FollowUp_AcrossTwoUtterances_StillReachesTheConfirmationGate()
        {
            // AdvanceSlotFill carries `Reason` over so the pending stays a PartialMatch. That
            // field is load-bearing in exactly one place — Complete's re-entry guard
            // `RequiresConfirmation && Reason == PartialMatch` — and nothing reached it before:
            // every other test that fills a slot strands only ONE, so it goes straight to
            // Complete without passing through AdvanceSlotFill. A regression writing
            // AwaitingConfirmation here would leave the rest of this file green while firing a
            // requiresConfirmation command with its confirmation gate skipped.
            //
            // This is also the path the fix repaired rather than merely guarded: before #77,
            // Complete re-entered on the FIRST fill with UnfilledSlots emptied, which made every
            // further fill a no-op and stranded {target} permanently.
            ConfigureTwoSlotPartial(requiresConfirm: true);

            var events = new List<string>();
            _recogniser.OnCommandPending += cmd => events.Add($"pending:{cmd.Intent}");
            _recogniser.OnCommandConfirmed += cmd => events.Add($"confirmed:{cmd.Intent}");
            VoxrCommand? recognised = null;
            _recogniser.OnCommandRecognised += cmd => recognised = cmd;

            _recogniser.InjectText("launch at on my mark");
            AssertBothArgumentsPending();

            _recogniser.InjectText("missiles");
            Assert.IsFalse(recognised.HasValue, "a partial fill does not fire");
            Assert.AreEqual(
                "missiles",
                _recogniser.PendingCommand.Value.GetSlot("weapon"),
                "and the second slot is still fillable — the pre-#77 bug emptied UnfilledSlots "
                    + "here and stranded {target} for good"
            );

            _recogniser.InjectText("hotel one");

            Assert.IsFalse(
                recognised.HasValue,
                "the COMPLETING fill must not fire either — this command requires confirmation"
            );
            Assert.IsTrue(_recogniser.HasPendingCommand, "it re-enters pending for confirmation");
            Assert.AreEqual(
                "hotel one",
                _recogniser.PendingCommand.Value.GetSlot("target"),
                "carrying both slots into the confirmation stage"
            );

            _recogniser.InjectText("confirm");

            Assert.IsTrue(recognised.HasValue, "and only the confirm phrase fires it");
            Assert.AreEqual("missiles", recognised.Value.GetSlot("weapon"));
            Assert.AreEqual("hotel one", recognised.Value.GetSlot("target"));
            Assert.IsFalse(_recogniser.HasPendingCommand);
            CollectionAssert.AreEqual(
                new[]
                {
                    "pending:launch_weapon", // entered, both slots absent
                    "pending:launch_weapon", // re-announced after {weapon} filled
                    "pending:launch_weapon", // re-entered for confirmation once complete
                    "confirmed:launch_weapon",
                },
                events
            );
        }

#if UNITY_EDITOR
        [UnityTest]
        public IEnumerator FollowUp_PartialFill_DoesNotExtendThePendingTimeoutWindow()
        {
            // The documented promise: "the whole exchange runs against the single pendingTimeout
            // window that started when the command first entered pending — filling a slot does
            // not extend it." No test could observe it before, because every timeout test goes
            // through TestForceTimeoutNow, which overwrites CreatedTime outright before the only
            // check that reads it. Read the field directly instead.
            ConfigureTwoSlotPartial();

            _recogniser.InjectText("launch at on my mark");
            AssertBothArgumentsPending();
            float createdOnEntry = _recogniser.EditorPendingCommand.Value.CreatedTime;

            yield return null;

            // Without this the test cannot tell "preserved" from "refreshed" — both would read
            // back the same value — so it would pass no matter which the code did.
            Assert.Greater(
                Time.time,
                createdOnEntry,
                "precondition: the clock advanced between entry and fill"
            );

            _recogniser.InjectText("missiles");

            Assert.IsTrue(_recogniser.HasPendingCommand, "precondition: the fill kept it alive");
            Assert.AreEqual(
                createdOnEntry,
                _recogniser.EditorPendingCommand.Value.CreatedTime,
                "a fill is progress, not a reprieve: the deadline stays where entry set it"
            );
        }
#endif

        [Test]
        public void ConfirmVocabulary_OnAPartialMatchPending_FiresAsIs_ByDesign()
        {
            // Issue #77 case 2, ruled deliberate and documented in
            // Documentation~/command-recognition.md rather than fixed. A confirm phrase resolves
            // the pending at Step 1, before any completeness test, so it fires the command with
            // whatever is filled — here nothing at all. Pinned because the ruling is the only
            // thing separating it from case 1 above: opting into allowPartialMatch is what
            // authorises it, and a handler for such a command must tolerate absent arguments.
            ConfigureTwoSlotPartial();

            _recogniser.InjectText("launch at on my mark");
            AssertBothArgumentsPending();

            VoxrCommand? confirmed = null;
            _recogniser.OnCommandConfirmed += cmd => confirmed = cmd;

            _recogniser.InjectText("confirm");

            Assert.IsTrue(confirmed.HasValue, "confirming a partial match fires it as-is");
            Assert.IsFalse(confirmed.Value.HasSlot("weapon"), "with {weapon} absent");
            Assert.IsFalse(confirmed.Value.HasSlot("target"), "and {target} absent");
            Assert.IsFalse(_recogniser.HasPendingCommand);
        }

        [Test]
        public void TimeoutFireAsIs_OnATwoSlotPartialMatchPending_FiresAsIs_ByDesign()
        {
            // Issue #77 case 3, ruled deliberate for the same reason and documented alongside it.
            // PartialMatch_TimeoutFireAsIs_FiresWithPartialSlots already covers one unfilled slot;
            // this pins the shape the issue named as untested, where the command reaches the
            // handler missing every argument it has.
            ConfigureTwoSlotPartial();
            _recogniser.PendingTimeoutBehavior = VoxrPendingTimeoutBehavior.FireAsIs;

            _recogniser.InjectText("launch at on my mark");
            AssertBothArgumentsPending();

            VoxrCommand? confirmed = null;
            _recogniser.OnCommandConfirmed += cmd => confirmed = cmd;
            VoxrCommand? recognised = null;
            _recogniser.OnCommandRecognised += cmd => recognised = cmd;

            ForceTimeoutNow();

            Assert.IsTrue(confirmed.HasValue, "FireAsIs fires it as-is");
            Assert.IsTrue(recognised.HasValue);
            Assert.IsFalse(confirmed.Value.HasSlot("weapon"), "with {weapon} absent");
            Assert.IsFalse(confirmed.Value.HasSlot("target"), "and {target} absent");
            Assert.IsFalse(_recogniser.HasPendingCommand);
        }

        [Test]
        public void Timeout_DefaultCancel_OnAPartlyFilledPending_Cancels()
        {
            // The other side of the #77 fix. Keeping the pending alive means an unanswered
            // slot-fill now reaches the timeout instead of firing on the follow-up, so the
            // default behaviour has to be the one that discards it — the #73 stance that firing
            // nothing beats firing a command whose argument the handler never receives.
            ConfigureTwoSlotPartial();

            _recogniser.InjectText("launch at on my mark");
            _recogniser.InjectText("missiles");
            Assert.IsTrue(_recogniser.HasPendingCommand);

            VoxrCommand? cancelled = null;
            _recogniser.OnCommandCancelled += cmd => cancelled = cmd;
            VoxrCommand? recognised = null;
            _recogniser.OnCommandRecognised += cmd => recognised = cmd;

            ForceTimeoutNow();

            Assert.IsTrue(cancelled.HasValue, "the partly filled command is discarded");
            Assert.AreEqual(
                "missiles",
                cancelled.Value.GetSlot("weapon"),
                "and reports what it had got as far as filling"
            );
            Assert.IsFalse(recognised.HasValue, "nothing fires");
            Assert.IsFalse(_recogniser.HasPendingCommand);
        }

        // -------- Helpers --------

        void ForceTimeoutNow()
        {
            _recogniser.TestForceTimeoutNow();
        }
    }
}
