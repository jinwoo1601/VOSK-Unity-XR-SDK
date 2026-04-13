using NUnit.Framework;
using VoskXR;

namespace VoskXR.Tests.Runtime
{
    public class ParseAlternativesFromJsonTests
    {
        [Test]
        public void TwoAlternatives_ParsesBothWithWordsAndConfidence()
        {
            const string json =
                "{\"alternatives\":[" +
                "{\"confidence\":8430.95," +
                "\"result\":[{\"conf\":0.95,\"end\":0.6,\"start\":0.1,\"word\":\"hello\"}," +
                "{\"conf\":0.87,\"end\":1.2,\"start\":0.7,\"word\":\"world\"}]," +
                "\"text\":\"hello world\"}," +
                "{\"confidence\":7231.42," +
                "\"result\":[{\"conf\":0.65,\"end\":0.6,\"start\":0.1,\"word\":\"yellow\"}," +
                "{\"conf\":0.87,\"end\":1.2,\"start\":0.7,\"word\":\"world\"}]," +
                "\"text\":\"yellow world\"}" +
                "]}";

            var alts = VoskJsonParser.ParseAlternativesFromJson(json);

            Assert.AreEqual(2, alts.Length);

            Assert.AreEqual("hello world", alts[0].Text);
            Assert.AreEqual(8430.95f, alts[0].Confidence, 0.1f);
            Assert.AreEqual(2, alts[0].Words.Length);
            Assert.AreEqual("hello", alts[0].Words[0].Text);
            Assert.AreEqual(0.95f, alts[0].Words[0].Confidence, 0.001f);

            Assert.AreEqual("yellow world", alts[1].Text);
            Assert.AreEqual(7231.42f, alts[1].Confidence, 0.1f);
            Assert.AreEqual(2, alts[1].Words.Length);
            Assert.AreEqual("yellow", alts[1].Words[0].Text);
            Assert.AreEqual(0.65f, alts[1].Words[0].Confidence, 0.001f);
        }

        [Test]
        public void SingleAlternative_ParsesCorrectly()
        {
            const string json =
                "{\"alternatives\":[" +
                "{\"confidence\":5000.0," +
                "\"result\":[{\"conf\":1.0,\"end\":0.4,\"start\":0.1,\"word\":\"yes\"}]," +
                "\"text\":\"yes\"}" +
                "]}";

            var alts = VoskJsonParser.ParseAlternativesFromJson(json);

            Assert.AreEqual(1, alts.Length);
            Assert.AreEqual("yes", alts[0].Text);
            Assert.AreEqual(1, alts[0].Words.Length);
        }

        [Test]
        public void NoAlternativesKey_ReturnsEmpty()
        {
            const string json =
                "{\"result\":[{\"conf\":0.9,\"end\":0.5,\"start\":0.1,\"word\":\"hello\"}]," +
                "\"text\":\"hello\"}";

            var alts = VoskJsonParser.ParseAlternativesFromJson(json);

            Assert.AreEqual(0, alts.Length);
        }

        [Test]
        public void AlternativeWithoutResultArray_WordsAreEmpty()
        {
            const string json =
                "{\"alternatives\":[" +
                "{\"confidence\":100.0,\"text\":\"hello\"}" +
                "]}";

            var alts = VoskJsonParser.ParseAlternativesFromJson(json);

            Assert.AreEqual(1, alts.Length);
            Assert.AreEqual("hello", alts[0].Text);
            Assert.AreEqual(0, alts[0].Words.Length);
        }

        [Test]
        public void PrettyPrintedAlternatives_ParsesCorrectly()
        {
            const string json =
                "{\n" +
                "  \"alternatives\" : [{\n" +
                "      \"confidence\" : 1234.5,\n" +
                "      \"result\" : [{\n" +
                "          \"conf\" : 0.99,\n" +
                "          \"end\" : 0.5,\n" +
                "          \"start\" : 0.1,\n" +
                "          \"word\" : \"test\"\n" +
                "        }],\n" +
                "      \"text\" : \"test\"\n" +
                "    }]\n" +
                "}";

            var alts = VoskJsonParser.ParseAlternativesFromJson(json);

            Assert.AreEqual(1, alts.Length);
            Assert.AreEqual("test", alts[0].Text);
            Assert.AreEqual(1234.5f, alts[0].Confidence, 0.1f);
            Assert.AreEqual("test", alts[0].Words[0].Text);
        }

        [Test]
        public void VoskAlternativeToString_ShowsTextAndScore()
        {
            var alt = new VoskAlternative("hello", 1234.5f, System.Array.Empty<VoskWord>());
            Assert.AreEqual("hello (score 1234.5)", alt.ToString());
        }
    }
}
