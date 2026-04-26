// ============================================================================
// Purpose:  Manages runtime slot value providers that filter which values the parser accepts
// Layer:    Runtime.Commands
// Owns:     DynamicSlotManager (internal sealed class)
// Depends:  VoxrSlotDefinition, VoxrSlotType
// ============================================================================
using System;
using System.Collections.Generic;

namespace VoXR.Commands
{
    internal sealed class DynamicSlotManager
    {
        Dictionary<string, Func<string[]>> _providers;

        internal bool HasProviders => _providers != null && _providers.Count > 0;

        internal void Register(string slotName, Func<string[]> provider)
        {
            if (slotName == null) throw new ArgumentNullException(nameof(slotName));
            if (provider == null) throw new ArgumentNullException(nameof(provider));

            if (_providers == null)
                _providers = new Dictionary<string, Func<string[]>>(StringComparer.Ordinal);

            _providers[slotName] = provider;
        }

        internal bool Unregister(string slotName)
        {
            if (slotName == null) throw new ArgumentNullException(nameof(slotName));
            return _providers != null && _providers.Remove(slotName);
        }

        internal VoxrSlotDefinition[] BuildEffectiveSlots(VoxrSlotDefinition[] baseSlots)
        {
            if (_providers == null || _providers.Count == 0)
                return baseSlots;

            VoxrSlotDefinition[] effective = null;

            for (int i = 0; i < baseSlots.Length; i++)
            {
                var slot = baseSlots[i];

                if (slot.Type == VoxrSlotType.NumberSequence ||
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
                    effective = new VoxrSlotDefinition[baseSlots.Length];
                    Array.Copy(baseSlots, effective, i);
                }

                if (activeValues.Length == 0)
                {
                    effective[i] = new VoxrSlotDefinition(slot.Name, Array.Empty<string>(), null);
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

                effective[i] = new VoxrSlotDefinition(slot.Name, activeValues, filteredAliases);
            }

            return effective ?? baseSlots;
        }
    }
}
