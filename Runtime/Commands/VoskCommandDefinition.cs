// ============================================================================
// Purpose:  Immutable command intent definition with phrase patterns and behavioral flags
// Layer:    Runtime.Commands
// Owns:     VoskCommandDefinition (public readonly struct)
// Depends:  (none)
// ============================================================================
using System;

namespace VoskXR.Commands
{
    /// <summary>
    /// Defines a command intent with one or more phrase patterns.
    /// Each pattern is an array of literal words and slot references
    /// (<c>{slot}</c> for required, <c>{?slot}</c> for optional).
    /// </summary>
    public readonly struct VoskCommandDefinition
    {
        /// <summary>The intent name, e.g. "launch_weapon".</summary>
        public readonly string Intent;

        /// <summary>
        /// Phrase templates for this command. Each inner array is one pattern
        /// containing literal tokens and slot references.
        /// </summary>
        public readonly string[][] Patterns;

        /// <summary>
        /// When true, a match with unfilled required slots enters pending state
        /// instead of being rejected, allowing follow-up speech to fill the gaps.
        /// </summary>
        public readonly bool AllowPartialMatch;

        /// <summary>
        /// When true, a fully-matched command enters pending state awaiting
        /// explicit confirmation ("confirm" / "cancel") before firing.
        /// </summary>
        public readonly bool RequiresConfirmation;

        public VoskCommandDefinition(string intent, string[][] patterns)
            : this(intent, patterns, false, false)
        {
        }

        public VoskCommandDefinition(string intent, string[][] patterns,
            bool allowPartialMatch = false, bool requiresConfirmation = false)
        {
            Intent = intent ?? throw new ArgumentNullException(nameof(intent));

            if (patterns == null)
                throw new ArgumentNullException(nameof(patterns));

            var outerCopy = new string[patterns.Length][];
            for (int i = 0; i < patterns.Length; i++)
            {
                if (patterns[i] == null)
                    throw new ArgumentNullException($"patterns[{i}]");

                var inner = new string[patterns[i].Length];
                Array.Copy(patterns[i], inner, patterns[i].Length);
                outerCopy[i] = inner;
            }

            Patterns = outerCopy;
            AllowPartialMatch = allowPartialMatch;
            RequiresConfirmation = requiresConfirmation;
        }
    }
}
