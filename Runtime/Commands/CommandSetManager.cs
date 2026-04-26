// ============================================================================
// Purpose:  Registers named command sets and aggregates active definitions on activation
// Layer:    Runtime.Commands
// Owns:     CommandSetManager (internal sealed class)
// Depends:  VoxrCommandSet, VoxrCommandDefinition
// ============================================================================
using System;
using System.Collections.Generic;

namespace VoXR.Commands
{
    internal sealed class CommandSetManager
    {
        Dictionary<string, VoxrCommandSet> _sets;
        string[] _activeSetNames = Array.Empty<string>();
        Dictionary<string, VoxrCommandDefinition> _commandLookup;

        internal string[] ActiveSetNames => _activeSetNames;

        internal bool HasSets => _sets != null;

        internal void Configure(VoxrCommandSet[] sets)
        {
            _sets = new Dictionary<string, VoxrCommandSet>(sets.Length, StringComparer.Ordinal);

            for (int i = 0; i < sets.Length; i++)
            {
                if (_sets.ContainsKey(sets[i].Name))
                    throw new ArgumentException($"Duplicate command set name: '{sets[i].Name}'.");
                _sets[sets[i].Name] = sets[i];
            }

            _activeSetNames = Array.Empty<string>();
            _commandLookup = null;
        }

        internal VoxrCommandDefinition[] Activate(params string[] setNames)
        {
            if (_sets == null)
                throw new InvalidOperationException(
                    "Configure(sets) must be called before Activate().");

            if (setNames == null)
                setNames = Array.Empty<string>();

            for (int i = 0; i < setNames.Length; i++)
            {
                if (!_sets.ContainsKey(setNames[i]))
                    throw new ArgumentException(
                        $"Unknown command set name: '{setNames[i]}'.", nameof(setNames));
            }

            int total = 0;
            for (int i = 0; i < setNames.Length; i++)
                total += _sets[setNames[i]].Commands.Length;

            VoxrCommandDefinition[] commands;
            if (total == 0)
            {
                commands = Array.Empty<VoxrCommandDefinition>();
            }
            else
            {
                commands = new VoxrCommandDefinition[total];
                int offset = 0;
                for (int i = 0; i < setNames.Length; i++)
                {
                    var c = _sets[setNames[i]].Commands;
                    Array.Copy(c, 0, commands, offset, c.Length);
                    offset += c.Length;
                }
            }

            _activeSetNames = setNames.Length > 0
                ? (string[])setNames.Clone()
                : Array.Empty<string>();

            BuildLookup(commands);
            return commands;
        }

        internal void BuildLookup(VoxrCommandDefinition[] commands)
        {
            if (_commandLookup == null)
                _commandLookup = new Dictionary<string, VoxrCommandDefinition>(
                    commands.Length, StringComparer.Ordinal);
            else
                _commandLookup.Clear();

            for (int i = 0; i < commands.Length; i++)
                _commandLookup[commands[i].Intent] = commands[i];
        }

        internal bool TryLookupCommand(string intent, out VoxrCommandDefinition definition)
        {
            if (_commandLookup != null)
                return _commandLookup.TryGetValue(intent, out definition);

            definition = default;
            return false;
        }

        internal void Reset()
        {
            _sets = null;
            _activeSetNames = Array.Empty<string>();
            _commandLookup = null;
        }
    }
}
