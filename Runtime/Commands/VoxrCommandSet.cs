// ============================================================================
// Purpose:  Named group of command definitions for runtime activation/deactivation
// Layer:    Runtime.Commands
// Owns:     VoxrCommandSet (public readonly struct)
// Depends:  VoxrCommandDefinition
// ============================================================================
using System;

namespace VoXR.Commands
{
    public readonly struct VoxrCommandSet
    {
        public string Name { get; }
        public VoxrCommandDefinition[] Commands { get; }

        public VoxrCommandSet(string name, VoxrCommandDefinition[] commands)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            if (commands == null) throw new ArgumentNullException(nameof(commands));

            var copy = new VoxrCommandDefinition[commands.Length];
            Array.Copy(commands, copy, commands.Length);
            Commands = copy;
        }
    }
}
