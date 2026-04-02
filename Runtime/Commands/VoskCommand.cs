using System;

namespace VoskXR.Commands
{
    /// <summary>
    /// A single matched slot within a parsed command.
    /// </summary>
    public readonly struct VoskSlotMatch
    {
        /// <summary>The slot name, e.g. "weapon".</summary>
        public readonly string Name;

        /// <summary>The matched value, e.g. "missiles".</summary>
        public readonly string Value;

        public VoskSlotMatch(string name, string value)
        {
            Name = name;
            Value = value;
        }

        public override string ToString() => $"{Name}={Value}";
    }

    /// <summary>
    /// A successfully parsed command with its intent, matched slots, and confidence.
    /// </summary>
    public readonly struct VoskCommand
    {
        /// <summary>The matched command intent, e.g. "launch_weapon".</summary>
        public readonly string Intent;

        /// <summary>Matched slot name/value pairs.</summary>
        public readonly VoskSlotMatch[] Slots;

        /// <summary>Minimum word confidence across matched tokens. 0 when word data is unavailable.</summary>
        public readonly float Confidence;

        /// <summary>The original VOSK output text.</summary>
        public readonly string RawText;

        public VoskCommand(string intent, VoskSlotMatch[] slots, float confidence, string rawText)
        {
            Intent = intent;
            Slots = slots ?? Array.Empty<VoskSlotMatch>();
            Confidence = confidence;
            RawText = rawText;
        }

        /// <summary>
        /// Returns the value of the named slot, or <see cref="string.Empty"/> if the slot was not matched.
        /// </summary>
        public string GetSlot(string name)
        {
            for (int i = 0; i < Slots.Length; i++)
            {
                if (string.Equals(Slots[i].Name, name, StringComparison.Ordinal))
                    return Slots[i].Value;
            }

            return string.Empty;
        }

        /// <summary>
        /// Returns true if the named slot was matched.
        /// </summary>
        public bool HasSlot(string name)
        {
            for (int i = 0; i < Slots.Length; i++)
            {
                if (string.Equals(Slots[i].Name, name, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        public override string ToString() => $"{Intent} ({Slots.Length} slots)";
    }

    /// <summary>
    /// Wraps a command parse attempt. Check <see cref="IsMatch"/> before accessing <see cref="Command"/>.
    /// </summary>
    public readonly struct VoskCommandResult
    {
        /// <summary>True if a command pattern matched the input.</summary>
        public readonly bool IsMatch;

        /// <summary>The parsed command. Only valid when <see cref="IsMatch"/> is true.</summary>
        public readonly VoskCommand Command;

        /// <summary>The original VOSK output text.</summary>
        public readonly string RawText;

        /// <summary>Creates a successful match result.</summary>
        public VoskCommandResult(VoskCommand command)
        {
            IsMatch = true;
            Command = command;
            RawText = command.RawText;
        }

        /// <summary>Creates a no-match result.</summary>
        public VoskCommandResult(string rawText)
        {
            IsMatch = false;
            Command = default;
            RawText = rawText;
        }
    }
}
