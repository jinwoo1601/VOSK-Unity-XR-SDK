using System;
using System.Collections.Generic;

namespace VoskXR.Commands
{
    /// <summary>
    /// Defines a named slot with a fixed set of allowed values and optional aliases,
    /// or a NumberSequence slot that greedily matches digit words.
    /// Slots are registered once and referenced by name across multiple command definitions.
    /// </summary>
    public readonly struct VoskSlotDefinition
    {
        /// <summary>The slot name used in pattern references, e.g. "weapon".</summary>
        public readonly string Name;

        /// <summary>How this slot matches tokens during parsing.</summary>
        public readonly VoskSlotType Type;

        /// <summary>Allowed values for this slot (Enumerated only).</summary>
        public readonly string[] Values;

        /// <summary>
        /// Maps variant words to canonical values, e.g. "jackals" → "jackal".
        /// <see cref="VoskSlotMatch.Value"/> contains the canonical value after resolution.
        /// </summary>
        public readonly Dictionary<string, string> Aliases;

        /// <summary>Minimum number of digit words to consume (NumberSequence only).</summary>
        public readonly int MinWords;

        /// <summary>Maximum number of digit words to consume (NumberSequence only).</summary>
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

        /// <summary>
        /// Creates a NumberSequence slot that greedily matches consecutive digit words.
        /// </summary>
        /// <param name="name">Slot name used in pattern references.</param>
        /// <param name="minWords">Minimum digit words required for a match (must be >= 1).</param>
        /// <param name="maxWords">Maximum digit words consumed (must be >= minWords).</param>
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
