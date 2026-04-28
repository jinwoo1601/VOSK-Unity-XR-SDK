using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using VoXR;

namespace VoXR.Tests.Runtime
{
    public class VoxrSpeechRecogniserInjectionTests
    {
        GameObject _go;
        VoxrSpeechRecogniser _recogniser;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("TestRecogniser");
            _recogniser = _go.AddComponent<VoxrSpeechRecogniser>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null)
                Object.DestroyImmediate(_go);
        }

        [Test]
        public void InjectResult_FiresOnFinalResult()
        {
            string received = null;
            _recogniser.OnFinalResult += text => received = text;

            _recogniser.InjectResult("hello world");

            Assert.AreEqual("hello world", received);
        }

        [Test]
        public void InjectResult_FiresOnResult_WithProvidedWords()
        {
            VoxrResult? received = null;
            _recogniser.OnResult += r => received = r;

            var words = new[]
            {
                new VoxrWord("hello", 0.9f, 0f, 0.3f),
                new VoxrWord("world", 0.8f, 0.3f, 0.6f),
            };
            _recogniser.InjectResult("hello world", words);

            Assert.IsTrue(received.HasValue);
            Assert.AreEqual("hello world", received.Value.Text);
            Assert.AreEqual(2, received.Value.Words.Length);
            Assert.AreEqual("hello", received.Value.Words[0].Text);
            Assert.AreEqual(0.9f, received.Value.Words[0].Confidence);
            Assert.AreEqual("world", received.Value.Words[1].Text);
        }

        [Test]
        public void InjectResult_FiresOnResult_WithNullWords_DefaultsToEmpty()
        {
            VoxrResult? received = null;
            _recogniser.OnResult += r => received = r;

            _recogniser.InjectResult("hello", words: null);

            Assert.IsTrue(received.HasValue);
            Assert.IsNotNull(received.Value.Words);
            Assert.AreEqual(0, received.Value.Words.Length);
        }

        [Test]
        public void InjectResult_EmptyText_StillFiresEvents()
        {
            // The real audio path does not short-circuit on empty text, so neither does Inject.
            int finalCount = 0;
            int resultCount = 0;
            _recogniser.OnFinalResult += _ => finalCount++;
            _recogniser.OnResult += _ => resultCount++;

            _recogniser.InjectResult("");

            Assert.AreEqual(1, finalCount);
            Assert.AreEqual(1, resultCount);
        }

        [Test]
        public void InjectResult_EventOrder_FinalResultBeforeResult()
        {
            var order = new List<string>();
            _recogniser.OnFinalResult += _ => order.Add("final");
            _recogniser.OnResult += _ => order.Add("result");

            _recogniser.InjectResult("hello");

            Assert.AreEqual(2, order.Count);
            Assert.AreEqual("final", order[0]);
            Assert.AreEqual("result", order[1]);
        }

        [Test]
        public void InjectPartialResult_FiresOnPartialResult()
        {
            string received = null;
            _recogniser.OnPartialResult += text => received = text;

            _recogniser.InjectPartialResult("hel");

            Assert.AreEqual("hel", received);
        }

        [Test]
        public void InjectPartialResult_EmptyText_StillFires()
        {
            int count = 0;
            _recogniser.OnPartialResult += _ => count++;

            _recogniser.InjectPartialResult("");

            Assert.AreEqual(1, count);
        }

        [TestCase("hello", 1, 1.0f)]
        [TestCase("hello world", 2, 0.5f)]
        [TestCase("launch all missiles target hotel one", 6, 0.75f)]
        public void CreateSimulatedWords_TokenCountAndConfidence(string text, int expectedCount, float confidence)
        {
            var words = VoxrSpeechRecogniser.CreateSimulatedWords(text, confidence);

            Assert.AreEqual(expectedCount, words.Length);
            for (int i = 0; i < words.Length; i++)
                Assert.AreEqual(confidence, words[i].Confidence,
                    $"Word {i} ({words[i].Text}) confidence mismatch");
        }

        [TestCase("")]
        [TestCase(null)]
        [TestCase("   ")]
        [TestCase("\t\n")]
        public void CreateSimulatedWords_EmptyOrWhitespace_ReturnsEmpty(string text)
        {
            var words = VoxrSpeechRecogniser.CreateSimulatedWords(text);

            Assert.IsNotNull(words);
            Assert.AreEqual(0, words.Length);
        }

        [Test]
        public void CreateSimulatedWords_TimingIsSequential()
        {
            var words = VoxrSpeechRecogniser.CreateSimulatedWords("one two three");

            Assert.AreEqual(3, words.Length);
            for (int i = 0; i < words.Length - 1; i++)
                Assert.AreEqual(words[i].EndTime, words[i + 1].StartTime, 1e-6f,
                    $"Gap between word {i} and {i + 1}");
        }
    }
}
