// ============================================================================
// Purpose:  Enum distinguishing enumerated slots from number-sequence slots
// Layer:    Runtime.Commands
// Owns:     VoskSlotType (public enum)
// Depends:  (none)
// ============================================================================
namespace VoskXR.Commands
{
    /// <summary>
    /// Determines how a slot matches tokens during command parsing.
    /// </summary>
    public enum VoskSlotType
    {
        /// <summary>Matches against a fixed set of allowed values and aliases.</summary>
        Enumerated,

        /// <summary>Greedily consumes consecutive number-word tokens (e.g. "two seven zero").</summary>
        NumberSequence
    }
}
