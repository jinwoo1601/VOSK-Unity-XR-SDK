using NUnit.Framework;
using VoskXR;

namespace VoskXR.Tests.Runtime
{
    public class ParseWordsFromJsonTests
    {
        [Test]
        public void TypicalResult_ParsesAllWords()
        {
            const string json =
                "{\"result\":[" +
                "{\"conf\":0.95,\"end\":0.60,\"start\":0.10,\"word\":\"hello\"}," +
                "{\"conf\":0.87,\"end\":1.20,\"start\":0.70,\"word\":\"world\"}" +
                "],\"text\":\"hello world\"}";

            var words = VoskJsonParser.ParseWordsFromJson(json);

            Assert.AreEqual(2, words.Length);

            Assert.AreEqual("hello", words[0].Text);
            Assert.AreEqual(0.95f, words[0].Confidence, 0.001f);
            Assert.AreEqual(0.10f, words[0].StartTime, 0.001f);
            Assert.AreEqual(0.60f, words[0].EndTime, 0.001f);

            Assert.AreEqual("world", words[1].Text);
            Assert.AreEqual(0.87f, words[1].Confidence, 0.001f);
            Assert.AreEqual(0.70f, words[1].StartTime, 0.001f);
            Assert.AreEqual(1.20f, words[1].EndTime, 0.001f);
        }

        [Test]
        public void SingleWord_ParsesCorrectly()
        {
            const string json =
                "{\"result\":[{\"conf\":1.000000,\"end\":0.39,\"start\":0.09,\"word\":\"yes\"}]," +
                "\"text\":\"yes\"}";

            var words = VoskJsonParser.ParseWordsFromJson(json);

            Assert.AreEqual(1, words.Length);
            Assert.AreEqual("yes", words[0].Text);
            Assert.AreEqual(1.0f, words[0].Confidence, 0.001f);
        }

        [Test]
        public void EmptyText_NoResultKey_ReturnsEmpty()
        {
            const string json = "{\"text\":\"\"}";

            var words = VoskJsonParser.ParseWordsFromJson(json);

            Assert.AreEqual(0, words.Length);
        }

        [Test]
        public void EmptyResultArray_ReturnsEmpty()
        {
            const string json = "{\"result\":[],\"text\":\"\"}";

            var words = VoskJsonParser.ParseWordsFromJson(json);

            Assert.AreEqual(0, words.Length);
        }

        [Test]
        public void PrettyPrintedJson_ParsesCorrectly()
        {
            const string json =
                "{\n" +
                "  \"result\" : [{\n" +
                "      \"conf\" : 0.876543,\n" +
                "      \"end\" : 1.020000,\n" +
                "      \"start\" : 0.390000,\n" +
                "      \"word\" : \"test\"\n" +
                "    }],\n" +
                "  \"text\" : \"test\"\n" +
                "}";

            var words = VoskJsonParser.ParseWordsFromJson(json);

            Assert.AreEqual(1, words.Length);
            Assert.AreEqual("test", words[0].Text);
            Assert.AreEqual(0.876543f, words[0].Confidence, 0.0001f);
        }

        [Test]
        public void ZeroConfidence_ParsedAsZero()
        {
            const string json =
                "{\"result\":[{\"conf\":0.000000,\"end\":0.5,\"start\":0.1,\"word\":\"um\"}]," +
                "\"text\":\"um\"}";

            var words = VoskJsonParser.ParseWordsFromJson(json);

            Assert.AreEqual(1, words.Length);
            Assert.AreEqual(0f, words[0].Confidence, 0.001f);
        }

        [Test]
        public void VoskWordToString_ShowsTextAndConfidence()
        {
            var word = new VoskWord("hello", 0.95f, 0.1f, 0.6f);
            Assert.AreEqual("hello (0.95)", word.ToString());
        }
    }
}
