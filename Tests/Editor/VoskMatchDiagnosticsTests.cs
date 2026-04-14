using NUnit.Framework;
using VoskXR;
using VoskXR.Commands;

namespace VoskXR.Tests.Editor
{
    public class VoskMatchDiagnosticsTests
    {
        // 2.1
        [Test]
        public void Constructor_NullWords_DefaultsToEmpty()
        {
            var diag = new VoskMatchDiagnostics("hello", null,
                new VoskMatchAttempt[0], 1);

            Assert.IsNotNull(diag.Words);
            Assert.AreEqual(0, diag.Words.Length);
        }

        // 2.2
        [Test]
        public void Constructor_NullAttempts_DefaultsToEmpty()
        {
            var diag = new VoskMatchDiagnostics("hello",
                new VoskWord[0], null, 1);

            Assert.IsNotNull(diag.Attempts);
            Assert.AreEqual(0, diag.Attempts.Length);
        }

        // 2.3
        [Test]
        public void AttemptConstructor_NullSlots_DefaultsToEmpty()
        {
            var attempt = new VoskMatchAttempt(
                "fire", "fire {weapon}", 0.9f, 0.6f, 0.85f, 0.4f,
                null, null, true);

            Assert.IsNotNull(attempt.Slots);
            Assert.AreEqual(0, attempt.Slots.Length);
        }

        // 2.4 — readonly struct enforced at compile time. This test verifies
        //        the fields are read-only by checking they survive round-trip.
        [Test]
        public void Structs_AreReadonly_FieldsPreserved()
        {
            var diag = new VoskMatchDiagnostics("text",
                new[] { new VoskWord("w", 0.9f, 0f, 0.3f) },
                new[] { new VoskMatchAttempt("intent", "pattern", 1f, 0.6f, 0.9f, 0.4f, null, null, true) },
                42);

            Assert.AreEqual("text", diag.InputText);
            Assert.AreEqual(42, diag.Frame);
            Assert.AreEqual(1, diag.Words.Length);
            Assert.AreEqual(1, diag.Attempts.Length);
        }

        // 2.5
        [Test]
        public void Frame_StoresFrameCountSnapshot()
        {
            var diag = new VoskMatchDiagnostics("text", null, null, 42);
            Assert.AreEqual(42, diag.Frame);
        }

        // 2.6
        [Test]
        public void Attempt_Accepted_NoRejectReason()
        {
            var attempt = new VoskMatchAttempt(
                "fire", "fire {weapon}", 0.9f, 0.6f, 0.85f, 0.4f,
                null, null, true);

            Assert.IsTrue(attempt.IsAccepted);
            Assert.IsNull(attempt.RejectReason);
        }

        // 2.7
        [Test]
        public void Attempt_Rejected_HasRejectReason()
        {
            var attempt = new VoskMatchAttempt(
                "fire", "fire {weapon}", 0.3f, 0.6f, 0.85f, 0.4f,
                null, "score 0.30 < minScore 0.60", false);

            Assert.IsFalse(attempt.IsAccepted);
            Assert.IsNotNull(attempt.RejectReason);
            Assert.AreEqual("score 0.30 < minScore 0.60", attempt.RejectReason);
        }

        // 2.8
        [Test]
        public void SlotMatch_Confidence_NegativeOneWhenUnavailable()
        {
            var slot = new VoskDiagnosticSlotMatch("weapon", "missiles", 1, 2, -1f);
            Assert.AreEqual(-1f, slot.Confidence);
        }

        // 2.9
        [Test]
        public void SlotMatch_WordSpan_StartEndCorrect()
        {
            var slot = new VoskDiagnosticSlotMatch("target", "hotel one", 2, 4, 0.9f);
            Assert.AreEqual(2, slot.StartWord);
            Assert.AreEqual(4, slot.EndWord);
        }
    }
}
