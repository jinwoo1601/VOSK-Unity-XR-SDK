using NUnit.Framework;
using VoskXR;

namespace VoskXR.Tests.Editor
{
    public class VoskBridgeErrorCodeTests
    {
        [TestCase(VoskBridgeErrorCode.Ok, 0)]
        [TestCase(VoskBridgeErrorCode.ModelLoadFailed, 1)]
        [TestCase(VoskBridgeErrorCode.AudioDeviceUnavailable, 2)]
        [TestCase(VoskBridgeErrorCode.PermissionDenied, 3)]
        [TestCase(VoskBridgeErrorCode.RingBufferOverflow, 4)]
        [TestCase(VoskBridgeErrorCode.AlreadyRunning, 5)]
        [TestCase(VoskBridgeErrorCode.NotInitialised, 6)]
        [TestCase(VoskBridgeErrorCode.AlreadyInitialised, 7)]
        public void ErrorCode_HasExpectedIntegerValue(VoskBridgeErrorCode code, int expected)
        {
            Assert.AreEqual(expected, (int)code);
        }

        [Test]
        public void ToDescription_ReturnsNonEmptyForAllCodes()
        {
            foreach (VoskBridgeErrorCode code in System.Enum.GetValues(typeof(VoskBridgeErrorCode)))
            {
                string description = code.ToDescription();
                Assert.IsNotNull(description, $"Description for {code} is null");
                Assert.IsNotEmpty(description, $"Description for {code} is empty");
            }
        }

        [Test]
        public void ToDescription_UnknownCode_ReturnsNonEmpty()
        {
            var unknown = (VoskBridgeErrorCode)999;
            string description = unknown.ToDescription();
            Assert.IsNotNull(description);
            Assert.IsNotEmpty(description);
        }
    }
}
