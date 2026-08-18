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

        // Only the authored-Continuous tests build this one; see AuthorContinuousInactive.
        GameObject _authoredGo;

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
            if (_authoredGo != null)
                Object.DestroyImmediate(_authoredGo);
            _authoredGo = null;
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

        // -------- Authored Continuous (issue #109) --------

        // Authoring Continuous in the Inspector is not the same as assigning ListeningMode at
        // runtime, and the difference is the whole bug: the field is already Continuous when
        // OnEnable first runs, so no press and no mode change ever announce the start. The
        // property setter every other test here uses cannot reproduce that, because it *is* a
        // change. Building the object inactive is what holds Awake/OnEnable back until the
        // mode and the references are in place; the caller activates it to fire them.
        //
        // componentEnabled mirrors the component's own Inspector checkbox, which serializes
        // independently of the GameObject. It is a distinct axis from SetActive: Unity runs
        // Awake on a disabled component of an active GameObject but never OnEnable, so the
        // two states diverge — see AuthoredContinuous_ComponentDisabled_* below.
        VoxrPushToTalkController AuthorContinuousInactive(bool componentEnabled = true)
        {
            _authoredGo = new GameObject("AuthoredContinuousPTT");
            _authoredGo.SetActive(false);

            var speech = _authoredGo.AddComponent<VoxrSpeechRecogniser>();
            var controller = _authoredGo.AddComponent<VoxrPushToTalkController>();

            controller.SpeechRecogniser = speech;
            controller.InitialiseOnStart = false;
            controller.InitialMode = VoxrListeningMode.Continuous;
            controller.enabled = componentEnabled;

            return controller;
        }

        [Test]
        public void AuthoredContinuous_FirstEnable_FiresOnTalkStarted()
        {
            var controller = AuthorContinuousInactive();
            int started = 0;
            int ended = 0;
            controller.OnTalkStarted.AddListener(() => started++);
            controller.OnTalkEnded.AddListener(() => ended++);

            _authoredGo.SetActive(true);

            Assert.AreEqual(
                1,
                started,
                "A scene authored on Continuous starts recognition on enable, so it must "
                    + "announce it exactly once — an indicator wired only to OnTalkStarted is "
                    + "otherwise dark while the mic is live"
            );
            Assert.AreEqual(0, ended);
        }

        [Test]
        public void AuthoredContinuous_DisableEnableCycle_DoesNotRefireOnTalkStarted()
        {
            var controller = AuthorContinuousInactive();
            int started = 0;
            controller.OnTalkStarted.AddListener(() => started++);

            _authoredGo.SetActive(true);
            Assume.That(started, Is.EqualTo(1), "Precondition: the authored start announced once");

            controller.enabled = false;
            controller.enabled = true;

            Assert.AreEqual(
                1,
                started,
                "The startup announcement is a one-off: a lifecycle resume must stay silent, "
                    + "exactly as it does for a press or a runtime switch to Continuous"
            );
        }

        [Test]
        public void AuthoredContinuous_ThenSwitchToPushToTalk_FiresOnTalkEndedOnce()
        {
            var controller = AuthorContinuousInactive();
            int ended = 0;
            controller.OnTalkEnded.AddListener(() => ended++);

            _authoredGo.SetActive(true);
            controller.ListeningMode = VoxrListeningMode.PushToTalk;

            Assert.AreEqual(
                1,
                ended,
                "Proves the authored enable recorded the want-to-recognise flag, not just "
                    + "the event — the switch away only stops and fires when that flag is set"
            );
        }

        // The two tests below pin the one state where this change is not purely additive.
        // Before it, Awake recorded the want-to-recognise flag for ANY authored-Continuous
        // component, enabled or not, because Unity runs Awake on a disabled component of an
        // active GameObject. The flag now rises in OnEnable, which such a component never
        // reaches — so it stays down until the checkbox is ticked. That was chosen, not
        // overlooked: the flag's only pre-enable reader is the ListeningMode setter, and what
        // it did with it was fire an OnTalkEnded that no OnTalkStarted ever paired with, on a
        // recogniser that had never been started.

        [Test]
        public void AuthoredContinuous_ComponentDisabled_AnnouncesOnlyWhenEnabled()
        {
            var controller = AuthorContinuousInactive(componentEnabled: false);
            int started = 0;
            int ended = 0;
            controller.OnTalkStarted.AddListener(() => started++);
            controller.OnTalkEnded.AddListener(() => ended++);

            _authoredGo.SetActive(true);

            Assert.AreEqual(
                0,
                started,
                "A disabled component starts no recognition, so it must announce nothing — "
                    + "activating the GameObject does not reach OnEnable"
            );

            controller.enabled = true;

            Assert.AreEqual(
                1,
                started,
                "Ticking the checkbox is where recognition actually begins, so that is where "
                    + "the announcement belongs — deferred, not lost"
            );
            Assert.AreEqual(0, ended);
        }

        [Test]
        public void AuthoredContinuous_ComponentDisabled_SwitchToPushToTalkIsSilent()
        {
            var controller = AuthorContinuousInactive(componentEnabled: false);
            int ended = 0;
            controller.OnTalkEnded.AddListener(() => ended++);

            _authoredGo.SetActive(true);
            controller.ListeningMode = VoxrListeningMode.PushToTalk;

            Assert.AreEqual(
                0,
                ended,
                "Switching away before the component was ever enabled must be silent: nothing "
                    + "had started, and nothing had announced a start for this to pair with"
            );
        }

        // -------- Missing or destroyed recogniser (issue #115) --------

        // Unity overloads ==/!= on UnityEngine.Object so a destroyed component compares equal
        // to null while its managed wrapper is still alive. `?.` does not call that overload,
        // so the two tests below are the ones the old `?.` in the setter failed: they destroy
        // the component but keep the C# reference the controller holds.
        //
        // What pins the skip is the event count, NOT the DoesNotThrow. Dispatching into a
        // destroyed recogniser never reached this caller: StartRecognition hands off to a
        // discarded async Task, and StopRecognitionCore touches no Unity-side member on any
        // platform. The DoesNotThrow is kept only as a cheap guard against a future edit that
        // does make the path throw, and its message says exactly that much — the sibling
        // fixture (VoxrPushToTalkPauseTests) warns about assertions that cannot see the throw
        // they exist to rule out. Pinning non-dispatch directly would need a call-counting
        // double over `internal virtual StartRecognitionCore`, as that fixture uses.

        [Test]
        public void ListeningMode_SetToContinuous_RecogniserDestroyed_IsNoOp()
        {
            Object.DestroyImmediate(_speech);
            Assume.That(_speech == null, Is.True, "Precondition: the recogniser reads as null");

            Assert.DoesNotThrow(
                () => _controller.ListeningMode = VoxrListeningMode.Continuous,
                "Switching to Continuous must not surface an exception to the caller"
            );
            Assert.AreEqual(
                0,
                _startedCount,
                "Nothing started, so nothing may announce a start — the same pairing PressTalk "
                    + "and the authored-Continuous enable already keep"
            );
        }

        [Test]
        public void ListeningMode_SetToPushToTalk_RecogniserDestroyed_IsNoOp()
        {
            _controller.ListeningMode = VoxrListeningMode.Continuous;
            Assume.That(_startedCount, Is.EqualTo(1), "Precondition: the live start announced");

            Object.DestroyImmediate(_speech);

            Assert.DoesNotThrow(
                () => _controller.ListeningMode = VoxrListeningMode.PushToTalk,
                "Switching back must not surface an exception to the caller"
            );
            Assert.AreEqual(
                0,
                _endedCount,
                "Nothing stopped, so nothing may announce a stop — ReleaseTalk is already "
                    + "silent on a missing recogniser for the same reason"
            );
        }

        [Test]
        public void ListeningMode_SetToContinuous_RecogniserNull_IsNoOp()
        {
            _controller.SpeechRecogniser = null;

            Assert.DoesNotThrow(() => _controller.ListeningMode = VoxrListeningMode.Continuous);
            Assert.AreEqual(0, _startedCount);
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
