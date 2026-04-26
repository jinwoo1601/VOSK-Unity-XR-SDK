// ============================================================================
// Purpose:  ScriptableObject for Inspector-authored slot definitions
// Layer:    Runtime.Commands
// Owns:     VoxrSlotAsset (public ScriptableObject), AliasEntry (public struct)
// Depends:  VoxrSlotDefinition, VoxrSlotType
// ============================================================================
using System;
using System.Collections.Generic;
using UnityEngine;

namespace VoXR.Commands
{
    [CreateAssetMenu(menuName = "VoXR/Slot Definition")]
    public class VoxrSlotAsset : ScriptableObject
    {
        [Tooltip("Slot name used in pattern references {slotName}")]
        public string slotName;

        public VoxrSlotType slotType = VoxrSlotType.Enumerated;

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

        public VoxrSlotDefinition ToDefinition()
        {
            if (slotType == VoxrSlotType.NumberSequence)
                return VoxrSlotDefinition.NumberSequence(slotName, minWords, maxWords);

            Dictionary<string, string> aliasDict = null;
            if (aliases != null && aliases.Length > 0)
            {
                aliasDict = new Dictionary<string, string>(aliases.Length, StringComparer.Ordinal);
                for (int i = 0; i < aliases.Length; i++)
                    aliasDict[aliases[i].variant] = aliases[i].canonical;
            }

            return new VoxrSlotDefinition(
                slotName,
                values ?? Array.Empty<string>(),
                aliasDict);
        }
    }
}
