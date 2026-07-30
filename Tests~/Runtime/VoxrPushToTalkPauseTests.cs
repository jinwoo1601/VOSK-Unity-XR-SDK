// ============================================================================
// Purpose:  PlayMode tests for the VoxrPushToTalkController.OnApplicationPause state machine
// Layer:    Tests.Runtime
// Owns:     VoxrPushToTalkPauseTests (public class), FakeSpeechRecogniser (nested test double)
// Depends:  VoxrPushToTalkController, VoxrSpeechRecogniser, VoxrListeningMode
// ============================================================================
using System.Collections;
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
        // and stages recognition state. No override calls base, so nothing here
        // touches a model, the native bridge, or a microphone.
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

        // Continuous mode is how _wantRecognising gets set without a button press.
        // The mode setter starts recognition, so call counts are cleared afterwards
        // and every assertion below is absolute.
        void ArrangeContinuousAndRunning()
        {
            _controller.ListeningMode = VoxrListeningMode.Continuous;
            Assume.That(
                _fake.Running,
                Is.True,
                "Precondition: switching to continuous mode should have started recognition"
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
            _controller.SpeechRecogniser = null;

            Assert.DoesNotThrow(() =>
            {
                Pause();
                Resume();
            });
            Assert.AreEqual(0, _fake.StartCalls);
            Assert.AreEqual(0, _fake.StopCalls);
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
            // VoxrPushToTalkController.cs:144 is the only thing preventing a second
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

            // Let Update() run the reconciliation (VoxrPushToTalkController.cs:149-155).
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
