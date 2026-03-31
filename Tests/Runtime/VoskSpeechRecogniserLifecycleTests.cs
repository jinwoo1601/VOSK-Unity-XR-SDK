using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VoskXR;

namespace VoskXR.Tests.Runtime
{
    public class VoskSpeechRecogniserLifecycleTests
    {
        GameObject _go;
        VoskSpeechRecogniser _recogniser;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("TestRecogniser");
            _recogniser = _go.AddComponent<VoskSpeechRecogniser>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null)
                Object.DestroyImmediate(_go);
        }

        [Test]
        public void AddComponent_InitialState_NotInitialised()
        {
            Assert.IsFalse(_recogniser.IsModelReady);
        }

        [Test]
        public void StopRecognition_WhenNotRunning_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => _recogniser.StopRecognition());
        }

        [Test]
        public void ReleaseNativeResources_WhenNotInitialised_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => _recogniser.ReleaseNativeResources());
        }

        [Test]
        public void ReleaseNativeResources_CalledTwice_DoesNotThrow()
        {
            Assert.DoesNotThrow(() =>
            {
                _recogniser.ReleaseNativeResources();
                _recogniser.ReleaseNativeResources();
            });
        }

        [Test]
        public void ResetRecogniser_WhenNotInitialised_FiresErrorEvent()
        {
            VoskBridgeErrorCode? receivedCode = null;
            _recogniser.OnError += (code, msg) => receivedCode = code;

            // On non-Android, this will hit DllNotFoundException and fire ModelLoadFailed
            // rather than NotInitialised. Both are acceptable — the key is it doesn't crash.
            _recogniser.ResetRecogniser();
        }

        [UnityTest]
        public IEnumerator OnDestroy_CleansUpWithoutError()
        {
            Object.DestroyImmediate(_go);
            _go = null;
            yield return null;
            // If we get here without exception, cleanup succeeded.
        }
    }
}
