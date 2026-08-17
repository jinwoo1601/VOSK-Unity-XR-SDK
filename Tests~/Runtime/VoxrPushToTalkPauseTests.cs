// ============================================================================
// Purpose:  PlayMode tests for the VoxrPushToTalkController.OnApplicationPause state machine
// Layer:    Tests.Runtime
// Owns:     VoxrPushToTalkPauseTests (public class), FakeSpeechRecogniser (nested test double)
// Depends:  VoxrPushToTalkController, VoxrSpeechRecogniser, VoxrListeningMode
// ============================================================================
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VoXR.Tests.Runtime
{
    public class VoxrPushToTalkPauseTests
    {
        // Subclasses the real component through the internal seam
        // (VoxrSpeechRecogniser.IsRecognisingCore/StartRecognitionCore/StopRecognitionCore)
        // so the controller cannot tell the difference, while the test counts calls
        // and stages recognition state.
        //
        // Overriding the cores is not on its own what keeps this fixture off the model,
        // the native bridge and the microphone. Inertness rests on all three of:
        //   - no override calling base, so nothing the seam covers reaches native code;
        //   - the empty OnDestroy() below, because a base class's privately-declared
        //     Unity messages still run on a subclass and the real one releases native
        //     resources (benign only under UNITY_EDITOR_WIN, a live
        //     vosk_bridge_destroy() P/Invoke off it). The base Update() needs no such
        //     shim — it returns early while the base's own _isRecognising stays false;
        //   - SetUp's InitialiseOnStart = false, since Initialise()/InitialiseAsync()/
        //     StartRecognitionAsync() are public non-virtual and the seam misses them.
        sealed class FakeSpeechRecogniser : VoxrSpeechRecogniser
        {
            internal int StartCalls { get; private set; }
            internal int StopCalls { get; private set; }

            // What IsRecognising reports. Settable so a test can stage a session the
            // controller did not start itself.
            internal bool Running { get; set; }

            internal override bool IsRecognisingCore => Running;

            internal override void StartRecognitionCore()
            {
                StartCalls++;
                Running = true;
            }

            internal override void StopRecognitionCore()
            {
                StopCalls++;
                Running = false;
            }

            internal void ResetCalls()
            {
                StartCalls = 0;
                StopCalls = 0;
            }

            // Keeps the base's private OnDestroy — and its ReleaseNativeResources() call
            // (VoxrSpeechRecogniser.cs:515) — off this double. No `new` keyword: the base
            // declaration is private, so nothing is being hidden as far as C# is concerned.
            void OnDestroy() { }
        }

        GameObject _recogniserGo;
        GameObject _controllerGo;
        FakeSpeechRecogniser _fake;
        VoxrPushToTalkController _controller;

        int _startedCount;
        int _endedCount;

        [SetUp]
        public void SetUp()
        {
            _recogniserGo = new GameObject("PauseTestFakeRecogniser");
            _fake = _recogniserGo.AddComponent<FakeSpeechRecogniser>();

            // The controller lives on its own GameObject so SendMessage("OnApplicationPause")
            // reaches the state machine under test and nothing else.
            _controllerGo = new GameObject("PauseTestController");
            _controller = _controllerGo.AddComponent<VoxrPushToTalkController>();
            _controller.SpeechRecogniser = _fake;
            // Load-bearing, not tidiness: Initialise() is public and non-virtual, so the
            // seam does not cover it. Without this, Start() loads the real model.
            _controller.InitialiseOnStart = false;

            _startedCount = 0;
            _endedCount = 0;
            _controller.OnTalkStarted.AddListener(() => _startedCount++);
            _controller.OnTalkEnded.AddListener(() => _endedCount++);
        }

        [TearDown]
        public void TearDown()
        {
            if (_controllerGo != null)
                Object.DestroyImmediate(_controllerGo);
            if (_recogniserGo != null)
                Object.DestroyImmediate(_recogniserGo);
        }

        // -------- Drivers --------

        // OnApplicationPause is private, so drive it the way the OS does.
        void Pause() => _controllerGo.SendMessage("OnApplicationPause", true);

        void Resume() => _controllerGo.SendMessage("OnApplicationPause", false);

        // Same message, called directly. Only the null-guard test needs this: SendMessage
        // may log a receiver exception rather than propagate it, which would leave
        // Assert.DoesNotThrow unable to see the very throw it exists to rule out.
        // Reflection's TargetInvocationException wrapper still fails DoesNotThrow.
        void PauseDirect(bool paused) =>
            typeof(VoxrPushToTalkController)
                .GetMethod("OnApplicationPause", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(_controller, new object[] { paused });

        // Continuous mode is how _wantRecognising gets set without a button press.
        // The mode setter starts recognition, so call counts are cleared afterwards
        // and every assertion below is absolute.
        //
        // Assert rather than Assume, unlike the other preconditions in this file: this is
        // the only check anywhere in Tests~ that continuous mode starts recognition at all.
        // VoxrPushToTalkController.cs:68 is a separate statement from the :69 event that
        // the sibling ListeningMode_SetToContinuous_FiresOnTalkStarted covers, so deleting
        // :68 is invisible everywhere else. An unsatisfied Assume reports Inconclusive,
        // which this project's failed="0" green criterion would report as a pass.
        void ArrangeContinuousAndRunning()
        {
            _controller.ListeningMode = VoxrListeningMode.Continuous;
            Assert.That(
                _fake.Running,
                Is.True,
                "Switching to continuous mode must start recognition"
            );
            _fake.ResetCalls();
        }

        // -------- Pause --------

        [Test]
        public void Pause_WhileRecognisingInContinuousMode_StopsRecognition()
        {
            ArrangeContinuousAndRunning();

            Pause();

            Assert.AreEqual(
                1,
                _fake.StopCalls,
                "Pause while recognising must stop recognition exactly once"
            );
            Assert.IsFalse(_fake.Running);
            Assert.AreEqual(0, _fake.StartCalls);
            Assert.AreEqual(
                0,
                _endedCount,
                "Pause is a lifecycle event — OnTalkEnded must not fire"
            );
        }

        [Test]
        public void Pause_WhilePushToTalkIdle_DoesNotStopAndPreservesState()
        {
            Assume.That(_controller.ListeningMode, Is.EqualTo(VoxrListeningMode.PushToTalk));
            Assume.That(
                _fake.Running,
                Is.False,
                "Precondition: an untouched push-to-talk controller must not be recognising"
            );

            Pause();

            Assert.AreEqual(0, _fake.StopCalls, "Pause must not stop what was never started");
            Assert.AreEqual(0, _fake.StartCalls);

            Resume();

            Assert.AreEqual(
                0,
                _fake.StartCalls,
                "Resume must not start recognition the user never asked for"
            );

            // _wantRecognising is private; a press that still takes effect proves the
            // pause/resume pair left it false rather than corrupting it.
            _controller.PressTalk();

            Assert.AreEqual(
                1,
                _fake.StartCalls,
                "PressTalk after an idle pause/resume cycle must still start recognition"
            );
            Assert.AreEqual(1, _startedCount);
        }

        [Test]
        public void Pause_WithNullRecogniser_IsNoOp()
        {
            // The guard at VoxrPushToTalkController.cs:158 only carries weight when the
            // user still wants recognition: both branches of the state machine are gated
            // on _wantRecognising (:162, :167), so from the PushToTalk-idle default this
            // test would pass with the whole method body deleted. Arrange the wanting
            // state first, then drop the reference — now deleting :158 dereferences null
            // on the pause branch and again on the resume branch.
            ArrangeContinuousAndRunning();
            _controller.SpeechRecogniser = null;

            Assert.DoesNotThrow(() =>
            {
                PauseDirect(true);
                PauseDirect(false);
            });
        }

        // -------- Resume --------

        [Test]
        public void Resume_WithWantRecognisingSet_RestartsRecognition()
        {
            ArrangeContinuousAndRunning();
            Pause();
            Assume.That(
                _fake.Running,
                Is.False,
                "Precondition: pause should have stopped recognition"
            );
            _fake.ResetCalls();

            Resume();

            Assert.AreEqual(
                1,
                _fake.StartCalls,
                "Resume must restart recognition when the user still wants it"
            );
            Assert.IsTrue(_fake.Running);
            Assert.AreEqual(
                1,
                _startedCount,
                "Resume is a lifecycle event — OnTalkStarted must not re-fire "
                    + "(the 1 is the continuous-mode switch)"
            );
        }

        [Test]
        public void Resume_WhenAlreadyRecognising_DoesNotDoubleStart()
        {
            // A resume that arrives while the session is still up — the guard at
            // VoxrPushToTalkController.cs:167 is the only thing preventing a second
            // native start, and only a call count can prove it held.
            ArrangeContinuousAndRunning();

            Resume();

            Assert.AreEqual(
                0,
                _fake.StartCalls,
                "Resume must not start a second session over a running one"
            );
            Assert.IsTrue(_fake.Running);
            Assert.AreEqual(0, _fake.StopCalls);
        }

        [Test]
        public void PauseResume_WhilePushToTalkHeld_StopsThenRestarts()
        {
            _controller.PressTalk();
            Assume.That(
                _fake.Running,
                Is.True,
                "Precondition: PressTalk should have started recognition"
            );
            _fake.ResetCalls();

            Pause();

            Assert.AreEqual(
                1,
                _fake.StopCalls,
                "Pause while the talk button is held must stop capture"
            );
            Assert.IsFalse(_fake.Running);

            Resume();

            Assert.AreEqual(1, _fake.StartCalls, "Resume must restore the still-held talk session");
            Assert.IsTrue(_fake.Running);
            Assert.AreEqual(
                1,
                _startedCount,
                "A lifecycle pause/resume must not re-fire OnTalkStarted"
            );
            Assert.AreEqual(0, _endedCount, "A lifecycle pause/resume must not fire OnTalkEnded");
        }

        // -------- Interaction with the Update() permission-race reconciliation --------

        [UnityTest]
        public IEnumerator Resume_ThenUpdate_ReconcilerLeavesTheResumedSessionRunning()
        {
            ArrangeContinuousAndRunning();
            Pause();
            _fake.ResetCalls();

            Resume();
            Assume.That(
                _fake.StartCalls,
                Is.EqualTo(1),
                "Precondition: resume should have restarted"
            );

            // Let Update() run the reconciliation (VoxrPushToTalkController.cs:172-178).
            yield return null;
            yield return null;

            Assert.AreEqual(
                0,
                _fake.StopCalls,
                "The reconciler must not stop a session the resume just restarted"
            );
            Assert.IsTrue(_fake.Running);
            Assert.AreEqual(
                1,
                _fake.StartCalls,
                "The reconciler must not start a second session either"
            );
        }

        [UnityTest]
        public IEnumerator PauseResume_WhileIdleWithRecogniserRunning_ReconcilerStopsItNextFrame()
        {
            // Stages the Android mic-permission race the reconciler exists for: the
            // permission coroutine fired a native start after the user already
            // released, so the recogniser is running while _wantRecognising is false.
            _fake.Running = true;
            _fake.ResetCalls();

            Pause();

            Assert.AreEqual(
                0,
                _fake.StopCalls,
                "Pause keys off _wantRecognising, not the recogniser's own state"
            );

            Resume();

            Assert.AreEqual(
                0,
                _fake.StartCalls,
                "Resume must not adopt a session the user never asked for"
            );

            yield return null;

            Assert.AreEqual(
                1,
                _fake.StopCalls,
                "The reconciler must still stop the orphaned session on the next frame"
            );
            Assert.IsFalse(_fake.Running);
        }
    }
}
