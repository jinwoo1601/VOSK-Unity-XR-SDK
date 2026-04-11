#if UNITY_EDITOR_WIN
using NUnit.Framework;
using UnityEngine;
using VoskXR;

namespace VoskXR.Tests.Editor
{
    /// <summary>
    /// Category 5: Audio metric tests (ComputeRms, forwarded properties).
    /// Windows Editor only — the underlying code is #if UNITY_EDITOR_WIN guarded.
    /// </summary>
    public class AudioMetricTests
    {
        // 5.4
        [Test]
        public void ComputeRms_EmptyBuffer_ReturnsZero()
        {
            float result = EditorMicBackend.ComputeRms(new float[0], 0);
            Assert.AreEqual(0f, result);
        }

        // 5.5
        [Test]
        public void ComputeRms_KnownSignal_ReturnsExpected()
        {
            // Buffer of constant 0.5 values → RMS = sqrt(0.25) = 0.5
            var buffer = new float[100];
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = 0.5f;

            float result = EditorMicBackend.ComputeRms(buffer, buffer.Length);
            Assert.AreEqual(0.5f, result, 1e-5f);
        }

        // 5.6
        [Test]
        public void ForwardedEditorPreAgcRms_NullBackend_ReturnsZero()
        {
            var go = new GameObject("SpeechTest");
            try
            {
                var speech = go.AddComponent<VoskSpeechRecogniser>();
                // _editorBackend is null before InitialiseAsync
                Assert.AreEqual(0f, speech.EditorPreAgcRms);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // 5.7
        [Test]
        public void ForwardedEditorPostAgcRms_NullBackend_ReturnsZero()
        {
            var go = new GameObject("SpeechTest");
            try
            {
                var speech = go.AddComponent<VoskSpeechRecogniser>();
                Assert.AreEqual(0f, speech.EditorPostAgcRms);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // 5.8
        [Test]
        public void ForwardedEditorAgcGain_NullBackend_ReturnsOne()
        {
            var go = new GameObject("SpeechTest");
            try
            {
                var speech = go.AddComponent<VoskSpeechRecogniser>();
                Assert.AreEqual(1f, speech.EditorAgcGain);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
#endif
