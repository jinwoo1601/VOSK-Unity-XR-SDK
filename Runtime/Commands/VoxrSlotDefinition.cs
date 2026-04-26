// ============================================================================
// Purpose:  Immutable slot definition (enumerated values + aliases, or number sequence)
// Layer:    Runtime.Commands
// Owns:     VoxrSlotDefinition (public readonly struct)
// Depends:  VoxrSlotType
// ============================================================================
using System;
using System.Collections.Generic;

namespace VoXR.Commands
{
    public readonly struct VoxrSlotDefinition
    {
        public readonly string Name;

        public readonly VoxrSlotType Type;

        public readonly string[] Values;

        public readonly Dictionary<string, string> Aliases;

        public readonly int MinWords;

        public readonly int MaxWords;

        public VoxrSlotDefinition(string name, string[] values)
            : this(name, values, null)
        {
        }

        public VoxrSlotDefinition(string name, string[] values, Dictionary<string, string> aliases)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));

            if (values == null)
                throw new ArgumentNullException(nameof(values));

            Type = VoxrSlotType.Enumerated;

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

        VoxrSlotDefinition(string name, int minWords, int maxWords)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Type = VoxrSlotType.NumberSequence;
            Values = Array.Empty<string>();
            Aliases = null;
            MinWords = minWords;
            MaxWords = maxWords;
        }

        public static VoxrSlotDefinition NumberSequence(string name, int minWords = 1, int maxWords = 3)
        {
            if (minWords < 1)
                throw new ArgumentOutOfRangeException(nameof(minWords), "Must be >= 1.");
            if (maxWords < minWords)
                throw new ArgumentOutOfRangeException(nameof(maxWords), "Must be >= minWords.");

            return new VoxrSlotDefinition(name, minWords, maxWords);
        }

        public static VoxrSlotDefinition OneOf(string name, params string[] values)
            => new VoxrSlotDefinition(name, values ?? Array.Empty<string>());
    }
}
