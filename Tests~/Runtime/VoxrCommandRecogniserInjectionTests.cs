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
    }
}
