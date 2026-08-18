// ============================================================================
// Purpose:  Data types for parsed command results (slot match, command, command result)
// Layer:    Runtime.Commands
// Owns:     VoxrSlotMatch, VoxrCommand, VoxrCommandResult (public readonly structs)
// Depends:  (none)
// ============================================================================
using System;

namespace VoXR.Commands
{
    public readonly struct VoxrSlotMatch
    {
        public readonly string Name;

        /// <summary>
        /// The matched value. For a <see cref="VoxrSlotType.NumberSequence"/> slot this is the
        /// number words as spoken ("two seven zero"), never a numeric string ("270") -- convert
        /// with <see cref="VoxrNumberParser"/>. For an <see cref="VoxrSlotType.Enumerated"/> slot
        /// it is the canonical value, so a spoken alias arrives already resolved ("jackals"
        /// yields "jackal").
        /// </summary>
        public readonly string Value;

        public VoxrSlotMatch(string name, string value)
        {
            Name = name;
            Value = value;
        }

        public override string ToString() => $"{Name}={Value}";
    }

    public readonly struct VoxrCommand
    {
        public readonly string Intent;

        public readonly VoxrSlotMatch[] Slots;

        public readonly float Confidence;

        public readonly float Score;

        public readonly string RawText;

        public readonly int MatchedPatternIndex;

        readonly string[] _registeredSlotNames;

        public VoxrCommand(string intent, VoxrSlotMatch[] slots, float confidence, float score,
            string rawText, string[] registeredSlotNames = null, int matchedPatternIndex = -1)
        {
            Intent = intent;
            Slots = slots ?? Array.Empty<VoxrSlotMatch>();
            Confidence = confidence;
            Score = score;
            RawText = rawText;
            MatchedPatternIndex = matchedPatternIndex;
            _registeredSlotNames = registeredSlotNames;
        }

        // A copy carrying a different score (issue #113), for re-arming a pending with a fill
        // whose re-score is not admissible. It lives here rather than at the call site because
        // _registeredSlotNames is private: a rebuild through the public constructor would
        // silently drop it, and GetSlot would stop distinguishing a registered-but-unmatched
        // slot from one the pattern never declared.
        internal VoxrCommand WithScore(float score)
        {
            return new VoxrCommand(Intent, Slots, Confidence, score, RawText,
                _registeredSlotNames, MatchedPatternIndex);
        }

        /// <summary>
        /// Returns the value of a named slot, or an empty string if the slot was not matched.
        /// </summary>
        /// <remarks>
        /// The value's shape depends on the slot type. For a
        /// <see cref="VoxrSlotType.NumberSequence"/> slot it is the number words as spoken --
        /// "orient heading two seven zero" yields <c>"two seven zero"</c>, not <c>"270"</c>, so
        /// <c>int.TryParse</c> on the result always fails silently. Convert with
        /// <see cref="VoxrNumberParser.ParseDigitSequence"/> for digit-by-digit utterances or
        /// <see cref="VoxrNumberParser.ParseCardinal"/> for cardinal phrases ("two hundred"); both
        /// throw <see cref="FormatException"/> on words they do not accept (null or empty returns
        /// <c>0</c>), so the canonical pattern is to try the digit path and fall back to the
        /// cardinal one. See the Command Recognition guide, "NumberSequence Slots", for the full
        /// snippet. For an <see cref="VoxrSlotType.Enumerated"/> slot the value is the canonical
        /// value, with any spoken alias already resolved.
        /// </remarks>
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
                        $"[VoxrCommand] GetSlot(\"{name}\") called but no slot with that name is registered. " +
                        "Check for typos in the slot name.");
                }
            }
#endif

            return string.Empty;
        }

        /// <summary>
        /// Returns true if the named slot was matched in this command. Presence only -- see
        /// <see cref="GetSlot"/> for the value and the shape it takes.
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

    public readonly struct VoxrCommandResult
    {
        public readonly bool IsMatch;

        public readonly VoxrCommand Command;

        public readonly string RawText;

        public VoxrCommandResult(VoxrCommand command)
        {
            IsMatch = true;
            Command = command;
            RawText = command.RawText;
        }

        public VoxrCommandResult(string rawText)
        {
            IsMatch = false;
            Command = default;
            RawText = rawText;
        }
    }
}
