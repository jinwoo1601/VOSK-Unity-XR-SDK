// ============================================================================
// Purpose:  Manages runtime slot value providers that filter which values the parser accepts
// Layer:    Runtime.Commands
// Owns:     DynamicSlotManager (internal sealed class)
// Depends:  VoskSlotDefinition, VoskSlotType
// ============================================================================
using System;
using System.Collections.Generic;

namespace VoskXR.Commands
{
    /// <summary>
    /// Maintains a registry of slot value providers (functions returning active values)
    /// and builds effective slot arrays by filtering base slots against provider results.
    /// </summary>
    internal sealed class DynamicSlotManager
    {
        Dictionary<string, Func<string[]>> _providers;

        /// <summary>True when at least one provider is registered.</summary>
        internal bool HasProviders => _providers != null && _providers.Count > 0;

        /// <summary>
        /// Registers a function that controls which values of the named slot
        /// the parser will accept.
        /// </summary>
        internal void Register(string slotName, Func<string[]> provider)
        {
            if (slotName == null) throw new ArgumentNullException(nameof(slotName));
            if (provider == null) throw new ArgumentNullException(nameof(provider));

            if (_providers == null)
                _providers = new Dictionary<string, Func<string[]>>(StringComparer.Ordinal);

            _providers[slotName] = provider;
        }

        /// <summary>
        /// Removes a previously registered value provider.
        /// </summary>
        internal bool Unregister(string slotName)
        {
            if (slotName == null) throw new ArgumentNullException(nameof(slotName));
            return _providers != null && _providers.Remove(slotName);
        }

        /// <summary>
        /// Builds an effective slot array by filtering <paramref name="baseSlots"/>
        /// against registered provider results. Returns the original array if no
        /// providers are registered or none produce a filter.
        /// </summary>
        internal VoskSlotDefinition[] BuildEffectiveSlots(VoskSlotDefinition[] baseSlots)
        {
            if (_providers == null || _providers.Count == 0)
                return baseSlots;

            VoskSlotDefinition[] effective = null;

            for (int i = 0; i < baseSlots.Length; i++)
            {
                var slot = baseSlots[i];

                if (slot.Type == VoskSlotType.NumberSequence ||
                    !_providers.TryGetValue(slot.Name, out var provider))
                {
                    if (effective != null)
                        effective[i] = slot;
                    continue;
                }

                var activeValues = provider();
                if (activeValues == null)
                {
                    if (effective != null)
                        effective[i] = slot;
                    continue;
                }

                if (effective == null)
                {
                    effective = new VoskSlotDefinition[baseSlots.Length];
                    Array.Copy(baseSlots, effective, i);
                }

                if (activeValues.Length == 0)
                {
                    effective[i] = new VoskSlotDefinition(slot.Name, Array.Empty<string>(), null);
                    continue;
                }

                var activeSet = new HashSet<string>(activeValues, StringComparer.Ordinal);

                Dictionary<string, string> filteredAliases = null;
                if (slot.Aliases != null)
                {
                    foreach (var kvp in slot.Aliases)
                    {
                        if (activeSet.Contains(kvp.Value))
                        {
                            if (filteredAliases == null)
                                filteredAliases = new Dictionary<string, string>(StringComparer.Ordinal);
                            filteredAliases[kvp.Key] = kvp.Value;
                        }
                    }
                }

                effective[i] = new VoskSlotDefinition(slot.Name, activeValues, filteredAliases);
            }

            return effective ?? baseSlots;
        }
    }
}
