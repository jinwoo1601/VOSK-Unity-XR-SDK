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
    [CreateAssetMenu(menuName = "VOSK XR/Command Set")]
    public class VoskCommandSetAsset : ScriptableObject
    {
        public string setName;
        public VoskCommandAsset[] commands;

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
