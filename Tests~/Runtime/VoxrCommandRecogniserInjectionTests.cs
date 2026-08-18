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
            LogAssert.NoUnexpectedReceived();
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

        [Test]
        public void EagerFlush_PatternStillOwingItsLastWord_FiresTheRightCommand()
        {
            // Issue #70 at the level where its harm actually shows. Two commands share a
            // three-word prefix and diverge only on the last word, so the buffer "set auto
            // pilot" matches both at (1 + 1 + 1 + 0) / 4 = 0.75 — over the default
            // minScore, with the match reaching the buffer end because a missed word consumes
            // no tokens. Before the tail condition this eager-fired autopilot_on on
            // registration order alone, while the speaker was still saying "off": not an early
            // fire but the WRONG command, and the real one then never arrived.
            var commands = new[]
            {
                new VoxrCommandDefinition(
                    "autopilot_on",
                    new[] { new[] { "set", "auto", "pilot", "on" } }
                ),
                new VoxrCommandDefinition(
                    "autopilot_off",
                    new[] { new[] { "set", "auto", "pilot", "off" } }
                ),
            };

            _recogniser.Configure(new VoxrSlotDefinition[0], commands);
            _recogniser.BufferWindow = 1.5f;
            _recogniser.CommandCooldown = 0f;
            _recogniser.EagerFlushOnCompleteMatch = true;

            int fireCount = 0;
            VoxrCommand? received = null;
            _recogniser.OnCommandRecognised += cmd =>
            {
                fireCount++;
                received = cmd;
            };

            _recogniser.InjectText("set auto pilot");
            Assert.AreEqual(
                0,
                fireCount,
                "a pattern still owing its last word must not eager-fire — it would fire the "
                    + "first-registered sibling, not the command being spoken"
            );

            _recogniser.InjectText("off");
            Assert.AreEqual(1, fireCount, "the completed command fires once the last word lands");
            Assert.AreEqual(
                "autopilot_off",
                received.Value.Intent,
                "and it must be the command the speaker actually said"
            );
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
            // Construction is where the over-limit pattern is reported now. Two warnings land
            // here, not one: the prefix-hold grammar this builds on is itself a droppable-
            // required-literal shape ("status" is a bare form of "status report {target}"),
            // which is the very thing the hold exists to exercise, so the #42 check fires too.
            // Expectations are matched in queue order against the logs in emission order, and
            // the ctor runs the #42 scan before the optional-expansion one — so this pair must
            // stay in this order.
            LogAssert.Expect(
                LogType.Warning,
                new Regex(@"""status"" \(intent 'status'\) is a bare form of")
            );
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
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void UnanalysableGrammar_ZeroHold_KeepsTheFullWindow()
        {
            // Grammars that never opted into the short hold see exactly the old behaviour.
            ConfigureForUnanalysableGrammar(0f);

            _recogniser.InjectText("cease fire");

            Assert.AreEqual(2.0f, _recogniser.TestEffectiveBufferWindow, 1e-4f);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void UnanalysableGrammar_IncompleteSpeech_KeepsTheFullWindow()
        {
            // The degrade sits behind the completeness gates, so a half-spoken command still
            // gets the full window to be completed in.
            ConfigureForUnanalysableGrammar(0.6f);

            _recogniser.InjectText("cease");

            Assert.AreEqual(2.0f, _recogniser.TestEffectiveBufferWindow, 1e-4f);
            LogAssert.NoUnexpectedReceived();
        }

        // -------- Required-literal miss cost (issue #65 §5.1) --------
        //
        // The parser-level tests pin the arithmetic; these pin the claim the requirements
        // actually make, which is about behaviour. Parse applies no threshold of its own —
        // minScore lives here — so "scores 0.667" and "the command fires" are two different
        // statements, and only this level can make the second one.

        [Test]
        public void MissedLiteral_ThreeElementPattern_NowFires()
        {
            // Symptom 1, end to end. "time to target" heard as "time target" scores
            // (1 + 0 + 1) / 3 = 0.667 and clears the default minScore of 0.6. Before the miss
            // cost changed this was 0.5 and the user got silence.
            _recogniser.Configure(
                new VoxrSlotDefinition[0],
                new[]
                {
                    new VoxrCommandDefinition(
                        "time_to_target",
                        new[] { new[] { "time", "to", "target" } }
                    ),
                }
            );
            _recogniser.BufferWindow = 0f;
            _recogniser.CommandCooldown = 0f;

            VoxrCommand? received = null;
            int unrecognisedCount = 0;
            _recogniser.OnCommandRecognised += cmd => received = cmd;
            _recogniser.OnUnrecognisedSpeech += _ => unrecognisedCount++;

            _recogniser.InjectText("time target");

            Assert.IsTrue(
                received.HasValue,
                "a single dropped function word must not silence a three-element command"
            );
            Assert.AreEqual("time_to_target", received.Value.Intent);
            Assert.AreEqual(2f / 3f, received.Value.Score, 0.001f);
            Assert.AreEqual(0, unrecognisedCount);
        }

        [Test]
        public void MissedLiteral_TwoElementPattern_DoesNotFire()
        {
            // The other half of §5.1, and the reason the miss cost was reduced rather than
            // removed: "cease fire" heard as "fire" is genuinely ambiguous, scores
            // (0 + 1) / 2 = 0.5, and must stay under the gate.
            ConfigureWithSyncDefaults();

            int recognisedCount = 0;
            string unrecognised = null;
            _recogniser.OnCommandRecognised += _ => recognisedCount++;
            _recogniser.OnUnrecognisedSpeech += text => unrecognised = text;

            _recogniser.InjectText("fire");

            Assert.AreEqual(0, recognisedCount, "half the evidence must not fire a command");
            Assert.AreEqual("fire", unrecognised);
        }

        [Test]
        public void MissedLiteral_BoundaryCase_ExactlySixTenths_Fires()
        {
            // G1 ruling 2, at the level that can actually test it. Two drops on a five-element
            // pattern score (1 + 0 + 0 + 1 + 1) / 5 = 0.60 — the gate value itself. The gate is
            // >=, so this fires; the parser-level test can only assert the number, not that the
            // comparison admits it.
            _recogniser.Configure(
                new[] { new VoxrSlotDefinition("burn_level", new[] { "coast", "hard burn" }) },
                new[]
                {
                    new VoxrCommandDefinition(
                        "set_burn",
                        new[] { new[] { "set", "burn", "to", "{burn_level}", "now" } }
                    ),
                }
            );
            _recogniser.BufferWindow = 0f;
            _recogniser.CommandCooldown = 0f;

            VoxrCommand? received = null;
            _recogniser.OnCommandRecognised += cmd => received = cmd;

            _recogniser.InjectText("set hard burn now");

            Assert.IsTrue(received.HasValue, "a score of exactly minScore must pass the >= gate");
            Assert.AreEqual(3f / 5f, received.Value.Score, 0.001f);
            Assert.AreEqual(
                "hard burn",
                received.Value.GetSlot("burn_level"),
                "firing on the boundary is only right because every argument is present"
            );
        }

        // -------- Flush-path completeness (issue #73) --------
        //
        // TryEagerCommit has refused a command with an unfilled required slot since #66. The
        // ordinary flush path — the one on by default — had no such condition, so a slot-missing
        // candidate that cleared minScore fired with its argument simply absent.
        //
        // The claim is "an incomplete command does not fire AT ANY SCORE", so the cases below
        // are spread across the range instead of clustered on the gate value (issue #76). A case
        // pinned to 0.60 witnesses only "does not fire at 0.60", which the suite would keep
        // reporting green if the completeness term were deleted and the gate loosened to
        // `> minScore` — the exact regression these tests exist to catch.

        // Pins the candidate's score against the caller's hand-derived expectation, then asserts
        // the recogniser refuses to fire it.
        //
        // The two guards are the point, and they close different doors. Without them a
        // does-not-fire assertion FAILS OPEN: a candidate that ended up below the gate would
        // leave the test green for the wrong reason — rejected on score, never reaching the
        // completeness branch — with nothing to signal that it had gone vacuous. The score pin
        // catches the candidate moving (a change to the denominator); reading MinScore off the
        // recogniser catches the gate moving underneath a candidate that did not. A hard-coded
        // 0.60 would catch neither, being implied by the pin directly above it.
        //
        // Per the #65 §7.3 discipline the expected scores are hand-derived and argued at each
        // call site, never updated to whatever the code happens to emit.
        void AssertIncompleteDoesNotFire(
            VoxrSlotDefinition[] slots,
            VoxrCommandDefinition[] commands,
            string utterance,
            float expectedScore,
            string strandedSlot
        )
        {
            var probe = new VoxrCommandParser(slots, commands).Parse(utterance);
            Assert.AreEqual(1, probe.Length, "the utterance must yield exactly one candidate");
            Assert.IsTrue(probe[0].IsMatch, "and that candidate must be a match");
            Assert.AreEqual(
                expectedScore,
                probe[0].Command.Score,
                0.001f,
                "the hand-derived score no longer holds — re-derive it and argue the new value "
                    + "before touching anything else, because everything below rests on it"
            );
            Assert.GreaterOrEqual(
                probe[0].Command.Score,
                _recogniser.MinScore,
                "the candidate must clear the gate the recogniser is actually running, or the "
                    + "refusal below proves nothing about completeness"
            );
            Assert.IsFalse(
                probe[0].Command.HasSlot(strandedSlot),
                $"'{strandedSlot}' must really be unfilled — that is what the recogniser rejects on"
            );

            _recogniser.Configure(slots, commands);
            _recogniser.BufferWindow = 0f;
            _recogniser.CommandCooldown = 0f;

            int recognisedCount = 0;
            string unrecognised = null;
            _recogniser.OnCommandRecognised += _ => recognisedCount++;
            _recogniser.OnUnrecognisedSpeech += text => unrecognised = text;

            _recogniser.InjectText(utterance);

            Assert.AreEqual(
                0,
                recognisedCount,
                "a command missing a required argument must not fire, whatever it scores"
            );
            Assert.AreEqual(
                utterance,
                unrecognised,
                "and the utterance is reported unrecognised rather than dropped in silence"
            );
        }

        [Test]
        public void SlotMissing_OnTheGate_DoesNotFire()
        {
            // #73's own repro, kept because it is the shape that was actually reported, and the
            // deliberate counterpart to the boundary test above: both land on exactly 0.60 and
            // both clear the >= gate, so score cannot be what separates them. "launch all
            // missiles target" matches launch, {?quantity}, {weapon} and target, then strands
            // {target}: (1 + 1 + 1 + 1 - 1) / 5 = 0.60. Firing it hands the handler a launch
            // order with nothing to launch at.
            //
            // Sitting on the boundary is precisely what the helper's score pin protects: nothing
            // else would notice this candidate sliding underneath the gate.
            AssertIncompleteDoesNotFire(
                MakeSlots(),
                MakeCommands(),
                "launch all missiles target",
                3f / 5f,
                "target"
            );
        }

        [Test]
        public void SlotMissing_AboveGate_DoesNotFire()
        {
            // Clear of the boundary, so the completeness branch is the only condition in Step 7
            // that can reject this. Eight required elements — nothing optional, so the
            // denominator is the pattern length outright — with seven matched and the trailing
            // {target} stranded: (7 x 1 - 1) / 8 = 0.75.
            var slots = new[]
            {
                new VoxrSlotDefinition("target", new[] { "hotel one", "hotel two", "alpha one" }),
                new VoxrSlotDefinition("weapon", new[] { "missiles", "torpedoes" }),
                new VoxrSlotDefinition("quantity", new[] { "all", "one", "two" }),
                new VoxrSlotDefinition("tube", new[] { "one", "two", "three" }),
            };
            var commands = new[]
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
                    }
                ),
                new VoxrCommandDefinition("cease_fire", new[] { new[] { "cease", "fire" } }),
            };

            AssertIncompleteDoesNotFire(
                slots,
                commands,
                "launch all missiles from tube three at",
                6f / 8f,
                "target"
            );
        }

        [Test]
        public void SlotMissing_FarAboveGate_DoesNotFire()
        {
            // The witness for "at ANY score" (issue #76). One stranded slot on a fourteen-element
            // pattern: (13 x 1 - 1) / 14 = 0.857 — a full 0.257 above the gate, which no
            // plausible drift in the denominator moves under it. Concretely, coverage would have
            // to charge more than six whole tokens' worth of unexplained speech — 12 / (14 + c)
            // < 0.60 needs c > 6 — against an utterance the pattern consumes end to end.
            //
            // The pattern is deliberately long: with every element required and exactly one slot
            // stranded the score is (N - 2) / N, so headroom above the gate is bought only with
            // length. {target} is stranded mid-pattern and the tail still matches, which is also
            // what keeps the coverage term at zero here — "on my mark" consumes the rest, so
            // nothing is left orphaned after the last match.
            var slots = new[]
            {
                new VoxrSlotDefinition("target", new[] { "hotel one", "hotel two", "alpha one" }),
                new VoxrSlotDefinition("weapon", new[] { "missiles", "torpedoes" }),
                new VoxrSlotDefinition("quantity", new[] { "all", "one", "two" }),
                new VoxrSlotDefinition("tube", new[] { "one", "two", "three" }),
            };
            var commands = new[]
            {
                new VoxrCommandDefinition(
                    "launch_weapon",
                    new[]
                    {
                        new[]
                        {
                            "weapons",
                            "free",
                            "launch",
                            "{quantity}",
                            "{weapon}",
                            "from",
                            "tube",
                            "{tube}",
                            "at",
                            "target",
                            "{target}",
                            "on",
                            "my",
                            "mark",
                        },
                    }
                ),
            };

            AssertIncompleteDoesNotFire(
                slots,
                commands,
                "weapons free launch all missiles from tube three at target on my mark",
                12f / 14f,
                "target"
            );
        }

        [Test]
        public void SlotMissing_OptionalSlotOmitted_StillFires()
        {
            // The over-correction guard, and the line #66 already draws at the eager gate: an
            // omitted OPTIONAL slot is not an absent argument. "launch missiles target hotel one"
            // skips {?quantity} and fills every required slot, so it scores 4/4 and must fire.
            ConfigureWithSyncDefaults();

            VoxrCommand? received = null;
            _recogniser.OnCommandRecognised += cmd => received = cmd;

            _recogniser.InjectText("launch missiles target hotel one");

            Assert.IsTrue(received.HasValue, "an omitted optional slot is not a missing argument");
            Assert.AreEqual("launch_weapon", received.Value.Intent);
            Assert.AreEqual("hotel one", received.Value.GetSlot("target"));
            Assert.IsFalse(
                received.Value.HasSlot("quantity"),
                "and it really was omitted — this is not passing for the wrong reason"
            );
        }

        // -------- Sibling tie defers the eager commit (issue #74, DR-5) --------
        //
        // The parser-level tests in VoxrEagerCommitTests call TryEagerCommit directly, which
        // returns a verdict and therefore cannot show what happens AFTER a refusal. These two
        // cover the claim that matters most about this feature — that refusing costs latency
        // and nothing else, so nothing which fires today stops firing.
        //
        // The discriminator must be MEDIAL. A trailing one is already refused by the issue #70
        // tail condition, so a fixture built on one would go green without exercising the
        // sibling condition at all.

        void ConfigureMedialSiblings()
        {
            _recogniser.Configure(
                new[] { new VoxrSlotDefinition("ship", new[] { "alpha" }) },
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
            _recogniser.BufferWindow = 1.5f;
            _recogniser.CommandCooldown = 0f;
            _recogniser.EagerFlushOnCompleteMatch = true;
        }

        [Test]
        public void EagerFlush_SiblingTie_DefersToTheWindowThenStillFires()
        {
            LogAssert.Expect(LogType.Warning, new Regex("differ only at element 3"));
            ConfigureMedialSiblings();

            VoxrCommand? received = null;
            int fireCount = 0;
            _recogniser.OnCommandRecognised += cmd =>
            {
                received = cmd;
                fireCount++;
            };

            // "set alpha on" fits set_mode and set_level exactly equally. Before DR-5 this
            // eager-fired immediately on the coin flip.
            _recogniser.InjectText("set alpha on");
            Assert.AreEqual(
                0,
                fireCount,
                "an undecidable buffer must not commit early — the dropped word may still arrive"
            );

            _recogniser.FlushPendingBuffer();

            Assert.AreEqual(1, fireCount, "and it must still fire: the refusal costs latency only");
            Assert.AreEqual(
                "set_mode",
                received.Value.Intent,
                "the same intent the flush has always chosen, by registration order"
            );
            Assert.AreEqual("alpha", received.Value.GetSlot("ship"), "with its slots intact");
        }

        [Test]
        public void EagerFlush_SiblingTie_UnambiguousUtteranceIsUnaffected()
        {
            // A test asserting that the deferred window lets the missing word land used to sit
            // here. It was removed rather than repaired: for a MEDIAL discriminator that
            // scenario cannot happen. Reaching the sibling condition means the issue #70 tail
            // condition passed, so an element after the dropped word already matched, and
            // HandleResult only ever APPENDS to the buffer — nothing can fill a position the
            // match has gone past. Design §5.8's "the missing word may still arrive" describes
            // the trailing shape, which #70 refuses long before this feature is reached.
            //
            // What is worth pinning instead is the boundary: say the whole thing and the tie
            // never forms, so the refusal costs nothing on unambiguous speech.
            LogAssert.Expect(LogType.Warning, new Regex("differ only at element 3"));
            ConfigureMedialSiblings();

            VoxrCommand? received = null;
            int fireCount = 0;
            _recogniser.OnCommandRecognised += cmd =>
            {
                received = cmd;
                fireCount++;
            };

            _recogniser.InjectText("set alpha level on");

            Assert.AreEqual(
                1,
                fireCount,
                "no tie, so the gate commits immediately exactly as it always did"
            );
            Assert.AreEqual("set_level", received.Value.Intent, "and on the spoken intent");
        }

        // -------- The flush ASKS instead of guessing (issue #74 item 3) --------
        //
        // Item 2 bought a pause and spent it on nothing: the eager gate refuses, the window
        // expires, the flush ties, registration order decides, and the first-registered sibling
        // fires — the same coin flip, reached later. These pin what makes that latency worth
        // paying.
        //
        // ALL of them drive VoxrCommandRecogniser and assert on real events. Item 2's review
        // found that its two most important tests were never built, because every eager test
        // called TryEagerCommit as a pure function and so structurally could not observe what
        // happened after a refusal. A parser-level test here can prove a rival was RECORDED and
        // prove nothing about whether the speaker is ever ASKED.
        //
        // disambiguateSiblingTies is frozen into the parser at Configure time, so it is set
        // BEFORE Configure in every one of these. Getting that wrong is invisible in the Editor:
        // the parser records ties whenever the flag is set OR UNITY_EDITOR is defined.

        void ConfigureAsking(bool flag = true, params VoxrCommandDefinition[] extra)
        {
            var commands = new System.Collections.Generic.List<VoxrCommandDefinition>
            {
                new VoxrCommandDefinition(
                    "set_mode",
                    new[] { new[] { "set", "{ship}", "mode", "on" } }
                ),
                new VoxrCommandDefinition(
                    "set_level",
                    new[] { new[] { "set", "{ship}", "level", "on" } }
                ),
            };
            commands.AddRange(extra);

            _recogniser.DisambiguateSiblingTies = flag;
            _recogniser.Configure(
                new[] { new VoxrSlotDefinition("ship", new[] { "alpha" }) },
                commands.ToArray()
            );
            _recogniser.BufferWindow = 1.5f;
            _recogniser.CommandCooldown = 0f;
        }

        // The follow-up goes through the buffer exactly as the first utterance does — Step 1's
        // confirm/cancel/choice check runs inside ProcessParsedResultsCore, which a non-zero
        // bufferWindow reaches only on flush. Injecting an answer without flushing asserts
        // nothing: the pending is still live because the answer was never delivered, so a test
        // that forgets this passes its "still pending" asserts vacuously and fails its real one.
        // Named rather than inlined so it cannot be forgotten twice.
        void Answer(string utterance)
        {
            _recogniser.InjectText(utterance);
            _recogniser.FlushPendingBuffer();
        }

        [Test]
        public void Disambiguation_AmbiguousUtterance_AsksInsteadOfFiring()
        {
            LogAssert.Expect(LogType.Warning, new Regex("differ only at element 3"));
            ConfigureAsking();

            int pendingCount = 0,
                firedCount = 0,
                unrecognisedCount = 0;
            _recogniser.OnCommandPending += _ => pendingCount++;
            _recogniser.OnCommandRecognised += _ => firedCount++;
            _recogniser.OnUnrecognisedSpeech += _ => unrecognisedCount++;

            _recogniser.InjectText("set alpha on");
            _recogniser.FlushPendingBuffer();

            Assert.AreEqual(1, pendingCount, "the speaker is asked");
            Assert.AreEqual(0, firedCount, "and nothing fires on the coin flip");
            Assert.AreEqual(
                0,
                unrecognisedCount,
                "and the integrator is not told the speech was not understood in the same frame "
                    + "it was asked to prompt about it"
            );
            Assert.IsTrue(_recogniser.HasPendingCommand);
        }

        [Test]
        public void Disambiguation_FlagOff_FiresTheFirstRegisteredExactlyAsBefore()
        {
            // F2's control, at the level that matters: the whole feature is downstream of the
            // flag, and with it clear this fixture behaves as it does on main.
            LogAssert.Expect(LogType.Warning, new Regex("differ only at element 3"));
            ConfigureAsking(flag: false);

            int pendingCount = 0;
            VoxrCommand? received = null;
            _recogniser.OnCommandPending += _ => pendingCount++;
            _recogniser.OnCommandRecognised += cmd => received = cmd;

            _recogniser.InjectText("set alpha on");
            _recogniser.FlushPendingBuffer();

            Assert.AreEqual(0, pendingCount, "no question is asked");
            Assert.IsTrue(received.HasValue, "and the command still fires");
            Assert.AreEqual("set_mode", received.Value.Intent, "first-registered, as always");
            Assert.IsNull(_recogniser.PendingAmbiguity);
        }

        [Test]
        public void Disambiguation_TheAnswer_FiresTheChosenIntentWithItsOwnSlots()
        {
            LogAssert.Expect(LogType.Warning, new Regex("differ only at element 3"));
            ConfigureAsking();

            VoxrCommand? confirmed = null;
            VoxrCommand? received = null;
            _recogniser.OnCommandConfirmed += cmd => confirmed = cmd;
            _recogniser.OnCommandRecognised += cmd => received = cmd;

            _recogniser.InjectText("set alpha on");
            _recogniser.FlushPendingBuffer();
            Answer("level");

            Assert.IsTrue(confirmed.HasValue, "resolving a pending raises OnCommandConfirmed");
            Assert.IsTrue(received.HasValue, "…then OnCommandRecognised");
            Assert.AreEqual("set_level", received.Value.Intent, "the intent the speaker chose");
            Assert.AreEqual(
                "alpha",
                received.Value.GetSlot("ship"),
                "carrying the slots ITS own match produced, not the winner's (F6)"
            );
            Assert.IsFalse(_recogniser.HasPendingCommand, "and the question is closed");
        }

        [Test]
        public void Disambiguation_AnsweringWithTheWinnersOwnValue_FiresTheWinner()
        {
            // Index 0 is a choice too. The speaker who meant the first-registered intent still
            // has to say something, and the word that identifies it is its own discriminator.
            LogAssert.Expect(LogType.Warning, new Regex("differ only at element 3"));
            ConfigureAsking();

            VoxrCommand? received = null;
            _recogniser.OnCommandRecognised += cmd => received = cmd;

            _recogniser.InjectText("set alpha on");
            _recogniser.FlushPendingBuffer();
            Answer("mode");

            Assert.IsTrue(received.HasValue);
            Assert.AreEqual("set_mode", received.Value.Intent);
        }

        [Test]
        public void Disambiguation_PendingAmbiguity_ExposesTheReasonAndTheChoices()
        {
            // F11. Without this the opt-in is unusable: OnCommandPending carries only the
            // command, so an integrator already subscribed for requiresConfirmation would prompt
            // "yes/no" — and "yes" does nothing here, so the pending would time out and, under
            // DR-6, fire nothing. That is the exact failure the opt-in exists to prevent,
            // reappearing inside the opt-in.
            LogAssert.Expect(LogType.Warning, new Regex("differ only at element 3"));
            ConfigureAsking();

            VoxrPendingAmbiguity? seen = null;
            _recogniser.OnCommandPending += _ => seen = _recogniser.PendingAmbiguity;

            _recogniser.InjectText("set alpha on");
            _recogniser.FlushPendingBuffer();

            Assert.IsTrue(seen.HasValue, "readable from inside the OnCommandPending handler");
            CollectionAssert.AreEqual(
                new[] { "mode", "level" },
                seen.Value.DiscriminatingValues,
                "index 0 is what would have fired with the flag off, then registration order"
            );
            CollectionAssert.AreEqual(
                new[] { "set_mode", "set_level" },
                System.Array.ConvertAll(seen.Value.Choices, c => c.Intent)
            );
            Assert.IsFalse(seen.Value.IsTruncated);
        }

        [Test]
        public void Disambiguation_PendingAmbiguity_IsNullUnderAConfirmationPending()
        {
            // The other half of "HasValue is the reason signal". A confirmation pending must
            // read as null, or the property tells the integrator to prompt for a choice that
            // does not exist.
            _recogniser.Configure(
                MakeSlots(),
                new[]
                {
                    new VoxrCommandDefinition(
                        "self_destruct",
                        new[] { new[] { "self", "destruct" } },
                        requiresConfirmation: true
                    ),
                }
            );
            _recogniser.BufferWindow = 0f;
            _recogniser.CommandCooldown = 0f;

            _recogniser.InjectText("self destruct");

            Assert.IsTrue(_recogniser.HasPendingCommand, "it is pending…");
            Assert.IsNull(_recogniser.PendingAmbiguity, "…but not on an ambiguity");
        }

        [Test]
        public void Disambiguation_ThreeWaySet_OffersAndAnswersAllThree()
        {
            // F19 through the recogniser. Under item 2's first-rival rule the third choice was
            // never recorded, so "standby" would have gone unrecognised — on design §5.1's own
            // example grammar.
            LogAssert.Expect(LogType.Warning, new Regex("differ only at element 3"));
            ConfigureAsking(
                true,
                new VoxrCommandDefinition(
                    "set_standby",
                    new[] { new[] { "set", "{ship}", "standby", "on" } }
                )
            );

            VoxrPendingAmbiguity? seen = null;
            _recogniser.OnCommandPending += _ => seen = _recogniser.PendingAmbiguity;
            VoxrCommand? received = null;
            _recogniser.OnCommandRecognised += cmd => received = cmd;

            _recogniser.InjectText("set alpha on");
            _recogniser.FlushPendingBuffer();

            CollectionAssert.AreEqual(
                new[] { "mode", "level", "standby" },
                seen.Value.DiscriminatingValues,
                "three choices, not two"
            );

            Answer("standby");

            Assert.IsTrue(received.HasValue, "and the third answer is understood");
            Assert.AreEqual("set_standby", received.Value.Intent);
        }

        [Test]
        public void Disambiguation_CancelApplies_ConfirmDoesNot()
        {
            // F9. Cancel keeps its precedence and its meaning. Confirm is inert — "yes" is not
            // an answer to "which?" — and deliberately not a cancel either: leaving the pending
            // live lets the speaker follow it with the actual answer inside the same window.
            LogAssert.Expect(LogType.Warning, new Regex("differ only at element 3"));
            ConfigureAsking();

            int cancelledCount = 0,
                firedCount = 0;
            _recogniser.OnCommandCancelled += _ => cancelledCount++;
            _recogniser.OnCommandRecognised += _ => firedCount++;

            _recogniser.InjectText("set alpha on");
            _recogniser.FlushPendingBuffer();

            Answer("yes");
            Assert.AreEqual(0, firedCount, "confirm must not fire the winner");
            Assert.AreEqual(0, cancelledCount, "and must not abandon the question either");
            Assert.IsTrue(_recogniser.HasPendingCommand, "the pending stays live for the answer");

            Answer("level");
            Assert.AreEqual(1, firedCount, "which the speaker can still give");

            Assert.AreEqual(0, cancelledCount);
        }

        [Test]
        public void Disambiguation_CancelWord_CancelsTheQuestion()
        {
            LogAssert.Expect(LogType.Warning, new Regex("differ only at element 3"));
            ConfigureAsking();

            int cancelledCount = 0,
                firedCount = 0;
            _recogniser.OnCommandCancelled += _ => cancelledCount++;
            _recogniser.OnCommandRecognised += _ => firedCount++;

            _recogniser.InjectText("set alpha on");
            _recogniser.FlushPendingBuffer();
            Answer("cancel");

            Assert.AreEqual(1, cancelledCount);
            Assert.AreEqual(0, firedCount);
            Assert.IsFalse(_recogniser.HasPendingCommand);
        }

        [Test]
        public void Disambiguation_FullReUtterance_PreemptsAndFires()
        {
            // F8. Saying the whole thing is always available, and it goes through the ordinary
            // parse path: the re-utterance is complete and unambiguous, so Step 4 preempts the
            // pending before the choice check is ever reached.
            LogAssert.Expect(LogType.Warning, new Regex("differ only at element 3"));
            ConfigureAsking();

            int cancelledCount = 0;
            VoxrCommand? received = null;
            _recogniser.OnCommandCancelled += _ => cancelledCount++;
            _recogniser.OnCommandRecognised += cmd => received = cmd;

            _recogniser.InjectText("set alpha on");
            _recogniser.FlushPendingBuffer();
            _recogniser.InjectText("set alpha level on");
            _recogniser.FlushPendingBuffer();

            Assert.IsTrue(received.HasValue);
            Assert.AreEqual("set_level", received.Value.Intent);
            Assert.AreEqual(
                1,
                cancelledCount,
                "the superseded question is genuinely abandoned, and says so"
            );
            Assert.IsFalse(_recogniser.HasPendingCommand);
        }

        [Test]
        public void Disambiguation_SecondAmbiguousUtterance_ReArmsTheQuestion()
        {
            // F8's other half, and an event the architecture's first draft did not account for.
            // Step 4's completeness test knows nothing about ties, so a second ambiguous
            // utterance ALSO reads as a complete new command and preempts — one
            // OnCommandCancelled for the old question, then one OnCommandPending for the new.
            LogAssert.Expect(LogType.Warning, new Regex("differ only at element 3"));
            ConfigureAsking();

            int pendingCount = 0,
                cancelledCount = 0;
            _recogniser.OnCommandPending += _ => pendingCount++;
            _recogniser.OnCommandCancelled += _ => cancelledCount++;

            _recogniser.InjectText("set alpha on");
            _recogniser.FlushPendingBuffer();
            _recogniser.InjectText("set alpha on");
            _recogniser.FlushPendingBuffer();

            Assert.AreEqual(2, pendingCount, "asked again");
            Assert.AreEqual(1, cancelledCount, "the first question was abandoned to ask it");
            Assert.IsTrue(_recogniser.HasPendingCommand, "and exactly one is live");
            Assert.IsNotNull(_recogniser.PendingAmbiguity);
        }

        [Test]
        public void Disambiguation_AnswerRequiringConfirmation_AsksWhichThenAsksAreYouSure()
        {
            // Complete used to read pending.Definition — the WINNER's — so a chosen rival's
            // requiresConfirmation would have been taken from the wrong command: this
            // destructive intent would have fired the moment it was named. Plan validation
            // caught it; this pins the fix.
            //
            // The order is the only coherent one: you cannot confirm an intent you have not
            // identified.
            LogAssert.Expect(LogType.Warning, new Regex("differ only at element 3"));
            _recogniser.DisambiguateSiblingTies = true;
            _recogniser.Configure(
                new[] { new VoxrSlotDefinition("ship", new[] { "alpha" }) },
                new[]
                {
                    new VoxrCommandDefinition(
                        "set_mode",
                        new[] { new[] { "set", "{ship}", "mode", "on" } }
                    ),
                    new VoxrCommandDefinition(
                        "set_scuttle",
                        new[] { new[] { "set", "{ship}", "scuttle", "on" } },
                        requiresConfirmation: true
                    ),
                }
            );
            _recogniser.BufferWindow = 1.5f;
            _recogniser.CommandCooldown = 0f;

            int firedCount = 0;
            VoxrCommand? received = null;
            _recogniser.OnCommandRecognised += cmd =>
            {
                received = cmd;
                firedCount++;
            };

            _recogniser.InjectText("set alpha on");
            _recogniser.FlushPendingBuffer();
            Answer("scuttle");

            Assert.AreEqual(0, firedCount, "naming a destructive intent must not fire it");
            Assert.IsTrue(_recogniser.HasPendingCommand, "it asks again…");
            Assert.IsNull(
                _recogniser.PendingAmbiguity,
                "…and the second question is a confirmation, not an ambiguity"
            );

            Answer("yes");

            Assert.AreEqual(1, firedCount);
            Assert.AreEqual("set_scuttle", received.Value.Intent);
        }

        [Test]
        public void Disambiguation_WinnerRequiringConfirmation_DoesNotConfirmTheChosenRival()
        {
            // The same defect in the other direction: the winner requires confirmation and the
            // chosen rival does not. Reading the winner's definition would have made the speaker
            // confirm a benign command they had just explicitly named.
            LogAssert.Expect(LogType.Warning, new Regex("differ only at element 3"));
            _recogniser.DisambiguateSiblingTies = true;
            _recogniser.Configure(
                new[] { new VoxrSlotDefinition("ship", new[] { "alpha" }) },
                new[]
                {
                    new VoxrCommandDefinition(
                        "set_scuttle",
                        new[] { new[] { "set", "{ship}", "scuttle", "on" } },
                        requiresConfirmation: true
                    ),
                    new VoxrCommandDefinition(
                        "set_mode",
                        new[] { new[] { "set", "{ship}", "mode", "on" } }
                    ),
                }
            );
            _recogniser.BufferWindow = 1.5f;
            _recogniser.CommandCooldown = 0f;

            VoxrCommand? received = null;
            _recogniser.OnCommandRecognised += cmd => received = cmd;

            _recogniser.InjectText("set alpha on");
            _recogniser.FlushPendingBuffer();
            Answer("mode");

            Assert.IsTrue(received.HasValue, "the benign choice fires straight away");
            Assert.AreEqual("set_mode", received.Value.Intent);
            Assert.IsFalse(_recogniser.HasPendingCommand);
        }

        [Test]
        public void Disambiguation_SetLargerThanTheCap_ReportsTruncationToTheIntegrator()
        {
            // The parser-level test asserts record.Truncated; this asserts the four hops that
            // carry it to the surface an integrator actually reads — TiedSiblingBuffer.Truncated
            // → EnterPending(choicesTruncated) → VoxrPendingCommand.ChoicesTruncated →
            // VoxrPendingAmbiguity.IsTruncated. A hard-coded false at any hop passed the whole
            // suite before this test, because nothing asserted the true direction.
            LogAssert.Expect(LogType.Warning, new Regex("differ only at element 3"));
            _recogniser.DisambiguateSiblingTies = true;
            var commands = new System.Collections.Generic.List<VoxrCommandDefinition>();
            foreach (string v in new[] { "mode", "level", "standby", "trim", "gain", "bias" })
                commands.Add(
                    new VoxrCommandDefinition(
                        "set_" + v,
                        new[] { new[] { "set", "{ship}", v, "on" } }
                    )
                );
            _recogniser.Configure(
                new[] { new VoxrSlotDefinition("ship", new[] { "alpha" }) },
                commands.ToArray()
            );
            _recogniser.BufferWindow = 1.5f;
            _recogniser.CommandCooldown = 0f;

            VoxrPendingAmbiguity? seen = null;
            _recogniser.OnCommandPending += _ => seen = _recogniser.PendingAmbiguity;

            _recogniser.InjectText("set alpha on");
            _recogniser.FlushPendingBuffer();

            Assert.IsTrue(seen.HasValue);
            Assert.AreEqual(
                1 + VoxrCommandParser.MaxDisambiguationRivals,
                seen.Value.Choices.Length,
                "the winner plus the cap's worth of rivals"
            );
            Assert.IsTrue(
                seen.Value.IsTruncated,
                "and the integrator is told there are answers not on this list, so they can "
                    + "word \"…or say the whole command again\""
            );
        }

        [Test]
        public void Disambiguation_AnswerFiresEvenWhenItsIntentIsOnCooldown()
        {
            // Replaces a test that pinned the opposite, and was wrong to. Gating each choice on
            // its own cooldown was tried: on a two-way set it drops the only rival, the question
            // collapses, and the WINNER fires — so the speaker who said "set alpha level on" and
            // was then misheard got set_mode, a command they never uttered, precisely BECAUSE
            // they had just used level.
            //
            // The debouncer exists to suppress duplicate VOSK results, not deliberate answers to
            // a question the recogniser asked. The confirmation path already settles this: it
            // enters pending after the debounce check and fires on confirm without re-checking.
            LogAssert.Expect(LogType.Warning, new Regex("differ only at element 3"));
            ConfigureAsking();
            _recogniser.CommandCooldown = 30f;

            int pendingCount = 0;
            VoxrCommand? received = null;
            _recogniser.OnCommandPending += _ => pendingCount++;
            _recogniser.OnCommandRecognised += cmd => received = cmd;

            _recogniser.InjectText("set alpha level on");
            _recogniser.FlushPendingBuffer();
            Assert.AreEqual("set_level", received.Value.Intent, "set_level is now on cooldown");

            received = null;
            _recogniser.InjectText("set alpha on");
            _recogniser.FlushPendingBuffer();

            Assert.AreEqual(1, pendingCount, "the question is still asked");
            Assert.IsNull(received, "and nothing fires on the coin flip");

            Answer("level");

            Assert.IsTrue(received.HasValue, "the answer fires the intent the speaker chose");
            Assert.AreEqual("set_level", received.Value.Intent);
        }

        [Test]
        public void Disambiguation_TrailingDiscriminator_AlsoAsksAndIsAnswerable()
        {
            // Design §7 item 1 asks for BOTH shapes, and the trailing one reaches the flush for
            // a reason worth stating: issue #70's unmatched-required-tail flag drives the EAGER
            // gate only, and the flush path ignores it by design — at the gate a required tail
            // means the speaker may still be mid-utterance, so refusing costs latency; on the
            // flush the transcript is final and refusing means firing nothing.
            //
            // So on "set alpha" both patterns lose their last element equally and tie, and this
            // is the shape where asking replaces a coin flip with an answer.
            LogAssert.Expect(LogType.Warning, new Regex("differ only at element 3"));
            _recogniser.DisambiguateSiblingTies = true;
            _recogniser.Configure(
                new[] { new VoxrSlotDefinition("ship", new[] { "alpha" }) },
                new[]
                {
                    new VoxrCommandDefinition(
                        "set_mode",
                        new[] { new[] { "set", "{ship}", "mode" } }
                    ),
                    new VoxrCommandDefinition(
                        "set_level",
                        new[] { new[] { "set", "{ship}", "level" } }
                    ),
                }
            );
            _recogniser.BufferWindow = 1.5f;
            _recogniser.CommandCooldown = 0f;

            VoxrPendingAmbiguity? seen = null;
            _recogniser.OnCommandPending += _ => seen = _recogniser.PendingAmbiguity;
            VoxrCommand? received = null;
            _recogniser.OnCommandRecognised += cmd => received = cmd;

            _recogniser.InjectText("set alpha");
            _recogniser.FlushPendingBuffer();

            Assert.IsTrue(seen.HasValue, "a trailing discriminator is asked about too");
            CollectionAssert.AreEqual(new[] { "mode", "level" }, seen.Value.DiscriminatingValues);

            Answer("level");

            Assert.IsTrue(received.HasValue);
            Assert.AreEqual("set_level", received.Value.Intent);
            Assert.AreEqual("alpha", received.Value.GetSlot("ship"));
        }

        [Test]
        public void Disambiguation_GeneratedGrammarJson_IsIdenticalAcrossTheFlag()
        {
            // F7, pinned rather than reasoned. The claim is that discriminating values need no
            // grammar addition because they are already pattern literals — but that is a claim
            // about the DECODER's vocabulary, and GetFollowUpGrammarWords is not flag-aware. If
            // the reasoning is wrong the flag changes what VOSK can hear in BOTH configurations,
            // which would break F2 in the one way the 699-corpus A/B cannot localise.
            LogAssert.Expect(LogType.Warning, new Regex("differ only at element 3"));
            ConfigureAsking(flag: false);
            string withFlagOff = _recogniser.TestGrammarJson;

            LogAssert.Expect(LogType.Warning, new Regex("differ only at element 3"));
            ConfigureAsking(flag: true);
            string withFlagOn = _recogniser.TestGrammarJson;

            Assert.AreEqual(
                withFlagOff,
                withFlagOn,
                "enabling disambiguation must not change one byte of what the decoder is told"
            );
            StringAssert.Contains("mode", withFlagOff, "…because the values are already in it");
            StringAssert.Contains("level", withFlagOff);
        }

        [Test]
        public void Disambiguation_UtteranceContainingAChoiceWord_IsNotReadAsTheAnswer()
        {
            // F7. The choice check reuses the whole-utterance matcher, so it is not a substring
            // search: "set alpha mode on" is a full re-utterance and takes the parse path, not
            // the choice path. Both routes fire set_mode here — what this pins is that the
            // utterance is not silently truncated to its "mode" token.
            LogAssert.Expect(LogType.Warning, new Regex("differ only at element 3"));
            ConfigureAsking();

            VoxrCommand? received = null;
            _recogniser.OnCommandRecognised += cmd => received = cmd;

            _recogniser.InjectText("set alpha on");
            _recogniser.FlushPendingBuffer();
            _recogniser.InjectText("set alpha mode on");
            _recogniser.FlushPendingBuffer();

            Assert.IsTrue(received.HasValue);
            Assert.AreEqual("set_mode", received.Value.Intent);
            Assert.AreEqual(
                "set alpha mode on",
                received.Value.RawText,
                "the whole utterance was parsed, not read as the bare choice \"mode\""
            );
        }

        // -------- Duplicate intent created by activating two sets (issue #120) --------

        [Test]
        public void DuplicateIntent_SameCommandInTwoActiveSets_IsReportedAtRebuild()
        {
            // The route no parser-level test can reach, because the duplication is in neither
            // set — activating both is what creates it. Nothing stops one command definition
            // (one VoxrCommandAsset, under Inspector authoring) from sitting in two sets, and
            // CommandSetManager.Activate concatenates the active sets without de-duplicating, so
            // the parser is constructed from a list carrying that definition twice.
            //
            // Interchangeable copies, so it takes that report and not the divergence one: there
            // is a single definition here, registered twice.
            var shared = new VoxrCommandDefinition(
                "cease_fire",
                new[] { new[] { "cease", "fire" } }
            );

            _recogniser.Configure(
                MakeSlots(),
                new[]
                {
                    new VoxrCommandSet("combat", new[] { shared }),
                    new VoxrCommandSet("navigation", new[] { shared }),
                }
            );

            // Queued here, not before Configure: Configure(slots, sets) only stores them and
            // nulls the parser — SetActiveSets is what constructs one, and so what warns.
            LogAssert.Expect(
                LogType.Warning,
                new Regex("Intent 'cease_fire' is registered 2 times by definitions no consumer")
            );

            _recogniser.SetActiveSets("combat", "navigation");

            Assert.AreEqual(2, _recogniser.ActiveSetNames.Length);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void DuplicateIntent_EachSetActiveAlone_SaysNothing()
        {
            // And the other half: the same two sets are each individually well-formed, so
            // activating one of them must be silent. If this ever warns, the scan is reporting
            // a grammar that has no duplicate in it at all.
            var shared = new VoxrCommandDefinition(
                "cease_fire",
                new[] { new[] { "cease", "fire" } }
            );

            _recogniser.Configure(
                MakeSlots(),
                new[]
                {
                    new VoxrCommandSet("combat", new[] { shared }),
                    new VoxrCommandSet("navigation", new[] { shared }),
                }
            );

            _recogniser.SetActiveSets("combat");

            LogAssert.NoUnexpectedReceived();
        }

    }
}
