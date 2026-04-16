// ============================================================================
// Purpose:  ScriptableObject for Inspector-authored slot definitions
// Layer:    Runtime.Commands
// Owns:     VoskSlotAsset (public ScriptableObject), AliasEntry (public struct)
// Depends:  VoskSlotDefinition, VoskSlotType
// ============================================================================
using System;
using System.Collections.Generic;
using UnityEngine;

namespace VoskXR.Commands
{
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
