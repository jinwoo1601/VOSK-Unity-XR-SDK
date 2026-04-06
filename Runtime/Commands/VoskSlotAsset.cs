using System;
using System.Collections.Generic;
using UnityEngine;

namespace VoskXR.Commands
{
    /// <summary>
    /// ScriptableObject for defining a slot in the Inspector.
    /// Create via Assets > Create > VOSK XR > Slot Definition.
    /// Use <see cref="ToDefinition"/> to convert to the runtime struct.
    /// </summary>
    [CreateAssetMenu(menuName = "VOSK XR/Slot Definition")]
    public class VoskSlotAsset : ScriptableObject
    {
        [Tooltip("Slot name used in pattern references {slotName}")]
        public string slotName;

        public VoskSlotType slotType = VoskSlotType.Enumerated;

        [Tooltip("Allowed values (Enumerated slots only)")]
        public string[] values;

        [Tooltip("Variant → canonical mappings")]
        public AliasEntry[] aliases;

        [Header("NumberSequence Settings")]
        public int minWords = 1;
        public int maxWords = 3;

        [Serializable]
        public struct AliasEntry
        {
            public string variant;
            public string canonical;
        }

        /// <summary>
        /// Converts this asset to the runtime <see cref="VoskSlotDefinition"/> struct.
        /// </summary>
        public VoskSlotDefinition ToDefinition()
        {
            if (slotType == VoskSlotType.NumberSequence)
                return VoskSlotDefinition.NumberSequence(slotName, minWords, maxWords);

            Dictionary<string, string> aliasDict = null;
            if (aliases != null && aliases.Length > 0)
            {
                aliasDict = new Dictionary<string, string>(aliases.Length, StringComparer.Ordinal);
                for (int i = 0; i < aliases.Length; i++)
                    aliasDict[aliases[i].variant] = aliases[i].canonical;
            }

            return new VoskSlotDefinition(
                slotName,
                values ?? Array.Empty<string>(),
                aliasDict);
        }
    }
}
