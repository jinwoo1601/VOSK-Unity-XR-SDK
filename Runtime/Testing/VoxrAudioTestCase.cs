// ============================================================================
// Purpose:  Audio test case data: fixture WAV path, expected intent/slots/transcript for replay
// Layer:    Runtime.Testing
// Owns:     VoxrAudioTestCase (public class)
// Depends:  ExpectedSlot
// ============================================================================
using System;
using UnityEngine;

namespace VoXR.Testing
{
    [Serializable]
    public class VoxrAudioTestCase
    {
        [Tooltip(
            "WAV path relative to the fixture root (e.g. audio/tts/cease_fire.wav). 48 kHz mono 16-bit."
        )]
        public string file;

        [Tooltip("Fixture category (clean, slot-variant, homophone, filler, split, silence).")]
        public string category;

        [Tooltip("Expected intent name. Leave empty to expect no recognized command.")]
        public string expectedIntent;

        [Tooltip("Expected slot name/value pairs.")]
        public ExpectedSlot[] expectedSlots;

        [Tooltip("Expected final transcript. Leave empty to skip the transcript assertion.")]
        public string expectedTranscript;

        [TextArea]
        public string description;

        internal bool ExpectsNoCommand => string.IsNullOrEmpty(expectedIntent);
    }
}
