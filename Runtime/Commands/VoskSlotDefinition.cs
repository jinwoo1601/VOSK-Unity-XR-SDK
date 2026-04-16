// ============================================================================
// Purpose:  Immutable slot definition (enumerated values + aliases, or number sequence)
// Layer:    Runtime.Commands
// Owns:     VoskSlotDefinition (public readonly struct)
// Depends:  VoskSlotType
// ============================================================================
using System;
using System.Collections.Generic;

namespace VoskXR.Commands
{
    public readonly struct VoskSlotDefinition
    {
        public readonly string Name;

        public readonly VoskSlotType Type;

        public readonly string[] Values;

        public readonly Dictionary<string, string> Aliases;

        public readonly int MinWords;

        public readonly int MaxWords;

        public VoskSlotDefinition(string name, string[] values)
            : this(name, values, null)
        {
        }

        public VoskSlotDefinition(string name, string[] values, Dictionary<string, string> aliases)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));

            if (values == null)
                throw new ArgumentNullException(nameof(values));

            Type = VoskSlotType.Enumerated;

            var copy = new string[values.Length];
            Array.Copy(values, copy, values.Length);
            Values = copy;

            if (aliases != null && aliases.Count > 0)
            {
                Aliases = new Dictionary<string, string>(aliases.Count, StringComparer.Ordinal);
                foreach (var kvp in aliases)
                    Aliases[kvp.Key] = kvp.Value;
            }
            else
            {
                Aliases = null;
            }

            MinWords = 0;
            MaxWords = 0;
        }

        VoskSlotDefinition(string name, int minWords, int maxWords)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Type = VoskSlotType.NumberSequence;
            Values = Array.Empty<string>();
            Aliases = null;
            MinWords = minWords;
            MaxWords = maxWords;
        }

        public static VoskSlotDefinition NumberSequence(string name, int minWords = 1, int maxWords = 3)
        {
            if (minWords < 1)
                throw new ArgumentOutOfRangeException(nameof(minWords), "Must be >= 1.");
            if (maxWords < minWords)
                throw new ArgumentOutOfRangeException(nameof(maxWords), "Must be >= minWords.");

            return new VoskSlotDefinition(name, minWords, maxWords);
        }
    }
}
