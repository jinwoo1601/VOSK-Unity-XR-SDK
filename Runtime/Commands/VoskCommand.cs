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

        /// <summary>Minimum word confidence across matched tokens. -1 when word data is unavailable.</summary>
        public readonly float Confidence;

        /// <summary>Match quality score (0.0–1.0). Higher is better.</summary>
        public readonly float Score;

        /// <summary>The original VOSK output text.</summary>
        public readonly string RawText;

        /// <summary>
        /// Index into <see cref="VoskCommandDefinition.Patterns"/> identifying which
        /// pattern produced this match. -1 when unavailable (e.g. manually constructed).
        /// </summary>
        public readonly int MatchedPatternIndex;

        /// <summary>Names of all registered slots, used for debug validation in GetSlot. Null in release builds.</summary>
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

        /// <summary>
        /// Returns the value of the named slot, or <see cref="string.Empty"/> if the slot was not matched.
        /// In debug builds, logs a warning if the name doesn't match any registered slot.
        /// </summary>
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

        /// <summary>
        /// Returns true if the named slot was matched.
        /// </summary>
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
