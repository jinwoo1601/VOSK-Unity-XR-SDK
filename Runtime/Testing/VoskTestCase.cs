// ============================================================================
// Purpose:  Test case data: input text, expected intent/slots, simulated word confidence
// Layer:    Runtime.Testing
// Owns:     VoskTestCase (public class), ExpectedSlot (public struct)
// Depends:  (none)
// ============================================================================
using System;
using UnityEngine;

namespace VoskXR.Testing
{
    /// <summary>
    /// A single test case for the batch test runner.
    /// Specifies input text, expected command result, and optional simulated word confidence.
    /// </summary>
    [Serializable]
    public class VoskTestCase
    {
        /// <summary>The text to feed through the command parser.</summary>
        [Tooltip("Text to feed through the command parser (as if from VOSK).")]
        public string input;

        /// <summary>
        /// Expected intent name after threshold filtering. Null or empty means
        /// the input should be rejected (no match or below threshold).
        /// </summary>
        [Tooltip("Expected intent name. Leave empty to expect rejection (no match or below threshold).")]
        public string expectedIntent;

        /// <summary>Expected slot values. Each entry is a name/value pair.</summary>
        [Tooltip("Expected slot name/value pairs.")]
        public ExpectedSlot[] expectedSlots;

        /// <summary>
        /// Simulated uniform word confidence for threshold testing.
        /// Set to -1 (default) to omit word data. When >= 0, the runner calls
        /// <see cref="VoskSpeechRecogniser.CreateSimulatedWords"/> to generate
        /// per-word confidence data.
        /// </summary>
        [Tooltip("Simulated word confidence (0-1). Set to -1 to omit word data.")]
        public float wordConfidence = -1f;

        /// <summary>Human-readable description of what this test case verifies.</summary>
        [TextArea]
        public string description;

        /// <summary>True when the test expects no command to be accepted.</summary>
        internal bool ExpectsRejection =>
            string.IsNullOrEmpty(expectedIntent);
    }

    /// <summary>
    /// A single expected slot name/value pair within a <see cref="VoskTestCase"/>.
    /// </summary>
    [Serializable]
    public struct ExpectedSlot
    {
        public string name;
        public string value;
    }
}
