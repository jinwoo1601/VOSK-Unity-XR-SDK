using System;

namespace VoskXR.Commands
{
    /// <summary>
    /// A named group of command definitions that can be activated or deactivated
    /// at runtime via <see cref="VoskCommandRecogniser.SetActiveSets"/>.
    /// </summary>
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
