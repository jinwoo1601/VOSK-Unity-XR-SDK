// ============================================================================
// Purpose:  ScriptableObject grouping VoxrCommandAssets into a named set
// Layer:    Runtime.Commands
// Owns:     VoxrCommandSetAsset (public ScriptableObject)
// Depends:  VoxrCommandAsset, VoxrCommandSet
// ============================================================================
using System;
using System.Collections.Generic;
using UnityEngine;

namespace VoXR.Commands
{
    [CreateAssetMenu(menuName = "VoXR/Command Set")]
    public class VoxrCommandSetAsset : ScriptableObject
    {
        public string setName;
        public VoxrCommandAsset[] commands;

        public VoxrCommandSet ToSet()
        {
            if (commands == null || commands.Length == 0)
                return new VoxrCommandSet(setName, Array.Empty<VoxrCommandDefinition>());

            var defs = new List<VoxrCommandDefinition>(commands.Length);
            for (int i = 0; i < commands.Length; i++)
            {
                if (commands[i] == null)
                {
                    Debug.LogWarning($"[VoxrCommandSetAsset] '{setName}' commands[{i}] is null — skipping.");
                    continue;
                }
                defs.Add(commands[i].ToDefinition());
            }

            return new VoxrCommandSet(setName, defs.ToArray());
        }
    }
}
