using System;
using UnityEngine;

namespace VoskXR.Commands
{
    /// <summary>
    /// ScriptableObject for defining a command in the Inspector.
    /// Create via Assets > Create > VOSK XR > Command Definition.
    /// Patterns are authored as single strings (e.g. "launch {?quantity} {weapon} target {target}")
    /// and split on whitespace by <see cref="ToDefinition"/>.
    /// </summary>
    [CreateAssetMenu(menuName = "VOSK XR/Command Definition")]
    public class VoskCommandAsset : ScriptableObject
    {
        static readonly char[] SplitSeparator = { ' ' };

        public string intent;

        [Tooltip("Each element is one pattern. Tokens separated by spaces. " +
                 "Use {slot} for required slots, {?slot} for optional, ?word for optional literals.")]
        public string[] patterns;

        /// <summary>
        /// Converts this asset to the runtime <see cref="VoskCommandDefinition"/> struct.
        /// Each pattern string is split on whitespace to produce the token array the parser expects.
        /// </summary>
        public VoskCommandDefinition ToDefinition()
        {
            var patternArrays = patterns != null
                ? new string[patterns.Length][]
                : Array.Empty<string[]>();

            for (int i = 0; i < patternArrays.Length; i++)
            {
                patternArrays[i] = string.IsNullOrEmpty(patterns[i])
                    ? Array.Empty<string>()
                    : patterns[i].Split(SplitSeparator, StringSplitOptions.RemoveEmptyEntries);
            }

            return new VoskCommandDefinition(intent, patternArrays);
        }
    }
}
