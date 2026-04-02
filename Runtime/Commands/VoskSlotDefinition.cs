using System;

namespace VoskXR.Commands
{
    /// <summary>
    /// Defines a named slot with a fixed set of allowed values.
    /// Slots are registered once and referenced by name across multiple command definitions.
    /// </summary>
    public readonly struct VoskSlotDefinition
    {
        /// <summary>The slot name used in pattern references, e.g. "weapon".</summary>
        public readonly string Name;

        /// <summary>Allowed values for this slot, e.g. ["missiles", "torpedoes", "jackal"].</summary>
        public readonly string[] Values;

        public VoskSlotDefinition(string name, string[] values)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));

            if (values == null)
                throw new ArgumentNullException(nameof(values));

            var copy = new string[values.Length];
            Array.Copy(values, copy, values.Length);
            Values = copy;
        }
    }
}
