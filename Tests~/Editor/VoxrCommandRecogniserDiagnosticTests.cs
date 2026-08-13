using System.Collections.Generic;
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
        }

        // 4.3
        [Test]
        public void ScoreRejection_ReasonFormat()
        {
            ConfigureSync();
            // "launch missiles" partially matches the 5-element pattern: "target" is missed
            // and {target} is an unfilled required slot, giving (1 + 1 + 0 - 1) / 4 = 0.25.
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
    }
}
