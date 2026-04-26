using NUnit.Framework;
using VoXR;

namespace VoXR.Tests.Editor
{
    public class VoxrBridgeErrorCodeTests
    {
        [TestCase(VoxrBridgeErrorCode.Ok, 0)]
        [TestCase(VoxrBridgeErrorCode.ModelLoadFailed, 1)]
        [TestCase(VoxrBridgeErrorCode.AudioDeviceUnavailable, 2)]
        [TestCase(VoxrBridgeErrorCode.PermissionDenied, 3)]
        [TestCase(VoxrBridgeErrorCode.RingBufferOverflow, 4)]
        [TestCase(VoxrBridgeErrorCode.AlreadyRunning, 5)]
        [TestCase(VoxrBridgeErrorCode.NotInitialised, 6)]
        [TestCase(VoxrBridgeErrorCode.AlreadyInitialised, 7)]
        public void ErrorCode_HasExpectedIntegerValue(VoxrBridgeErrorCode code, int expected)
        {
            Assert.AreEqual(expected, (int)code);
        }

        [Test]
        public void ToDescription_ReturnsNonEmptyForAllCodes()
        {
            foreach (VoxrBridgeErrorCode code in System.Enum.GetValues(typeof(VoxrBridgeErrorCode)))
            {
                string description = code.ToDescription();
                Assert.IsNotNull(description, $"Description for {code} is null");
                Assert.IsNotEmpty(description, $"Description for {code} is empty");
            }
        }

        [Test]
        public void ToDescription_UnknownCode_ReturnsNonEmpty()
        {
            var unknown = (VoxrBridgeErrorCode)999;
            string description = unknown.ToDescription();
            Assert.IsNotNull(description);
            Assert.IsNotEmpty(description);
        }
    }
}
