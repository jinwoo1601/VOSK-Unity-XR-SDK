using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VoskXR;
using VoskXR.Commands;

namespace VoskXR.Tests.Runtime
{
    public class VoskCommandRecogniserInjectionTests
    {
        GameObject _go;
        VoskCommandRecogniser _recogniser;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("TestCommandRecogniser");
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
                    new[] { "hotel one", "hotel two", "alpha one" }),
                new VoskSlotDefinition("weapon",
                    new[] { "missiles", "torpedoes" }),
                new VoskSlotDefinition("quantity",
                    new[] { "all", "one", "two" }),
            };
        }

        static VoskCommandDefinition[] MakeCommands()
        {
            return new[]
            {
                new VoskCommandDefinition("launch_weapon", new[]
                {
                    new[] { "launch", "{?quantity}", "{weapon}", "target", "{target}" },
                }),
                new VoskCommandDefinition("cease_fire", new[]
                {
                    new[] { "cease", "fire" },
                }),
            };
        }

        void ConfigureWithSyncDefaults()
        {
            _recogniser.Configure(MakeSlots(), MakeCommands());
            // Disable buffer and cooldown so threshold tests can assert events synchronously.
            SetPrivateField(_recogniser, "bufferWindow", 0f);
            SetPrivateField(_recogniser, "commandCooldown", 0f);
        }

        static void SetPrivateField<T>(T target, string field, object value) where T : class
        {
            var fi = typeof(T).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance);
            if (fi == null)
                throw new ArgumentException($"Field '{field}' not found on {typeof(T).Name}");
            fi.SetValue(target, value);
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
            VoskCommand? singleEvent = null;
            VoskCommand[] batchEvent = null;
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
            VoskCommand? received = null;
            _recogniser.OnCommandRecognised += cmd => received = cmd;

            var words = VoskSpeechRecogniser.CreateSimulatedWords("cease fire", 0.85f);
            _recogniser.InjectText("cease fire", words);

            Assert.IsTrue(received.HasValue);
            Assert.AreEqual(0.85f, received.Value.Confidence, 1e-5f,
                "Confidence from injected words did not propagate to VoskCommand");
        }

        // -------- Threshold filtering --------

        [Test]
        public void InjectText_BelowMinConfidence_Rejected()
        {
            ConfigureWithSyncDefaults();
            // minConfidence default is 0.4
            int recognised = 0;
            int unrecognised = 0;
            _recogniser.OnCommandRecognised += _ => recognised++;
            _recogniser.OnUnrecognisedSpeech += _ => unrecognised++;

            var words = VoskSpeechRecogniser.CreateSimulatedWords("cease fire", 0.2f);
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

            var words = VoskSpeechRecogniser.CreateSimulatedWords("cease fire", 0.5f);
            _recogniser.InjectText("cease fire", words);

            Assert.AreEqual(1, recognised);
        }

        // -------- Cooldown --------

        [Test]
        public void InjectText_RespectsCommandCooldown()
        {
            _recogniser.Configure(MakeSlots(), MakeCommands());
            SetPrivateField(_recogniser, "bufferWindow", 0f);
            SetPrivateField(_recogniser, "commandCooldown", 1.0f);

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
            SetPrivateField(_recogniser, "bufferWindow", 1.5f);
            SetPrivateField(_recogniser, "commandCooldown", 0f);

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
            SetPrivateField(_recogniser, "bufferWindow", 1.5f);
            SetPrivateField(_recogniser, "commandCooldown", 0f);

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
            var speech = _go.AddComponent<VoskSpeechRecogniser>();
            SetPrivateField(_recogniser, "speechRecogniser", speech);

            // Force OnEnable to re-run with the now-set speechRecogniser reference.
            _recogniser.enabled = false;
            _recogniser.enabled = true;

            ConfigureWithSyncDefaults();

            VoskCommand? received = null;
            _recogniser.OnCommandRecognised += cmd => received = cmd;

            speech.InjectResult("cease fire");

            Assert.IsTrue(received.HasValue,
                "Speech-layer InjectResult did not propagate to command recogniser");
            Assert.AreEqual("cease_fire", received.Value.Intent);
        }
    }
}
