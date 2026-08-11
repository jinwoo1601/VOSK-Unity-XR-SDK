using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VoXR;
using VoXR.Commands;

namespace VoXR.Tests.Runtime
{
    public class VoxrCommandRecogniserInjectionTests
    {
        GameObject _go;
        VoxrCommandRecogniser _recogniser;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("TestCommandRecogniser");
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
                    new[] { "hotel one", "hotel two", "alpha one" }),
                new VoxrSlotDefinition("weapon",
                    new[] { "missiles", "torpedoes" }),
                new VoxrSlotDefinition("quantity",
                    new[] { "all", "one", "two" }),
            };
        }

        static VoxrCommandDefinition[] MakeCommands()
        {
            return new[]
            {
                new VoxrCommandDefinition("launch_weapon", new[]
                {
                    new[] { "launch", "{?quantity}", "{weapon}", "target", "{target}" },
                }),
                new VoxrCommandDefinition("cease_fire", new[]
                {
                    new[] { "cease", "fire" },
                }),
            };
        }

        void ConfigureWithSyncDefaults()
        {
            _recogniser.Configure(MakeSlots(), MakeCommands());
            // Disable buffer and cooldown so threshold tests can assert events synchronously.
            _recogniser.BufferWindow = 0f;
            _recogniser.CommandCooldown = 0f;
        }

        // -------- Warning / no-op cases --------

        [Test]
        public void InjectText_BeforeConfigure_LogsWarningAndDoesNotThrow()
        {
            LogAssert.Expect(LogType.Warning, new Regex("InjectText called before parser is ready"));

            Assert.DoesNotThrow(() => _recogniser.InjectText("anything"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void InjectText_NullOrWhitespace_NoOps(string text)
        {
            ConfigureWithSyncDefaults();
            int recognisedCount = 0;
            int unrecognisedCount = 0;
            _recogniser.OnCommandRecognised += _ => recognisedCount++;
            _recogniser.OnUnrecognisedSpeech += _ => unrecognisedCount++;

            _recogniser.InjectText(text);

            Assert.AreEqual(0, recognisedCount);
            Assert.AreEqual(0, unrecognisedCount);
        }

        // -------- Match / no-match --------

        [Test]
        public void InjectText_MatchingCommand_FiresBothEvents()
        {
            ConfigureWithSyncDefaults();
            VoxrCommand? singleEvent = null;
            VoxrCommand[] batchEvent = null;
            _recogniser.OnCommandRecognised += cmd => singleEvent = cmd;
            _recogniser.OnCommandsRecognised += cmds => batchEvent = cmds;

            _recogniser.InjectText("launch all missiles target hotel one");

            Assert.IsTrue(singleEvent.HasValue, "OnCommandRecognised did not fire");
            Assert.AreEqual("launch_weapon", singleEvent.Value.Intent);
            Assert.AreEqual("missiles", singleEvent.Value.GetSlot("weapon"));
            Assert.AreEqual("hotel one", singleEvent.Value.GetSlot("target"));
            Assert.AreEqual("all", singleEvent.Value.GetSlot("quantity"));

            Assert.IsNotNull(batchEvent, "OnCommandsRecognised did not fire");
            Assert.AreEqual(1, batchEvent.Length);
        }

        [Test]
        public void InjectText_NoMatch_FiresOnUnrecognisedSpeech()
        {
            ConfigureWithSyncDefaults();
            string received = null;
            _recogniser.OnUnrecognisedSpeech += text => received = text;

            _recogniser.InjectText("hello world");

            Assert.AreEqual("hello world", received);
        }

        // -------- Word data propagation --------

        [Test]
        public void InjectText_PassesWordsThroughToParser_ConfidencePropagated()
        {
            ConfigureWithSyncDefaults();
            VoxrCommand? received = null;
            _recogniser.OnCommandRecognised += cmd => received = cmd;

            var words = VoxrSpeechRecogniser.CreateSimulatedWords("cease fire", 0.85f);
            _recogniser.InjectText("cease fire", words);

            Assert.IsTrue(received.HasValue);
            Assert.AreEqual(0.85f, received.Value.Confidence, 1e-5f,
                "Confidence from injected words did not propagate to VoxrCommand");
        }

        // -------- Threshold filtering --------

        [Test]
        public void InjectText_BelowMinConfidence_Rejected()
        {
            ConfigureWithSyncDefaults();
            int recognised = 0;
            int unrecognised = 0;
            _recogniser.OnCommandRecognised += _ => recognised++;
            _recogniser.OnUnrecognisedSpeech += _ => unrecognised++;

            var words = VoxrSpeechRecogniser.CreateSimulatedWords("cease fire", 0.2f);
            _recogniser.InjectText("cease fire", words);

            Assert.AreEqual(0, recognised, "Command should be rejected by minConfidence");
            // Match but below threshold is silently filtered (not unrecognised).
            Assert.AreEqual(0, unrecognised);
        }

        [Test]
        public void InjectText_AtOrAboveMinConfidence_Accepted()
        {
            ConfigureWithSyncDefaults();
            int recognised = 0;
            _recogniser.OnCommandRecognised += _ => recognised++;

            var words = VoxrSpeechRecogniser.CreateSimulatedWords("cease fire", 0.5f);
            _recogniser.InjectText("cease fire", words);

            Assert.AreEqual(1, recognised);
        }

        // -------- Cooldown --------

        [Test]
        public void InjectText_RespectsCommandCooldown()
        {
            _recogniser.Configure(MakeSlots(), MakeCommands());
            _recogniser.BufferWindow = 0f;
            _recogniser.CommandCooldown = 1.0f;

            int fireCount = 0;
            _recogniser.OnCommandRecognised += _ => fireCount++;

            _recogniser.InjectText("cease fire");
            _recogniser.InjectText("cease fire");

            // Tests run in a single frame so Time.time does not advance — second call is within cooldown.
            Assert.AreEqual(1, fireCount, "Second injection within cooldown should be rejected");
        }

        // -------- Buffered path + flush --------

        [Test]
        public void InjectText_BufferedPath_QueuedUntilFlush()
        {
            _recogniser.Configure(MakeSlots(), MakeCommands());
            _recogniser.BufferWindow = 1.5f;
            _recogniser.CommandCooldown = 0f;

            int fireCount = 0;
            _recogniser.OnCommandRecognised += _ => fireCount++;

            _recogniser.InjectText("cease fire");
            Assert.AreEqual(0, fireCount, "Buffered injection must not fire immediately");

            _recogniser.FlushPendingBuffer();
            Assert.AreEqual(1, fireCount, "Flush must release the buffered command");
        }

        [Test]
        public void FlushPendingBuffer_NoBufferedSpeech_NoOps()
        {
            ConfigureWithSyncDefaults();
            int fireCount = 0;
            _recogniser.OnCommandRecognised += _ => fireCount++;
            _recogniser.OnUnrecognisedSpeech += _ => fireCount++;

            Assert.DoesNotThrow(() => _recogniser.FlushPendingBuffer());
            Assert.AreEqual(0, fireCount);
        }

        [Test]
        public void InjectText_AfterFlush_DoesNotDoubleFire()
        {
            _recogniser.Configure(MakeSlots(), MakeCommands());
            _recogniser.BufferWindow = 1.5f;
            _recogniser.CommandCooldown = 0f;

            int fireCount = 0;
            _recogniser.OnCommandRecognised += _ => fireCount++;

            _recogniser.InjectText("cease fire");
            _recogniser.FlushPendingBuffer();
            _recogniser.FlushPendingBuffer(); // second flush is a no-op

            Assert.AreEqual(1, fireCount);
        }

        // -------- Cross-component end-to-end --------

        [Test]
        public void InjectResult_OnSpeechRecogniser_PropagatesToCommandRecogniser()
        {
            // Build both components on the same GameObject and wire them together,
            // proving the production OnEnable subscription path actually connects.
            // If OnResult is ever renamed or unsubscribed, the isolated tests still
            // pass but this one fails.
            var speech = _go.AddComponent<VoxrSpeechRecogniser>();
            _recogniser.SpeechRecogniser = speech;

            // Force OnEnable to re-run with the now-set speechRecogniser reference.
            _recogniser.enabled = false;
            _recogniser.enabled = true;

            ConfigureWithSyncDefaults();

            VoxrCommand? received = null;
            _recogniser.OnCommandRecognised += cmd => received = cmd;

            speech.InjectResult("cease fire");

            Assert.IsTrue(received.HasValue,
                "Speech-layer InjectResult did not propagate to command recogniser");
            Assert.AreEqual("cease_fire", received.Value.Intent);
        }

        // -------- Eager flush (issue #25) --------

        [Test]
        public void EagerFlush_TerminalCommand_FiresImmediatelyWithoutFlush()
        {
            _recogniser.Configure(MakeSlots(), MakeCommands());
            _recogniser.BufferWindow = 1.5f;
            _recogniser.CommandCooldown = 0f;
            _recogniser.EagerFlushOnCompleteMatch = true;

            int fireCount = 0;
            _recogniser.OnCommandRecognised += _ => fireCount++;

            _recogniser.InjectText("cease fire");

            Assert.AreEqual(1, fireCount,
                "A terminal command should fire immediately under eager flush, " +
                "without waiting for the buffer window");
        }

        [Test]
        public void EagerFlush_FullSlottedCommand_FiresImmediately()
        {
            _recogniser.Configure(MakeSlots(), MakeCommands());
            _recogniser.BufferWindow = 1.5f;
            _recogniser.CommandCooldown = 0f;
            _recogniser.EagerFlushOnCompleteMatch = true;

            VoxrCommand? received = null;
            _recogniser.OnCommandRecognised += cmd => received = cmd;

            _recogniser.InjectText("launch all missiles target hotel one");

            Assert.IsTrue(received.HasValue,
                "A complete slotted command should fire immediately under eager flush");
            Assert.AreEqual("launch_weapon", received.Value.Intent);
            Assert.AreEqual("hotel one", received.Value.GetSlot("target"));
        }

        [Test]
        public void EagerFlush_Off_PreservesTimeOnlyBuffering()
        {
            // The default (flag off) must keep today's behaviour: buffered until flushed.
            _recogniser.Configure(MakeSlots(), MakeCommands());
            _recogniser.BufferWindow = 1.5f;
            _recogniser.CommandCooldown = 0f;
            // eagerFlushOnCompleteMatch intentionally left at its default (false).

            int fireCount = 0;
            _recogniser.OnCommandRecognised += _ => fireCount++;

            _recogniser.InjectText("cease fire");
            Assert.AreEqual(0, fireCount,
                "With eager flush off, a buffered command must not fire immediately");

            _recogniser.FlushPendingBuffer();
            Assert.AreEqual(1, fireCount);
        }

        [Test]
        public void EagerFlush_PrefixCommand_WaitsForTheWindow()
        {
            var slots = new[]
            {
                new VoxrSlotDefinition("target", new[] { "hotel one", "hotel two" }),
            };
            var commands = new[]
            {
                new VoxrCommandDefinition("status", new[] { new[] { "status" } }),
                new VoxrCommandDefinition("status_report",
                    new[] { new[] { "status", "report", "{target}" } }),
            };
            _recogniser.Configure(slots, commands);
            _recogniser.BufferWindow = 1.5f;
            _recogniser.CommandCooldown = 0f;
            _recogniser.EagerFlushOnCompleteMatch = true;

            int fireCount = 0;
            _recogniser.OnCommandRecognised += _ => fireCount++;

            // "status" is a prefix of "status report {target}", so it must NOT eager-fire.
            _recogniser.InjectText("status");
            Assert.AreEqual(0, fireCount,
                "A command that is a prefix of a longer one must wait the full window");

            _recogniser.FlushPendingBuffer();
            Assert.AreEqual(1, fireCount, "It still fires on the normal flush");
        }

        [Test]
        public void EagerFlush_TrailingExtensibleSlot_WaitsForTheWindow()
        {
            var slots = new[]
            {
                new VoxrSlotDefinition("colour", new[] { "red", "red dragon" }),
            };
            var commands = new[]
            {
                new VoxrCommandDefinition("pick", new[] { new[] { "pick", "{colour}" } }),
            };
            _recogniser.Configure(slots, commands);
            _recogniser.BufferWindow = 1.5f;
            _recogniser.CommandCooldown = 0f;
            _recogniser.EagerFlushOnCompleteMatch = true;

            int fireCount = 0;
            _recogniser.OnCommandRecognised += _ => fireCount++;

            // "pick red" could still grow into "pick red dragon", so it must NOT eager-fire.
            _recogniser.InjectText("pick red");
            Assert.AreEqual(0, fireCount,
                "A trailing slot whose value can grow must wait the full window");

            _recogniser.FlushPendingBuffer();
            Assert.AreEqual(1, fireCount);
        }

        [Test]
        public void EagerFlush_SplitCommand_FiresWhenSecondHalfCompletes()
        {
            _recogniser.Configure(MakeSlots(), MakeCommands());
            _recogniser.BufferWindow = 1.5f;
            _recogniser.CommandCooldown = 0f;
            _recogniser.EagerFlushOnCompleteMatch = true;

            int fireCount = 0;
            VoxrCommand? received = null;
            _recogniser.OnCommandRecognised += cmd => { fireCount++; received = cmd; };

            // First half is incomplete (target slot unfilled) -> below threshold -> no fire.
            _recogniser.InjectText("launch all missiles");
            Assert.AreEqual(0, fireCount, "An incomplete command must not eager-fire");

            // The second half completes the command on the merged buffer -> eager fire.
            _recogniser.InjectText("target hotel one");
            Assert.AreEqual(1, fireCount, "Completing the command should eager-fire immediately");
            Assert.AreEqual("hotel one", received.Value.GetSlot("target"));
        }

        // -------- Eager flush: confirmation, pending, and number sequences (review fix #7) --------

        [Test]
        public void EagerFlush_RequiresConfirmation_EntersPendingNotFire()
        {
            var commands = new[]
            {
                new VoxrCommandDefinition("arm", new[] { new[] { "arm", "system" } },
                    allowPartialMatch: false, requiresConfirmation: true),
            };
            _recogniser.Configure(new VoxrSlotDefinition[0], commands);
            _recogniser.BufferWindow = 1.5f;
            _recogniser.CommandCooldown = 0f;
            _recogniser.EagerFlushOnCompleteMatch = true;

            int recognised = 0;
            VoxrCommand? pending = null;
            _recogniser.OnCommandRecognised += _ => recognised++;
            _recogniser.OnCommandPending += cmd => pending = cmd;

            _recogniser.InjectText("arm system");

            Assert.IsTrue(pending.HasValue,
                "A complete command requiring confirmation should eager-flush into pending");
            Assert.AreEqual("arm", pending.Value.Intent);
            Assert.AreEqual(0, recognised,
                "It must enter pending, not fire OnCommandRecognised directly");
            Assert.IsTrue(_recogniser.HasPendingCommand);
        }

        [Test]
        public void EagerFlush_WhilePending_DoesNotEagerFire()
        {
            var commands = new[]
            {
                new VoxrCommandDefinition("arm", new[] { new[] { "arm", "system" } },
                    allowPartialMatch: false, requiresConfirmation: true),
                new VoxrCommandDefinition("cease_fire", new[] { new[] { "cease", "fire" } }),
            };
            _recogniser.Configure(new VoxrSlotDefinition[0], commands);
            _recogniser.BufferWindow = 1.5f;
            _recogniser.CommandCooldown = 0f;
            _recogniser.EagerFlushOnCompleteMatch = true;

            int recognised = 0;
            _recogniser.OnCommandRecognised += _ => recognised++;

            // Enter pending via the confirmation path.
            _recogniser.InjectText("arm system");
            Assert.IsTrue(_recogniser.HasPendingCommand, "Setup: arm system should be pending");
            Assert.AreEqual(0, recognised);

            // A new terminal command must NOT eager-fire while a command is pending (the
            // !_pending.HasPending guard suppresses eager); it stays buffered.
            _recogniser.InjectText("cease fire");
            Assert.AreEqual(0, recognised,
                "Eager flush must be suppressed while a command is pending");

            // Flushing the buffer releases the buffered command (which preempts the pending one).
            _recogniser.FlushPendingBuffer();
            Assert.AreEqual(1, recognised, "The buffered command fires on an explicit flush");
        }

        [Test]
        public void EagerFlush_FixedWidthNumberSequence_FiresImmediately()
        {
            var slots = new[] { VoxrSlotDefinition.NumberSequence("code", 4, 4) };
            var commands = new[]
            {
                new VoxrCommandDefinition("enter", new[] { new[] { "enter", "{code}" } }),
            };
            _recogniser.Configure(slots, commands);
            _recogniser.BufferWindow = 1.5f;
            _recogniser.CommandCooldown = 0f;
            _recogniser.EagerFlushOnCompleteMatch = true;

            VoxrCommand? received = null;
            _recogniser.OnCommandRecognised += cmd => received = cmd;

            _recogniser.InjectText("enter one two three four");

            Assert.IsTrue(received.HasValue,
                "A fixed-width number sequence completes the command, so it should eager-fire");
            Assert.AreEqual("enter", received.Value.Intent);
            Assert.AreEqual("one two three four", received.Value.GetSlot("code"));
        }

        [Test]
        public void EagerFlush_VariableWidthNumberSequence_WaitsForWindow()
        {
            var slots = new[] { VoxrSlotDefinition.NumberSequence("code", 1, 4) };
            var commands = new[]
            {
                new VoxrCommandDefinition("enter", new[] { new[] { "enter", "{code}" } }),
            };
            _recogniser.Configure(slots, commands);
            _recogniser.BufferWindow = 1.5f;
            _recogniser.CommandCooldown = 0f;
            _recogniser.EagerFlushOnCompleteMatch = true;

            int fireCount = 0;
            _recogniser.OnCommandRecognised += _ => fireCount++;

            // A variable-width number sequence can always absorb another digit, so the command
            // is not eager-committable — it must wait for the buffer window.
            _recogniser.InjectText("enter one two three");
            Assert.AreEqual(0, fireCount,
                "A variable-width number sequence must not eager-fire (more digits may follow)");

            _recogniser.FlushPendingBuffer();
            Assert.AreEqual(1, fireCount, "It still fires on the normal flush");
        }

        // -------- Prefix hold (issue #32) --------
        //
        // The hold shortens how long Update waits, and Time.time cannot be advanced from a
        // test, so these assert on the effective window the buffer is being timed against
        // rather than on wall-clock firing.

        static VoxrSlotDefinition[] PrefixHoldSlots() => new[]
        {
            new VoxrSlotDefinition("target", new[] { "hotel one", "hotel two" }),
        };

        static VoxrCommandDefinition[] PrefixHoldCommands() => new[]
        {
            new VoxrCommandDefinition("status", new[] { new[] { "status" } }),
            new VoxrCommandDefinition("status_report",
                new[] { new[] { "status", "report", "{target}" } }),
            new VoxrCommandDefinition("cease_fire", new[] { new[] { "cease", "fire" } }),
        };

        void ConfigureForPrefixHold(float prefixHold)
        {
            _recogniser.Configure(PrefixHoldSlots(), PrefixHoldCommands());
            _recogniser.BufferWindow = 2.0f;
            _recogniser.CommandCooldown = 0f;
            _recogniser.EagerFlushOnCompleteMatch = true;
            _recogniser.PrefixHoldSeconds = prefixHold;
        }

        [Test]
        public void PrefixHold_CompleteButExtendableMatch_ShortensTheWindow()
        {
            ConfigureForPrefixHold(0.6f);

            int fireCount = 0;
            _recogniser.OnCommandRecognised += _ => fireCount++;

            // "status" is a prefix of "status report {target}" — still no eager fire...
            _recogniser.InjectText("status");
            Assert.AreEqual(0, fireCount,
                "A prefix command must still not fire the instant it is heard");

            // ...but it now waits only prefixHoldSeconds for the continuation.
            Assert.AreEqual(0.6f, _recogniser.TestEffectiveBufferWindow, 1e-4f);
        }

        [Test]
        public void PrefixHold_Zero_KeepsTheFullWindow()
        {
            // The default (0) must preserve the pre-#32 behaviour exactly.
            ConfigureForPrefixHold(0f);

            _recogniser.InjectText("status");

            Assert.AreEqual(2.0f, _recogniser.TestEffectiveBufferWindow, 1e-4f,
                "prefixHoldSeconds = 0 leaves the full buffer window in force");
        }

        [Test]
        public void PrefixHold_LongerThanBufferWindow_NeverLengthensTheWait()
        {
            ConfigureForPrefixHold(5.0f);

            _recogniser.InjectText("status");

            Assert.AreEqual(2.0f, _recogniser.TestEffectiveBufferWindow, 1e-4f,
                "the hold may only shorten the wait, never extend it");
        }

        [Test]
        public void PrefixHold_UnmatchedSpeech_KeepsTheFullWindow()
        {
            // Partial speech that matches nothing yet is exactly the split-command case the
            // full window exists to recover — it must not be cut short.
            ConfigureForPrefixHold(0.6f);

            _recogniser.InjectText("cease");

            Assert.AreEqual(2.0f, _recogniser.TestEffectiveBufferWindow, 1e-4f,
                "an incomplete utterance is not a held complete match");
        }

        [Test]
        public void PrefixHold_ContinuationArrives_RestoresTheFullWindow()
        {
            ConfigureForPrefixHold(0.6f);

            _recogniser.InjectText("status");
            Assert.AreEqual(0.6f, _recogniser.TestEffectiveBufferWindow, 1e-4f, "setup: held");

            // "status report" is an incomplete match of the longer command, so the buffer
            // goes back to waiting the full window for the {target} that completes it.
            _recogniser.InjectText("report");
            Assert.AreEqual(2.0f, _recogniser.TestEffectiveBufferWindow, 1e-4f,
                "the hold must be re-derived per result, not carried forward");
        }

        [Test]
        public void PrefixHold_EagerFlushOff_KeepsTheFullWindow()
        {
            // The hold is part of the eager-flush analysis; without it the buffer stays
            // purely time-driven no matter what prefixHoldSeconds says.
            _recogniser.Configure(PrefixHoldSlots(), PrefixHoldCommands());
            _recogniser.BufferWindow = 2.0f;
            _recogniser.CommandCooldown = 0f;
            _recogniser.PrefixHoldSeconds = 0.6f;

            _recogniser.InjectText("status");

            Assert.AreEqual(2.0f, _recogniser.TestEffectiveBufferWindow, 1e-4f);
        }

        [Test]
        public void PrefixHold_ClearedOnFlush()
        {
            ConfigureForPrefixHold(0.6f);

            _recogniser.InjectText("status");
            Assert.AreEqual(0.6f, _recogniser.TestEffectiveBufferWindow, 1e-4f, "setup: held");

            _recogniser.FlushPendingBuffer();

            Assert.AreEqual(2.0f, _recogniser.TestEffectiveBufferWindow, 1e-4f,
                "a flushed buffer holds nothing, so the next utterance starts on the full window");
        }

        // -------- Un-analysable grammar degrades to the hold (issue #44) --------

        // The prefix-hold grammar plus one pattern past MaxOptionalExpansion (12), which
        // abandons the eager-eligibility analysis for the whole command set.
        static VoxrCommandDefinition[] UnanalysableCommands()
        {
            // 13 optional literals, written space-separated for legibility.
            var noisy = new VoxrCommandDefinition(
                "noisy",
                new[] { "noisy ?a ?b ?c ?d ?e ?f ?g ?h ?i ?j ?k ?l ?m".Split(' ') }
            );

            var basic = PrefixHoldCommands();
            var all = new VoxrCommandDefinition[basic.Length + 1];
            basic.CopyTo(all, 0);
            all[basic.Length] = noisy;
            return all;
        }

        void ConfigureForUnanalysableGrammar(float prefixHold)
        {
            // Construction is where the over-limit pattern is reported now.
            LogAssert.Expect(LogType.Warning, new Regex("more than the 12"));

            _recogniser.Configure(PrefixHoldSlots(), UnanalysableCommands());
            _recogniser.BufferWindow = 2.0f;
            _recogniser.CommandCooldown = 0f;
            _recogniser.EagerFlushOnCompleteMatch = true;
            _recogniser.PrefixHoldSeconds = prefixHold;
        }

        [Test]
        public void UnanalysableGrammar_HoldsInsteadOfPayingTheFullWindow()
        {
            ConfigureForUnanalysableGrammar(0.6f);

            int fireCount = 0;
            _recogniser.OnCommandRecognised += _ => fireCount++;

            // "cease fire" is terminal and prefixes nothing, so it would commit early in an
            // analysable set. Without the analysis it must not — but it is still a complete,
            // confident, whole-buffer match, so it waits the short hold, not the full window.
            _recogniser.InjectText("cease fire");

            Assert.AreEqual(
                0,
                fireCount,
                "nothing commits early on a grammar the eligibility analysis never vetted"
            );
            Assert.AreEqual(0.6f, _recogniser.TestEffectiveBufferWindow, 1e-4f);
        }

        [Test]
        public void UnanalysableGrammar_ZeroHold_KeepsTheFullWindow()
        {
            // Grammars that never opted into the short hold see exactly the old behaviour.
            ConfigureForUnanalysableGrammar(0f);

            _recogniser.InjectText("cease fire");

            Assert.AreEqual(2.0f, _recogniser.TestEffectiveBufferWindow, 1e-4f);
        }

        [Test]
        public void UnanalysableGrammar_IncompleteSpeech_KeepsTheFullWindow()
        {
            // The degrade sits behind the completeness gates, so a half-spoken command still
            // gets the full window to be completed in.
            ConfigureForUnanalysableGrammar(0.6f);

            _recogniser.InjectText("cease");

            Assert.AreEqual(2.0f, _recogniser.TestEffectiveBufferWindow, 1e-4f);
        }
    }
}
