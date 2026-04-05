using System;
using System.Collections.Generic;

namespace VoskXR.Commands
{
    /// <summary>
    /// Defines a named slot with a fixed set of allowed values and optional aliases.
    /// Slots are registered once and referenced by name across multiple command definitions.
    /// </summary>
    public readonly struct VoskSlotDefinition
    {
        /// <summary>The slot name used in pattern references, e.g. "weapon".</summary>
        public readonly string Name;

        /// <summary>Allowed values for this slot, e.g. ["missiles", "torpedoes", "jackal"].</summary>
        public readonly string[] Values;

        /// <summary>
        /// Maps variant words to canonical values, e.g. "jackals" → "jackal".
        /// <see cref="VoskSlotMatch.Value"/> contains the canonical value after resolution.
        /// </summary>
        public readonly Dictionary<string, string> Aliases;

        public VoskSlotDefinition(string name, string[] values)
            : this(name, values, null)
        {
        }

        public VoskSlotDefinition(string name, string[] values, Dictionary<string, string> aliases)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));

            if (values == null)
                throw new ArgumentNullException(nameof(values));

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
        }
    }
}
