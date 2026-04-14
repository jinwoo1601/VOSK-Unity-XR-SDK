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
    [Serializable]
    public class VoskTestCase
    {
        [Tooltip("Text to feed through the command parser (as if from VOSK).")]
        public string input;

        [Tooltip("Expected intent name. Leave empty to expect rejection (no match or below threshold).")]
        public string expectedIntent;

        [Tooltip("Expected slot name/value pairs.")]
        public ExpectedSlot[] expectedSlots;

        [Tooltip("Simulated word confidence (0-1). Set to -1 to omit word data.")]
        public float wordConfidence = -1f;

        [TextArea]
        public string description;

        internal bool ExpectsRejection =>
            string.IsNullOrEmpty(expectedIntent);
    }

    [Serializable]
    public struct ExpectedSlot
    {
        public string name;
        public string value;
    }
}
