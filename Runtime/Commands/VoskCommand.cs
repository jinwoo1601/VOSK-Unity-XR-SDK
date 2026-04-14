// ============================================================================
// Purpose:  Data types for parsed command results (slot match, command, command result)
// Layer:    Runtime.Commands
// Owns:     VoskSlotMatch, VoskCommand, VoskCommandResult (public readonly structs)
// Depends:  (none)
// ============================================================================
using System;

namespace VoskXR.Commands
{
    public readonly struct VoskSlotMatch
    {
        public readonly string Name;

        public readonly string Value;

        public VoskSlotMatch(string name, string value)
        {
            Name = name;
            Value = value;
        }

        public override string ToString() => $"{Name}={Value}";
    }

    public readonly struct VoskCommand
    {
        public readonly string Intent;

        public readonly VoskSlotMatch[] Slots;

        public readonly float Confidence;

        public readonly float Score;

        public readonly string RawText;

        public readonly int MatchedPatternIndex;

        readonly string[] _registeredSlotNames;

        public VoskCommand(string intent, VoskSlotMatch[] slots, float confidence, float score,
            string rawText, string[] registeredSlotNames = null, int matchedPatternIndex = -1)
        {
            Intent = intent;
            Slots = slots ?? Array.Empty<VoskSlotMatch>();
            Confidence = confidence;
            Score = score;
            RawText = rawText;
            MatchedPatternIndex = matchedPatternIndex;
            _registeredSlotNames = registeredSlotNames;
        }

        public string GetSlot(string name)
        {
            int idx = FindSlotIndex(name);
            if (idx >= 0)
                return Slots[idx].Value;

#if DEBUG
            if (_registeredSlotNames != null)
            {
                bool registered = false;
                for (int i = 0; i < _registeredSlotNames.Length; i++)
                {
                    if (string.Equals(_registeredSlotNames[i], name, StringComparison.Ordinal))
                    {
                        registered = true;
                        break;
                    }
                }
                if (!registered)
                {
                    UnityEngine.Debug.LogWarning(
                        $"[VoskCommand] GetSlot(\"{name}\") called but no slot with that name is registered. " +
                        "Check for typos in the slot name.");
                }
            }
#endif

            return string.Empty;
        }

        public bool HasSlot(string name) => FindSlotIndex(name) >= 0;

        int FindSlotIndex(string name)
        {
            for (int i = 0; i < Slots.Length; i++)
            {
                if (string.Equals(Slots[i].Name, name, StringComparison.Ordinal))
                    return i;
            }
            return -1;
        }

        public override string ToString() => $"{Intent} ({Slots.Length} slots, score={Score:F2})";
    }

    public readonly struct VoskCommandResult
    {
        public readonly bool IsMatch;

        public readonly VoskCommand Command;

        public readonly string RawText;

        public VoskCommandResult(VoskCommand command)
        {
            IsMatch = true;
            Command = command;
            RawText = command.RawText;
        }

        public VoskCommandResult(string rawText)
        {
            IsMatch = false;
            Command = default;
            RawText = rawText;
        }
    }
}
