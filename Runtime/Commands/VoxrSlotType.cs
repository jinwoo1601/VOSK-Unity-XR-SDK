// ============================================================================
// Purpose:  Enum distinguishing enumerated slots from number-sequence slots
// Layer:    Runtime.Commands
// Owns:     VoxrSlotType (public enum)
// Depends:  (none)
// ============================================================================
namespace VoXR.Commands
{
    public enum VoxrSlotType
    {
        /// <summary>
        /// Matches against the slot definition's fixed set of values and aliases. The matched
        /// value is the canonical value the alias resolved to.
        /// </summary>
        Enumerated,

        /// <summary>
        /// Greedily consumes consecutive number words from
        /// <see cref="VoxrNumberParser.DigitVocabulary"/> -- zero through nineteen, the tens, plus
        /// "hundred" and "thousand" -- within the definition's MinWords/MaxWords range.
        /// </summary>
        /// <remarks>
        /// The matched value is those words as spoken: "orient heading two seven zero" fills the
        /// slot with <c>"two seven zero"</c>, never <c>"270"</c>, so <c>int.TryParse</c> on it
        /// always fails silently. Convert with <see cref="VoxrNumberParser.ParseDigitSequence"/>
        /// or <see cref="VoxrNumberParser.ParseCardinal"/>.
        /// </remarks>
        NumberSequence
    }
}
