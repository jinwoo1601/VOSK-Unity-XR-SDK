// ============================================================================
// Purpose:  PlayMode smoke tests for the EditorMicBackend WAV playback seam
// Layer:    Tests.Runtime
// Owns:     VoxrPlaybackSeamTests (public class)
// Depends:  VoxrSpeechRecogniser, EditorMicBackend, VoxrBridgeErrorCode
// ============================================================================
#if UNITY_EDITOR_WIN
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VoXR.Tests.Runtime
{
    public class VoxrPlaybackSeamTests
    {
        GameObject _go;
        VoxrSpeechRecogniser _speech;
        List<string> _initErrors;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _go = new GameObject("PlaybackSeamTest");
            _speech = _go.AddComponent<VoxrSpeechRecogniser>();
            _initErrors = new List<string>();
            _speech.OnError += (code, msg) => _initErrors.Add($"{code}: {msg}");

            var init = _speech.InitialiseAsync();
            while (!init.IsCompleted)
                yield return null;

            Assert.IsTrue(
                _speech.IsInitialised,
                "Recogniser failed to initialise — is the VOSK model available to the "
                    + $"host project? Errors: [{string.Join("; ", _initErrors)}]"
            );
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _speech.EditorBackend?.StopPlayback();
            Object.Destroy(_go);
            yield return null;
        }

        static float[] Silence(float seconds) => new float[(int)(seconds * 48000)];

        static float[] Sine(float seconds, float peak, float freqHz = 220f)
        {
            var samples = new float[(int)(seconds * 48000)];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = peak * Mathf.Sin(2f * Mathf.PI * freqHz * i / 48000f);
            return samples;
        }

        [UnityTest]
        public IEnumerator StartPlayback_WrongSampleRate_RejectedNamingBothRates()
        {
            var backend = _speech.EditorBackend;
            var errors = new List<string>();

            bool ok = backend.StartPlayback(Sine(0.2f, 0.2f), 44100, (c, m) => errors.Add(m));

            Assert.IsFalse(ok);
            Assert.IsFalse(backend.IsInPlayback);
            Assert.AreEqual(1, errors.Count);
            StringAssert.Contains("44100", errors[0]);
            StringAssert.Contains("48000", errors[0]);
            yield return null;
        }

        [UnityTest]
        public IEnumerator StartPlayback_EmptySamples_Rejected()
        {
            var backend = _speech.EditorBackend;
            var errors = new List<string>();

            bool ok = backend.StartPlayback(new float[0], 48000, (c, m) => errors.Add(m));

            Assert.IsFalse(ok);
            Assert.IsFalse(backend.IsInPlayback);
            Assert.AreEqual(1, errors.Count);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Playback_Silence_CompletesAndFlushesOneFinalResult()
        {
            var backend = _speech.EditorBackend;
            int finalCount = 0;
            string finalText = null;
            _speech.OnFinalResult += t =>
            {
                finalCount++;
                finalText = t;
            };

            Assert.IsTrue(backend.StartPlayback(Silence(1f), 48000, null));
            Assert.IsTrue(backend.IsInPlayback);

            int guard = 0;
            while (backend.TickPlayback(_speech.EditorDispatcher))
            {
                Assert.Less(++guard, 1000, "playback did not complete");
                yield return null;
            }

            Assert.IsFalse(backend.IsInPlayback, "playback must disarm on exhaustion");
            Assert.AreEqual(1, finalCount, "end-of-playback must flush exactly one final result");
            Assert.AreEqual(string.Empty, finalText, "silence must produce an empty transcript");
        }

        [UnityTest]
        public IEnumerator Playback_QuietSine_EngagesAgc()
        {
            var backend = _speech.EditorBackend;
            Assert.IsTrue(backend.StartPlayback(Sine(1f, 0.05f), 48000, null));

            while (backend.TickPlayback(_speech.EditorDispatcher))
                yield return null;

            Assert.Greater(
                _speech.EditorAgcGain,
                1f,
                "AGC must have amplified a 0.05-peak signal — pre-DSP entry (F2)"
            );
        }

        [UnityTest]
        public IEnumerator MicStart_DuringPlayback_Rejected()
        {
            var backend = _speech.EditorBackend;
            Assert.IsTrue(backend.StartPlayback(Silence(1f), 48000, null));

            var errors = new List<string>();
            bool ok = backend.Start((c, m) => errors.Add(m));

            Assert.IsFalse(ok);
            Assert.AreEqual(1, errors.Count);
            StringAssert.Contains("playback", errors[0]);
            yield return null;
        }

        [UnityTest]
        public IEnumerator SetGrammar_DuringPlayback_Rejected()
        {
            var backend = _speech.EditorBackend;
            Assert.IsTrue(backend.StartPlayback(Silence(1f), 48000, null));

            var errors = new List<string>();
            backend.SetGrammar("[\"test\"]", (c, m) => errors.Add(m));

            Assert.AreEqual(1, errors.Count);
            StringAssert.Contains("playback", errors[0]);

            // The recognizer must still be usable: finish the replay normally.
            while (backend.TickPlayback(_speech.EditorDispatcher))
                yield return null;
            Assert.IsFalse(backend.IsInPlayback);
        }

        [UnityTest]
        public IEnumerator StopPlayback_DisarmsWithoutFlushing()
        {
            var backend = _speech.EditorBackend;
            int finalCount = 0;
            _speech.OnFinalResult += _ => finalCount++;

            Assert.IsTrue(backend.StartPlayback(Silence(1f), 48000, null));
            backend.StopPlayback();

            Assert.IsFalse(backend.IsInPlayback);
            Assert.IsFalse(backend.TickPlayback(_speech.EditorDispatcher));
            Assert.AreEqual(0, finalCount, "StopPlayback must not flush a final result");
            yield return null;
        }

        [UnityTest]
        public IEnumerator StartPlayback_ReArmOverActivePlayback_StartsFresh()
        {
            var backend = _speech.EditorBackend;

            Assert.IsTrue(backend.StartPlayback(Silence(1f), 48000, null));
            Assert.IsTrue(backend.TickPlayback(_speech.EditorDispatcher));

            // Re-arm mid-replay with a shorter buffer: 0.5 s = exactly 5 chunks.
            Assert.IsTrue(backend.StartPlayback(Silence(0.5f), 48000, null));

            int chunks = 0;
            while (backend.TickPlayback(_speech.EditorDispatcher))
            {
                chunks++;
                yield return null;
            }

            Assert.AreEqual(5, chunks, "re-armed playback must run the new buffer from the start");
            Assert.IsFalse(backend.IsInPlayback);
        }
    }
}
#endif
