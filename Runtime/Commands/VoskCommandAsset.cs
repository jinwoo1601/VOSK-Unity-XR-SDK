// ============================================================================
// Purpose:  ScriptableObject for Inspector-authored command definitions
// Layer:    Runtime.Commands
// Owns:     VoskCommandAsset (public ScriptableObject)
// Depends:  VoskCommandDefinition, VoskCommandParser (SplitSeparator)
// ============================================================================
using System;
using UnityEngine;

namespace VoskXR.Commands
{
    [CreateAssetMenu(menuName = "VOSK XR/Command Definition")]
    public class VoskCommandAsset : ScriptableObject
    {
        public string intent;

        [Tooltip("Each element is one pattern. Tokens separated by spaces. " +
                 "Use {slot} for required slots, {?slot} for optional, ?word for optional literals.")]
        public string[] patterns;

        [Tooltip("When enabled, this command enters pending state when matched with " +
                 "unfilled required slots, instead of being rejected. Follow-up speech " +
                 "can fill the missing slots.")]
        public bool allowPartialMatch;

        [Tooltip("When enabled, this command enters pending state even when fully matched, " +
                 "requiring explicit confirmation before firing.")]
        public bool requiresConfirmation;

        public VoskCommandDefinition ToDefinition()
        {
            var patternArrays = patterns != null
                ? new string[patterns.Length][]
                : Array.Empty<string[]>();

            for (int i = 0; i < patternArrays.Length; i++)
            {
                patternArrays[i] = string.IsNullOrEmpty(patterns[i])
                    ? Array.Empty<string>()
                    : patterns[i].Split(VoskCommandParser.SplitSeparator, StringSplitOptions.RemoveEmptyEntries);
            }

            return new VoskCommandDefinition(intent, patternArrays,
                allowPartialMatch, requiresConfirmation);
        }
    }
}
