// ============================================================================
// Purpose:  ScriptableObject grouping VoskCommandAssets into a named set
// Layer:    Runtime.Commands
// Owns:     VoskCommandSetAsset (public ScriptableObject)
// Depends:  VoskCommandAsset, VoskCommandSet
// ============================================================================
using System;
using System.Collections.Generic;
using UnityEngine;

namespace VoskXR.Commands
{
    /// <summary>
    /// ScriptableObject for grouping commands into a named set.
    /// Create via Assets > Create > VOSK XR > Command Set.
    /// Use <see cref="ToSet"/> to convert to the runtime struct.
    /// </summary>
    [CreateAssetMenu(menuName = "VOSK XR/Command Set")]
    public class VoskCommandSetAsset : ScriptableObject
    {
        public string setName;
        public VoskCommandAsset[] commands;

        /// <summary>
        /// Converts this asset to the runtime <see cref="VoskCommandSet"/> struct.
        /// Null entries in the commands array are skipped with a warning.
        /// </summary>
        public VoskCommandSet ToSet()
        {
            if (commands == null || commands.Length == 0)
                return new VoskCommandSet(setName, Array.Empty<VoskCommandDefinition>());

            var defs = new List<VoskCommandDefinition>(commands.Length);
            for (int i = 0; i < commands.Length; i++)
            {
                if (commands[i] == null)
                {
                    Debug.LogWarning($"[VoskCommandSetAsset] '{setName}' commands[{i}] is null — skipping.");
                    continue;
                }
                defs.Add(commands[i].ToDefinition());
            }

            return new VoskCommandSet(setName, defs.ToArray());
        }
    }
}
