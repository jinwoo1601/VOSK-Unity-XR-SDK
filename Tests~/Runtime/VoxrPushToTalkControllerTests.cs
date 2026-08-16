using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VoXR;
using VoXR.Commands;

namespace VoXR.Tests.Runtime
{
    public class VoxrPushToTalkControllerTests
    {
        GameObject _go;
        VoxrPushToTalkController _controller;
        VoxrSpeechRecogniser _speech;
        VoxrCommandRecogniser _command;

        int _startedCount;
        int _endedCount;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("TestPTTController");
            _speech = _go.AddComponent<VoxrSpeechRecogniser>();
            _command = _go.AddComponent<VoxrCommandRecogniser>();
            _controller = _go.AddComponent<VoxrPushToTalkController>();

            _controller.SpeechRecogniser = _speech;
            _controller.CommandRecogniser = _command;
            _controller.InitialiseOnStart = false;
            _command.SpeechRecogniser = _speech;

            _startedCount = 0;
            _endedCount = 0;
            _controller.OnTalkStarted.AddListener(() => _startedCount++);
            _controller.OnTalkEnded.AddListener(() => _endedCount++);
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null)
                Object.DestroyImmediate(_go);
        }

        // -------- Fixtures --------

        static VoxrSlotDefinition[] MakeSlots() => new[]
        {
            new VoxrSlotDefinition("target", new[] { "hotel one", "alpha one" }),
            new VoxrSlotDefinition("weapon", new[] { "missiles", "torpedoes" }),
        };

        static VoxrCommandDefinition[] MakeCommands(bool allowPartial = false) => new[]
        {
            new VoxrCommandDefinition("launch_weapon",
                new[] { new[] { "launch", "{weapon}", "target", "{target}" } },
                allowPartial, false),
            new VoxrCommandDefinition("cease_fire",
                new[] { new[] { "cease", "fire" } }),
        };

        void ConfigureBuffered()
        {
            _command.Configure(MakeSlots(), MakeCommands());
            _command.BufferWindow = 1.5f;
            _command.CommandCooldown = 0f;
        }

        void ConfigureSync(bool allowPartial = false)
        {
            _command.Configure(MakeSlots(), MakeCommands(allowPartial));
            _command.BufferWindow = 0f;
            _command.CommandCooldown = 0f;
            _command.PendingTimeout = 30f;
        }

        [Test]
        public void AddComponent_DefaultState_ListeningModeIsPushToTalk()
        {
            Assert.AreEqual(VoxrListeningMode.PushToTalk, _controller.ListeningMode);
        }

        [Test]
        public void PressTalk_SpeechRecogniserNull_IsNoOp()
        {
            _controller.SpeechRecogniser = null;

            Assert.DoesNotThrow(() => _controller.PressTalk());
            Assert.AreEqual(0, _startedCount);
        }

        [Test]
        public void PressTalk_FirstPress_FiresOnTalkStarted()
        {
            _controller.PressTalk();
            Assert.AreEqual(1, _startedCount);
        }

        [Test]
        public void PressTalk_CalledTwice_FiresOnTalkStartedOnce()
        {
            _controller.PressTalk();
            _controller.PressTalk();
            Assert.AreEqual(1, _startedCount);
        }

        [Test]
        public void ReleaseTalk_WithoutPriorPress_IsNoOp()
        {
            _controller.ReleaseTalk();
            Assert.AreEqual(0, _endedCount);
        }

        [Test]
        public void ReleaseTalk_AfterPress_FiresOnTalkEnded()
        {
            _controller.PressTalk();
            _controller.ReleaseTalk();
            Assert.AreEqual(1, _endedCount);
        }

        [Test]
        public void ReleaseTalk_WithCommandRecogniser_CallsFlushPendingBuffer()
        {
            ConfigureBuffered();

            int recognised = 0;
            _command.OnCommandRecognised += _ => recognised++;

            _controller.PressTalk();
            _command.InjectText("cease fire");
            Assert.AreEqual(0, recognised,
                "Buffered injection must not fire before release");

            _controller.ReleaseTalk();
            Assert.AreEqual(1, recognised,
                "Release must flush the buffered command through the command recogniser");
        }

        [Test]
        public void ReleaseTalk_CancelPendingFalse_DoesNotCallCancelPendingCommand()
        {
            ConfigureSync(allowPartial: true);
            _controller.CancelPendingOnRelease = false;

            int cancelled = 0;
            _command.OnCommandCancelled += _ => cancelled++;

            _controller.PressTalk();
            _command.InjectText("launch missiles target");
            Assume.That(_command.HasPendingCommand, Is.True,
                "Precondition: command recogniser should be in pending state");

            _controller.ReleaseTalk();

            Assert.IsTrue(_command.HasPendingCommand,
                "Pending command should survive release when CancelPendingOnRelease is false");
            Assert.AreEqual(0, cancelled);
        }

        [Test]
        public void ReleaseTalk_CancelPendingTrue_CallsCancelPendingCommand()
        {
            ConfigureSync(allowPartial: true);
            _controller.CancelPendingOnRelease = true;

            int cancelled = 0;
            _command.OnCommandCancelled += _ => cancelled++;

            _controller.PressTalk();
            _command.InjectText("launch missiles target");
            Assume.That(_command.HasPendingCommand, Is.True,
                "Precondition: command recogniser should be in pending state");

            _controller.ReleaseTalk();

            Assert.IsFalse(_command.HasPendingCommand,
                "Pending command should be cancelled on release when the flag is true");
            Assert.AreEqual(1, cancelled);
        }

        [Test]
        public void ListeningMode_SetToContinuous_FiresOnTalkStarted()
        {
            _controller.ListeningMode = VoxrListeningMode.Continuous;

            Assert.AreEqual(VoxrListeningMode.Continuous, _controller.ListeningMode);
            Assert.AreEqual(1, _startedCount);
        }

        [Test]
        public void ListeningMode_SetToPushToTalk_WhenPressed_FiresOnTalkEnded()
        {
            _controller.ListeningMode = VoxrListeningMode.Continuous;
            Assume.That(_startedCount, Is.EqualTo(1));

            _controller.ListeningMode = VoxrListeningMode.PushToTalk;

            Assert.AreEqual(VoxrListeningMode.PushToTalk, _controller.ListeningMode);
            Assert.AreEqual(1, _endedCount);
        }

        [Test]
        public void PressTalk_InContinuousMode_IsNoOp()
        {
            _controller.ListeningMode = VoxrListeningMode.Continuous;
            int startedAfterMode = _startedCount;

            _controller.PressTalk();

            Assert.AreEqual(startedAfterMode, _startedCount,
                "PressTalk must not re-fire OnTalkStarted in continuous mode");
        }

        [Test]
        public void OnDisable_WhilePressed_DoesNotFireOnTalkEnded()
        {
            _controller.PressTalk();
            Assume.That(_startedCount, Is.EqualTo(1));

            _controller.enabled = false;

            Assert.AreEqual(0, _endedCount,
                "OnDisable is a lifecycle event — OnTalkEnded must not fire");
        }

        [Test]
        public void OnDisable_InContinuousMode_PreservesWantRecognisingFlag()
        {
            _controller.ListeningMode = VoxrListeningMode.Continuous;
            Assume.That(_startedCount, Is.EqualTo(1));

            _controller.enabled = false;
            _controller.enabled = true;

            Assert.AreEqual(1, _startedCount,
                "Re-enable must not re-fire OnTalkStarted (lifecycle resume is silent)");
            Assert.AreEqual(0, _endedCount);

            _controller.ListeningMode = VoxrListeningMode.PushToTalk;

            Assert.AreEqual(1, _endedCount,
                "Switching back to PushToTalk after a disable/enable cycle must fire " +
                "OnTalkEnded exactly once — proves _wantRecognising was preserved");
        }

        [Test]
        public void OnEnable_WhenWantRecognisingTrue_DoesNotFireOnTalkStarted()
        {
            _controller.ListeningMode = VoxrListeningMode.Continuous;
            int startedBefore = _startedCount;

            _controller.enabled = false;
            _controller.enabled = true;

            Assert.AreEqual(startedBefore, _startedCount);
        }

        [UnityTest]
        public IEnumerator OnDestroy_Cleanup_DoesNotThrow()
        {
            _controller.PressTalk();
            Object.DestroyImmediate(_go);
            _go = null;
            yield return null;
        }

        [Test]
        public void ReleaseTalk_SiblingTieDeferredByTheEagerGate_StillFires()
        {
            // Issue #74 DR-5 has the eager gate refuse on a sibling tie, so a buffer that used
            // to commit early now waits. Releasing the trigger is the one path where "waits"
            // could have meant "is discarded" — OnTalkEnded calls CancelPendingCommand as well
            // as FlushPendingBuffer, and only the order between them keeps the command alive.
            //
            // The discriminator is MEDIAL: a trailing one is refused by the issue #70 tail
            // condition instead, so it would not exercise the sibling refusal at all.
            LogAssert.Expect(
                LogType.Warning,
                new System.Text.RegularExpressions.Regex("differ only at element 3")
            );

            _command.Configure(
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
            _command.BufferWindow = 1.5f;
            _command.CommandCooldown = 0f;
            _command.EagerFlushOnCompleteMatch = true;

            VoxrCommand? received = null;
            _command.OnCommandRecognised += cmd => received = cmd;

            _controller.PressTalk();
            _command.InjectText("set alpha on");
            Assert.IsFalse(received.HasValue, "the tie must defer rather than commit early");

            _controller.ReleaseTalk();

            Assert.IsTrue(
                received.HasValue,
                "releasing must flush the deferred command, not discard it"
            );
            Assert.AreEqual("set_mode", received.Value.Intent);
        }
    }
}
