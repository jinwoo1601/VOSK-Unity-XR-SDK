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
    internal sealed class CommandDebouncer
    {
        readonly Dictionary<string, float> _lastFireTime =
            new Dictionary<string, float>(StringComparer.Ordinal);

        internal bool IsOnCooldown(string intent, float currentTime, float cooldownSeconds)
        {
            return _lastFireTime.TryGetValue(intent, out float lastTime)
                && currentTime - lastTime < cooldownSeconds;
        }

        internal void RecordFire(string intent, float currentTime)
        {
            _lastFireTime[intent] = currentTime;
        }

        internal void Clear()
        {
            _lastFireTime.Clear();
        }
    }
}
