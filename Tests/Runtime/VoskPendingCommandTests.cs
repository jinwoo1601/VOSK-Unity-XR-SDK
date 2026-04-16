using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using VoskXR.Commands;

namespace VoskXR.Tests.Runtime
{
    public class VoskPendingCommandTests
    {
        GameObject _go;
        VoskCommandRecogniser _recogniser;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("TestPendingCommands");
            _recogniser = _go.AddComponent<VoskCommandRecogniser>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null)
                UnityEngine.Object.DestroyImmediate(_go);
        }

        // -------- Fixtures --------

        static VoskSlotDefinition[] MakeSlots()
        {
            return new[]
            {
                new VoskSlotDefinition("target",
                    new[] { "hotel one", "hotel two", "alpha one" },
                    new Dictionary<string, string>
                    {
                        { "h one", "hotel one" },
                        { "h two", "hotel two" },
                    }),
                new VoskSlotDefinition("weapon",
                    new[] { "missiles", "torpedoes" }),
            };
        }

        static VoskCommandDefinition[] MakeCommands(
            bool allowPartial = false, bool requiresConfirm = false)
        {
            return new[]
            {
                new VoskCommandDefinition("launch_weapon", new[]
                {
                    new[] { "launch", "{weapon}", "target", "{target}" },
                }, allowPartial, requiresConfirm),
                new VoskCommandDefinition("cease_fire", new[]
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
            VoskCommand? received = null;
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

            VoskCommand? pending = null;
            _recogniser.OnCommandPending += cmd => pending = cmd;
            VoskCommand? recognised = null;
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
        public void PartialMatch_AllSlotsFilled_FiresNormally()
        {
            ConfigureSync(allowPartial: true);

            VoskCommand? pending = null;
            _recogniser.OnCommandPending += cmd => pending = cmd;
            VoskCommand? recognised = null;
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

            VoskCommand? confirmed = null;
            _recogniser.OnCommandConfirmed += cmd => confirmed = cmd;
            VoskCommand? recognised = null;
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

            VoskCommand? confirmed = null;
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

            VoskCommand? cancelled = null;
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
            _recogniser.PendingTimeoutBehavior = VoskPendingTimeoutBehavior.FireAsIs;

            VoskCommand? confirmed = null;
            _recogniser.OnCommandConfirmed += cmd => confirmed = cmd;
            VoskCommand? recognised = null;
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

            VoskCommand? pending = null;
            _recogniser.OnCommandPending += cmd => pending = cmd;
            VoskCommand? recognised = null;
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

            VoskCommand? confirmed = null;
            _recogniser.OnCommandConfirmed += cmd => confirmed = cmd;
            VoskCommand? recognised = null;
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

            VoskCommand? cancelled = null;
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

            VoskCommand? confirmed = null;
            _recogniser.OnCommandConfirmed += cmd => confirmed = cmd;

            _recogniser.InjectText("launch missiles target hotel one");
            _recogniser.InjectText("affirmative");

            Assert.IsTrue(confirmed.HasValue, "Synonym 'affirmative' should confirm");
        }

        [Test]
        public void RequiresConfirmation_BelayThat_Cancels()
        {
            ConfigureSync(requiresConfirm: true);

            VoskCommand? cancelled = null;
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

            VoskCommand? confirmed = null;
            _recogniser.OnCommandConfirmed += cmd => confirmed = cmd;
            VoskCommand? cancelled = null;
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

            VoskCommand? cancelled = null;
            _recogniser.OnCommandCancelled += cmd => cancelled = cmd;
            VoskCommand? recognised = null;
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

            VoskCommand? confirmed = null;
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

            VoskCommand? cancelled = null;
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

            VoskCommand? cancelled = null;
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
                new VoskCommandSet("combat", MakeCommands(requiresConfirm: true)),
            };

            _recogniser.Configure(slots, sets);
            _recogniser.SetActiveSets("combat");
            _recogniser.BufferWindow = 0f;
            _recogniser.CommandCooldown = 0f;
            _recogniser.PendingTimeout = 30f;

            VoskCommand? cancelled = null;
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

            VoskCommand? cancelled = null;
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
            var def = new VoskCommandDefinition("test", new[] { new[] { "test" } });
            Assert.IsFalse(def.AllowPartialMatch);
            Assert.IsFalse(def.RequiresConfirmation);
        }

        [Test]
        public void NormalCommand_UnchangedBehavior()
        {
            ConfigureSync(); // No flags

            VoskCommand? recognised = null;
            _recogniser.OnCommandRecognised += cmd => recognised = cmd;
            VoskCommand? pending = null;
            _recogniser.OnCommandPending += cmd => pending = cmd;

            _recogniser.InjectText("launch missiles target hotel one");

            Assert.IsTrue(recognised.HasValue, "Normal command should fire as before");
            Assert.IsFalse(pending.HasValue, "Normal command should not enter pending");
        }

        [Test]
        public void CeaseFireCommand_StillFiresNormally()
        {
            ConfigureSync(allowPartial: true); // Only launch_weapon has partial

            VoskCommand? recognised = null;
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
                new VoskCommandDefinition("launch_weapon", new[]
                {
                    new[] { "launch", "{weapon}", "target", "{target}" },
                }, allowPartialMatch: true),
                new VoskCommandDefinition("engage_target", new[]
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

            VoskCommand? confirmed = null;
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

            VoskCommand? pending = null;
            _recogniser.OnCommandPending += cmd => pending = cmd;

            _recogniser.InjectText("launch missiles target");

            Assert.IsTrue(pending.HasValue);
            Assert.AreEqual(0, pending.Value.MatchedPatternIndex,
                "Should match pattern index 0");
        }

        // -------- Helpers --------

        void ForceTimeoutNow()
        {
            _recogniser.TestForceTimeoutNow();
        }
    }
}
