// ============================================================================
// Purpose:  Named group of command definitions for runtime activation/deactivation
// Layer:    Runtime.Commands
// Owns:     VoskCommandSet (public readonly struct)
// Depends:  VoskCommandDefinition
// ============================================================================
using System;

namespace VoskXR.Commands
{
    public readonly struct VoskCommandSet
    {
        public string Name { get; }
        public VoskCommandDefinition[] Commands { get; }

        public VoskCommandSet(string name, VoskCommandDefinition[] commands)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            if (commands == null) throw new ArgumentNullException(nameof(commands));

            var copy = new VoskCommandDefinition[commands.Length];
            Array.Copy(commands, copy, commands.Length);
            Commands = copy;
        }
    }
}
