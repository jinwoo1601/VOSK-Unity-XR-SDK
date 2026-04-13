// ============================================================================
// Purpose:  Registers named command sets and aggregates active definitions on activation
// Layer:    Runtime.Commands
// Owns:     CommandSetManager (internal sealed class)
// Depends:  VoskCommandSet, VoskCommandDefinition
// ============================================================================
using System;
using System.Collections.Generic;

namespace VoskXR.Commands
{
    /// <summary>
    /// Manages named command sets and provides the active command definitions
    /// after set activation. Also maintains a lookup from intent name to
    /// definition for partial-match and confirmation checks.
    /// </summary>
    internal sealed class CommandSetManager
    {
        Dictionary<string, VoskCommandSet> _sets;
        string[] _activeSetNames = Array.Empty<string>();
        Dictionary<string, VoskCommandDefinition> _commandLookup;

        /// <summary>Names of the currently active command sets (snapshot copy).</summary>
        internal string[] ActiveSetNames => (string[])_activeSetNames.Clone();

        /// <summary>True when sets have been configured.</summary>
        internal bool HasSets => _sets != null;

        /// <summary>
        /// Registers named command sets. Does not activate any set.
        /// </summary>
        internal void Configure(VoskCommandSet[] sets)
        {
            _sets = new Dictionary<string, VoskCommandSet>(sets.Length, StringComparer.Ordinal);

            for (int i = 0; i < sets.Length; i++)
            {
                if (_sets.ContainsKey(sets[i].Name))
                    throw new ArgumentException($"Duplicate command set name: '{sets[i].Name}'.");
                _sets[sets[i].Name] = sets[i];
            }

            _activeSetNames = Array.Empty<string>();
            _commandLookup = null;
        }

        /// <summary>
        /// Activates the named command sets and returns the aggregated command definitions.
        /// Also rebuilds the intent-to-definition lookup.
        /// </summary>
        internal VoskCommandDefinition[] Activate(params string[] setNames)
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

            VoskCommandDefinition[] commands;
            if (total == 0)
            {
                commands = Array.Empty<VoskCommandDefinition>();
            }
            else
            {
                commands = new VoskCommandDefinition[total];
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

        /// <summary>
        /// Builds or rebuilds the intent-to-definition lookup from a flat command array.
        /// Also used by the flat <c>Configure(slots, commands)</c> path.
        /// </summary>
        internal void BuildLookup(VoskCommandDefinition[] commands)
        {
            if (_commandLookup == null)
                _commandLookup = new Dictionary<string, VoskCommandDefinition>(
                    commands.Length, StringComparer.Ordinal);
            else
                _commandLookup.Clear();

            for (int i = 0; i < commands.Length; i++)
                _commandLookup[commands[i].Intent] = commands[i];
        }

        /// <summary>
        /// Looks up a command definition by intent name.
        /// Returns true if found; false otherwise.
        /// </summary>
        internal bool TryLookupCommand(string intent, out VoskCommandDefinition definition)
        {
            if (_commandLookup != null)
                return _commandLookup.TryGetValue(intent, out definition);

            definition = default;
            return false;
        }

        /// <summary>
        /// Resets all state — sets, active names, and lookup.
        /// </summary>
        internal void Reset()
        {
            _sets = null;
            _activeSetNames = Array.Empty<string>();
            _commandLookup = null;
        }
    }
}
