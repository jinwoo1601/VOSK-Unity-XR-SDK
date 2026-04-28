using NUnit.Framework;

namespace VoXR.Tests.Runtime
{
    // Probe that System.Buffers.Text.Utf8Parser is present in the Unity profile.
    // VoxrJsonParser uses Utf8Parser.TryParse on the recognition hot path for
    // zero-allocation float parsing. If this test ever fails (older Unity / a
    // stripped profile), the parser must fall back to
    // float.TryParse(Encoding.UTF8.GetString(...)) — see VoxrJsonParser.ParseFloatValue.
    public class Utf8ParserAvailabilityTests
    {
        [Test]
        public void Utf8Parser_IsAvailable_InEditorRuntime()
        {
            Assert.IsTrue(System.Buffers.Text.Utf8Parser.TryParse(
                new byte[] { (byte)'1', (byte)'.', (byte)'5' },
                out float v, out _));
            Assert.AreEqual(1.5f, v);
        }
    }
}
