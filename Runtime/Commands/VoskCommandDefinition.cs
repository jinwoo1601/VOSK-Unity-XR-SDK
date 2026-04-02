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

        public VoskCommandDefinition(string intent, string[][] patterns)
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
        }
    }
}
