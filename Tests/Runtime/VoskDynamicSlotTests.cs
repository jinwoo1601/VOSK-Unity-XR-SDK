using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using VoskXR.Commands;

namespace VoskXR.Tests.Runtime
{
    public class VoskDynamicSlotTests
    {
        GameObject _go;
        VoskCommandRecogniser _recogniser;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("TestDynamicSlots");
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
                        { "a one", "alpha one" },
                    }),
                new VoskSlotDefinition("weapon",
                    new[] { "missiles", "torpedoes" }),
            };
        }

        static VoskCommandDefinition[] MakeCommands()
        {
            return new[]
            {
                new VoskCommandDefinition("launch_weapon", new[]
                {
                    new[] { "launch", "{weapon}", "target", "{target}" },
                }),
                new VoskCommandDefinition("cease_fire", new[]
                {
                    new[] { "cease", "fire" },
                }),
            };
        }

        void ConfigureSync()
        {
            _recogniser.Configure(MakeSlots(), MakeCommands());
            _recogniser.BufferWindow = 0f;
            _recogniser.CommandCooldown = 0f;
        }

        // -------- Registration API --------

        [Test]
        public void RegisterSlotValueProvider_NullSlotName_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                _recogniser.RegisterSlotValueProvider(null, () => new[] { "a" }));
        }

        [Test]
        public void RegisterSlotValueProvider_NullProvider_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                _recogniser.RegisterSlotValueProvider("target", null));
        }

        [Test]
        public void UnregisterSlotValueProvider_NullSlotName_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                _recogniser.UnregisterSlotValueProvider(null));
        }

        [Test]
        public void UnregisterSlotValueProvider_NotRegistered_ReturnsFalse()
        {
            Assert.IsFalse(_recogniser.UnregisterSlotValueProvider("target"));
        }

        [Test]
        public void RegisterSlotValueProvider_Overwrite_AcceptsNewProvider()
        {
            ConfigureSync();

            _recogniser.RegisterSlotValueProvider("target", () => new[] { "hotel one" });
            _recogniser.RegisterSlotValueProvider("target", () => new[] { "hotel two" });
            _recogniser.NotifySlotChanged();

            VoskCommand? received = null;
            _recogniser.OnCommandRecognised += cmd => received = cmd;

            _recogniser.InjectText("launch missiles target hotel two");
            Assert.IsTrue(received.HasValue);
            Assert.AreEqual("hotel two", received.Value.GetSlot("target"));

            received = null;
            _recogniser.InjectText("launch missiles target hotel one");
            Assert.IsFalse(received.HasValue, "Overwritten provider should exclude hotel one");
        }

        // -------- Parser narrowing --------

        [Test]
        public void Provider_ReturnsSubset_ExcludedValuesStopMatching()
        {
            ConfigureSync();

            _recogniser.RegisterSlotValueProvider("target", () => new[] { "hotel one" });
            _recogniser.NotifySlotChanged();

            string unrecognised = null;
            _recogniser.OnUnrecognisedSpeech += text => unrecognised = text;

            _recogniser.InjectText("launch missiles target hotel two");

            Assert.IsNotNull(unrecognised,
                "Excluded value 'hotel two' should not match after provider narrowing");
        }

        [Test]
        public void Provider_ReturnsSubset_ActiveValuesStillMatch()
        {
            ConfigureSync();

            _recogniser.RegisterSlotValueProvider("target", () => new[] { "hotel one" });
            _recogniser.NotifySlotChanged();

            VoskCommand? received = null;
            _recogniser.OnCommandRecognised += cmd => received = cmd;

            _recogniser.InjectText("launch missiles target hotel one");

            Assert.IsTrue(received.HasValue);
            Assert.AreEqual("hotel one", received.Value.GetSlot("target"));
        }

        [Test]
        public void Provider_ReturnsNull_UsesStaticValues()
        {
            ConfigureSync();

            _recogniser.RegisterSlotValueProvider("target", () => null);
            _recogniser.NotifySlotChanged();

            VoskCommand? received = null;
            _recogniser.OnCommandRecognised += cmd => received = cmd;

            // All original values should still work
            _recogniser.InjectText("launch missiles target hotel two");

            Assert.IsTrue(received.HasValue);
            Assert.AreEqual("hotel two", received.Value.GetSlot("target"));
        }

        [Test]
        public void Provider_ReturnsEmpty_NothingMatches()
        {
            ConfigureSync();

            _recogniser.RegisterSlotValueProvider("target", () => Array.Empty<string>());
            _recogniser.NotifySlotChanged();

            string unrecognised = null;
            _recogniser.OnUnrecognisedSpeech += text => unrecognised = text;

            _recogniser.InjectText("launch missiles target hotel one");

            Assert.IsNotNull(unrecognised,
                "Empty provider should cause all target values to fail matching");
        }

        [Test]
        public void Provider_FiltersAliases_OnlyActiveCanonicalTargets()
        {
            ConfigureSync();

            // Only allow "hotel one" — aliases pointing to "hotel two" or "alpha one" should be pruned
            _recogniser.RegisterSlotValueProvider("target", () => new[] { "hotel one" });
            _recogniser.NotifySlotChanged();

            VoskCommand? received = null;
            _recogniser.OnCommandRecognised += cmd => received = cmd;

            // Alias "h one" -> "hotel one" should still work
            _recogniser.InjectText("launch missiles target h one");
            Assert.IsTrue(received.HasValue, "Alias 'h one' -> 'hotel one' should still match");
            Assert.AreEqual("hotel one", received.Value.GetSlot("target"));

            // Alias "h two" -> "hotel two" should be pruned
            received = null;
            string unrecognised = null;
            _recogniser.OnUnrecognisedSpeech += text => unrecognised = text;

            _recogniser.InjectText("launch missiles target h two");
            Assert.IsFalse(received.HasValue,
                "Alias 'h two' -> excluded 'hotel two' should not match");
        }

        // -------- Buffer preservation --------

        [Test]
        public void NotifySlotChanged_DoesNotClearBuffer()
        {
            _recogniser.Configure(MakeSlots(), MakeCommands());
            _recogniser.BufferWindow = 5f;
            _recogniser.CommandCooldown = 0f;

            VoskCommand? received = null;
            _recogniser.OnCommandRecognised += cmd => received = cmd;

            _recogniser.InjectText("cease fire");
            Assert.IsFalse(received.HasValue, "Buffer should hold the command");

            // Narrowing should not flush or discard the buffer
            _recogniser.RegisterSlotValueProvider("target", () => new[] { "hotel one" });
            _recogniser.NotifySlotChanged();

            _recogniser.FlushPendingBuffer();
            Assert.IsTrue(received.HasValue,
                "NotifySlotChanged must not clear the utterance buffer");
            Assert.AreEqual("cease_fire", received.Value.Intent);
        }

        // -------- Grammar independence --------

        [Test]
        public void RebuildParser_DoesNotChangeGrammar()
        {
            ConfigureSync();

            // Capture grammar before
            _recogniser.RegisterSlotValueProvider("target", () => new[] { "hotel one" });

            // Use reflection to read _grammarJson
            var field = typeof(VoskCommandRecogniser)
                .GetField("_grammarJson", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            string grammarBefore = (string)field.GetValue(_recogniser);

            _recogniser.NotifySlotChanged();

            string grammarAfter = (string)field.GetValue(_recogniser);
            Assert.AreEqual(grammarBefore, grammarAfter,
                "RebuildParser (via NotifySlotChanged) must not change grammar");
        }

        // -------- Integration --------

        [Test]
        public void Provider_UpdatedBetweenInjects_ReflectsLatest()
        {
            ConfigureSync();

            string[] activeTargets = { "hotel one" };
            _recogniser.RegisterSlotValueProvider("target", () => activeTargets);
            _recogniser.NotifySlotChanged();

            VoskCommand? received = null;
            _recogniser.OnCommandRecognised += cmd => received = cmd;

            // "hotel one" should match
            _recogniser.InjectText("launch missiles target hotel one");
            Assert.IsTrue(received.HasValue);

            // Switch provider to "hotel two"
            received = null;
            activeTargets = new[] { "hotel two" };
            _recogniser.NotifySlotChanged();

            string unrecognised = null;
            _recogniser.OnUnrecognisedSpeech += text => unrecognised = text;

            // "hotel one" should no longer match
            _recogniser.InjectText("launch missiles target hotel one");
            Assert.IsFalse(received.HasValue, "hotel one should be excluded after provider update");

            // "hotel two" should now match
            unrecognised = null;
            _recogniser.InjectText("launch missiles target hotel two");
            Assert.IsTrue(received.HasValue, "hotel two should match after provider update");
            Assert.AreEqual("hotel two", received.Value.GetSlot("target"));
        }

        [Test]
        public void Register_WithoutNotify_DoesNotAffectParser()
        {
            ConfigureSync();

            VoskCommand? received = null;
            _recogniser.OnCommandRecognised += cmd => received = cmd;

            // Register provider that excludes "hotel two", but don't call NotifySlotChanged
            _recogniser.RegisterSlotValueProvider("target", () => new[] { "hotel one" });

            // "hotel two" should still match because parser hasn't been rebuilt
            _recogniser.InjectText("launch missiles target hotel two");
            Assert.IsTrue(received.HasValue,
                "Without NotifySlotChanged, excluded value should still match old parser");
            Assert.AreEqual("hotel two", received.Value.GetSlot("target"));
        }

        // -------- Error paths --------

        [Test]
        public void RebuildParser_BeforeConfigure_Throws()
        {
            Assert.Throws<InvalidOperationException>(() =>
                _recogniser.RebuildParser());
        }

        [Test]
        public void RebuildGrammar_BeforeConfigure_Throws()
        {
            Assert.Throws<InvalidOperationException>(() =>
                _recogniser.RebuildGrammar());
        }

        [Test]
        public void NotifySlotChanged_BeforeConfigure_NoOp()
        {
            Assert.DoesNotThrow(() => _recogniser.NotifySlotChanged());
        }
    }
}
