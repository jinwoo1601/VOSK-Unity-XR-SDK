// ============================================================================
// Purpose:  Per-intent cooldown tracking to prevent duplicate command fires
// Layer:    Runtime.Commands
// Owns:     CommandDebouncer (internal sealed class)
// Depends:  (none)
// ============================================================================
using System;
using System.Collections.Generic;

namespace VoskXR.Commands
{
    /// <summary>
    /// Tracks the last fire time per intent and provides cooldown checks.
    /// </summary>
    internal sealed class CommandDebouncer
    {
        readonly Dictionary<string, float> _lastFireTime =
            new Dictionary<string, float>(StringComparer.Ordinal);

        /// <summary>
        /// Returns true if the intent was fired within the last <paramref name="cooldownSeconds"/>.
        /// </summary>
        internal bool IsOnCooldown(string intent, float currentTime, float cooldownSeconds)
        {
            return _lastFireTime.TryGetValue(intent, out float lastTime)
                && currentTime - lastTime < cooldownSeconds;
        }

        /// <summary>
        /// Records a fire event for the intent at the given time.
        /// </summary>
        internal void RecordFire(string intent, float currentTime)
        {
            _lastFireTime[intent] = currentTime;
        }

        /// <summary>
        /// Clears all recorded fire times.
        /// </summary>
        internal void Clear()
        {
            _lastFireTime.Clear();
        }
    }
}
