// ============================================================================
// Purpose:  Immutable command intent definition with phrase patterns and behavioral flags
// Layer:    Runtime.Commands
// Owns:     VoskCommandDefinition (public readonly struct)
// Depends:  (none)
// ============================================================================
using System;

namespace VoskXR.Commands
{
    public readonly struct VoskCommandDefinition
    {
        public readonly string Intent;

        public readonly string[][] Patterns;

        public readonly bool AllowPartialMatch;

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
