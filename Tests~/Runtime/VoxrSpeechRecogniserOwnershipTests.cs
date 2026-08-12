// ============================================================================
// Purpose:  PlayMode tests for single-owner enforcement of the process-global bridge
// Layer:    Tests.Runtime
// Owns:     VoxrSpeechRecogniserOwnershipTests (public class)
// Depends:  VoxrSpeechRecogniser, VoxrBridgeErrorCode
// ============================================================================
#if UNITY_EDITOR_WIN
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VoXR.Tests.Runtime
{
    // Issue #57: the native bridge is one per process, so a second component used
    // to inherit the first's configuration silently and free its recognizer from
    // its own OnDestroy. These tests pin the enforced contract — exactly one
    // owner, every other instance inert towards the bridge and loud about it.
    //
    // Editor-Windows only for the same reason as the playback-seam suite: the
    // enforcement itself is platform-independent, but establishing a real owner
    // needs a backend that actually initialises, which here is EditorMicBackend.
    public class VoxrSpeechRecogniserOwnershipTests
    {
        static readonly Regex DuplicateOwner = new Regex("already owns the native bridge");

        GameObject _ownerGo;
        GameObject _secondGo;
        VoxrSpeechRecogniser _owner;
        VoxrSpeechRecogniser _second;
        List<string> _ownerErrors;
        List<string> _secondErrors;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _ownerGo = new GameObject("OwnerRecogniser");
            _owner = _ownerGo.AddComponent<VoxrSpeechRecogniser>();
            _ownerErrors = new List<string>();
            _owner.OnError += (code, msg) => _ownerErrors.Add($"{code}: {msg}");

            var init = _owner.InitialiseAsync();
            while (!init.IsCompleted)
                yield return null;

            Assert.IsTrue(
                _owner.IsInitialised,
                "Owner recogniser failed to initialise — is the VOSK model available to "
                    + $"the host project? Errors: [{string.Join("; ", _ownerErrors)}]"
            );
            _ownerErrors.Clear();

            // Deliberately left uninitialised: the worst form of the bug needed no
            // initialisation at all, only an OnDestroy.
            _secondGo = new GameObject("SecondRecogniser");
            _second = _secondGo.AddComponent<VoxrSpeechRecogniser>();
            _secondErrors = new List<string>();
            _second.OnError += (code, msg) => _secondErrors.Add($"{code}: {msg}");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_secondGo != null)
                Object.Destroy(_secondGo);
            if (_ownerGo != null)
                Object.Destroy(_ownerGo);
            yield return null;
        }

        [UnityTest]
        public IEnumerator SecondInitialise_RejectedLoudly_OwnerKeepsBridge()
        {
            LogAssert.Expect(LogType.Error, DuplicateOwner);

            var init = _second.InitialiseAsync();
            while (!init.IsCompleted)
                yield return null;

            Assert.IsFalse(
                _second.IsInitialised,
                "A second recogniser must not report itself initialised off the owner's bridge."
            );
            Assert.IsTrue(_owner.IsInitialised, "Owner must still hold the bridge.");
            Assert.AreEqual(
                1,
                _secondErrors.Count,
                $"Expected exactly one rejection: [{string.Join("; ", _secondErrors)}]"
            );
            StringAssert.Contains(
                VoxrBridgeErrorCode.AlreadyInitialised.ToString(),
                _secondErrors[0]
            );
            StringAssert.Contains("OwnerRecogniser", _secondErrors[0]);
            Assert.IsEmpty(
                _ownerErrors,
                $"Owner must see no error: [{string.Join("; ", _ownerErrors)}]"
            );
        }

        [UnityTest]
        public IEnumerator SecondDestroyed_LeavesOwnerInitialised()
        {
            Object.DestroyImmediate(_secondGo);
            _secondGo = null;
            yield return null;

            Assert.IsTrue(
                _owner.IsInitialised,
                "Destroying a second recogniser must not free the owner's recognizer and model."
            );
        }

        [Test]
        public void SecondReleaseNativeResources_LeavesOwnerInitialised()
        {
            _second.ReleaseNativeResources();

            Assert.IsTrue(
                _owner.IsInitialised,
                "ReleaseNativeResources on a non-owner must not tear down the owner's bridge."
            );
        }

        [Test]
        public void SecondSetGrammar_RejectedLoudly()
        {
            LogAssert.Expect(LogType.Error, DuplicateOwner);

            _second.SetGrammar("{\"grammar\": [\"[unk]\"]}");

            Assert.AreEqual(
                1,
                _secondErrors.Count,
                $"Expected exactly one rejection: [{string.Join("; ", _secondErrors)}]"
            );
            Assert.IsTrue(_owner.IsInitialised, "Owner must still hold the bridge.");
        }

        [Test]
        public void SecondResetRecogniser_RejectedLoudly()
        {
            LogAssert.Expect(LogType.Error, DuplicateOwner);

            _second.ResetRecogniser();

            Assert.AreEqual(
                1,
                _secondErrors.Count,
                $"Expected exactly one rejection: [{string.Join("; ", _secondErrors)}]"
            );
            Assert.IsTrue(_owner.IsInitialised, "Owner must still hold the bridge.");
        }

        [UnityTest]
        public IEnumerator OwnerDestroyed_ReleasesClaimForNextRecogniser()
        {
            Object.DestroyImmediate(_ownerGo);
            _ownerGo = null;
            yield return null;

            var init = _second.InitialiseAsync();
            while (!init.IsCompleted)
                yield return null;

            Assert.IsTrue(
                _second.IsInitialised,
                "A destroyed owner must release its claim so the next recogniser can "
                    + $"initialise. Errors: [{string.Join("; ", _secondErrors)}]"
            );
        }

        [UnityTest]
        public IEnumerator OwnerReleased_ReleasesClaimForNextRecogniser()
        {
            _owner.ReleaseNativeResources();

            var init = _second.InitialiseAsync();
            while (!init.IsCompleted)
                yield return null;

            Assert.IsTrue(
                _second.IsInitialised,
                "ReleaseNativeResources must free the claim, not only the native state. "
                    + $"Errors: [{string.Join("; ", _secondErrors)}]"
            );
        }
    }
}
#endif
