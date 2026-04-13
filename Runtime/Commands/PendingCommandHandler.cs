// ============================================================================
// Purpose:  State machine for partial-match follow-up, confirmation, cancellation, and timeout
// Layer:    Runtime.Commands
// Owns:     PendingCommandHandler (internal sealed class), PendingOutcome (internal enum), PendingResolution (internal readonly struct)
// Depends:  VoskCommand, VoskCommandDefinition, VoskPendingCommand, VoskFollowUpVocabulary, VoskCommandParser
// ============================================================================
using System;
using System.Collections.Generic;

namespace VoskXR.Commands
{
    internal enum PendingOutcome
    {
        None,
        Confirmed,
        Cancelled,
        Entered,
        ReEnteredPending,
    }

    internal readonly struct PendingResolution
    {
        internal readonly PendingOutcome Outcome;
        internal readonly VoskCommand Command;

        PendingResolution(PendingOutcome outcome, VoskCommand command)
        {
            Outcome = outcome;
            Command = command;
        }

        internal static PendingResolution Confirmed(VoskCommand cmd)
            => new PendingResolution(PendingOutcome.Confirmed, cmd);

        internal static PendingResolution Cancelled(VoskCommand cmd)
            => new PendingResolution(PendingOutcome.Cancelled, cmd);

        internal static PendingResolution Entered(VoskCommand cmd)
            => new PendingResolution(PendingOutcome.Entered, cmd);

        internal static PendingResolution ReEntered(VoskCommand cmd)
            => new PendingResolution(PendingOutcome.ReEnteredPending, cmd);

        internal static PendingResolution NoAction()
            => new PendingResolution(PendingOutcome.None, default);
    }

    /// <summary>
    /// Manages the pending command state machine. Returns <see cref="PendingResolution"/>
    /// structs that the recogniser interprets to dispatch events, record debounce times,
    /// and drain deferred grammar rebuilds.
    /// </summary>
    internal sealed class PendingCommandHandler
    {
        VoskPendingCommand? _pendingCommand;

        internal bool HasPending => _pendingCommand.HasValue;
        internal VoskPendingCommand? Current => _pendingCommand;
        internal VoskCommand? PendingCommand => _pendingCommand?.Command;

        /// <summary>
        /// Enters pending state for the given command. Cancels any existing pending first.
        /// Returns two resolutions: <paramref name="cancelledPrevious"/> for the displaced
        /// command (may be <c>NoAction</c>), and the return value for the newly entered command.
        /// </summary>
        internal PendingResolution EnterPending(VoskCommand command,
            VoskCommandDefinition definition, string[] unfilledSlots,
            VoskPendingReason reason, float currentTime,
            out PendingResolution cancelledPrevious)
        {
            cancelledPrevious = _pendingCommand.HasValue
                ? Cancel()
                : PendingResolution.NoAction();

            _pendingCommand = new VoskPendingCommand
            {
                Command = command,
                Definition = definition,
                UnfilledSlots = unfilledSlots,
                Reason = reason,
                CreatedTime = currentTime,
            };

            return PendingResolution.Entered(command);
        }

        /// <summary>
        /// Checks if the pre-split tokens match confirm or cancel vocabulary.
        /// Returns a resolution describing what happened.
        /// </summary>
        internal PendingResolution TryHandleConfirmCancel(string[] tokens,
            string[] confirmVocab, string[] cancelVocab)
        {
            if (tokens.Length == 0)
                return PendingResolution.NoAction();

            string normalized = tokens.Length == 1 ? tokens[0] : string.Join(" ", tokens);

            string[] effectiveCancel = cancelVocab != null && cancelVocab.Length > 0
                ? cancelVocab : VoskFollowUpVocabulary.DefaultCancel;
            string[] effectiveConfirm = confirmVocab != null && confirmVocab.Length > 0
                ? confirmVocab : VoskFollowUpVocabulary.DefaultConfirm;

            if (IsVocabularyMatch(normalized, effectiveCancel))
                return Cancel();

            if (IsVocabularyMatch(normalized, effectiveConfirm))
            {
                var confirmed = _pendingCommand.Value;
                _pendingCommand = null;
                return PendingResolution.Confirmed(confirmed.Command);
            }

            return PendingResolution.NoAction();
        }

        /// <summary>
        /// Attempts to fill unfilled slots from follow-up speech.
        /// Returns the completed command if any new slots were filled, null otherwise.
        /// </summary>
        internal VoskCommand? TryFollowUpSlotFill(string text, string[] tokens,
            Dictionary<string, float> wordConfidence, VoskCommandParser parser)
        {
            var pending = _pendingCommand.Value;
            if (pending.UnfilledSlots == null || pending.UnfilledSlots.Length == 0)
                return null;

            if (tokens.Length == 0)
                return null;

            var newSlots = new List<VoskSlotMatch>(pending.Command.Slots);
            int tokenIdx = 0;

            foreach (string slotName in pending.UnfilledSlots)
            {
                bool found = false;
                for (int startIdx = tokenIdx; startIdx < tokens.Length; startIdx++)
                {
                    if (tokens[startIdx] == VoskCommandParser.UnkToken)
                        continue;

                    string value = parser.TryMatchSlotByName(
                        tokens, startIdx, slotName, out int consumed);
                    if (value != null)
                    {
                        newSlots.Add(new VoskSlotMatch(slotName, value));
                        tokenIdx = startIdx + consumed;
                        found = true;
                        break;
                    }
                }

                if (!found)
                    break;
            }

            // Must have filled at least one new slot
            if (newSlots.Count == pending.Command.Slots.Length)
                return null;

            float followUpConf = VoskCommandParser.ComputeConfidence(
                tokens, 0, tokens.Length, wordConfidence);

            float mergedConfidence = pending.Command.Confidence >= 0f && followUpConf >= 0f
                ? Math.Min(pending.Command.Confidence, followUpConf)
                : pending.Command.Confidence >= 0f ? pending.Command.Confidence : followUpConf;

            return new VoskCommand(
                pending.Command.Intent,
                newSlots.ToArray(),
                mergedConfidence,
                pending.Command.Score,
                pending.Command.RawText + " " + text,
                null,
                pending.Command.MatchedPatternIndex);
        }

        /// <summary>
        /// Completes a pending command. Handles re-entry for partial-match commands
        /// that also require confirmation.
        /// </summary>
        internal PendingResolution Complete(VoskCommand completed)
        {
            var pending = _pendingCommand.Value;
            _pendingCommand = null;

            // If the definition also requires confirmation and we were pending
            // for partial match, re-enter pending for confirmation
            if (pending.Definition.RequiresConfirmation &&
                pending.Reason == VoskPendingReason.PartialMatch)
            {
                _pendingCommand = new VoskPendingCommand
                {
                    Command = completed,
                    Definition = pending.Definition,
                    UnfilledSlots = Array.Empty<string>(),
                    Reason = VoskPendingReason.AwaitingConfirmation,
                    CreatedTime = pending.CreatedTime,
                };
                return PendingResolution.ReEntered(completed);
            }

            return PendingResolution.Confirmed(completed);
        }

        /// <summary>
        /// Handles pending timeout based on the configured behavior.
        /// </summary>
        internal PendingResolution HandleTimeout(VoskPendingTimeoutBehavior behavior)
        {
            var pending = _pendingCommand.Value;
            _pendingCommand = null;

            if (behavior == VoskPendingTimeoutBehavior.FireAsIs)
                return PendingResolution.Confirmed(pending.Command);

            return PendingResolution.Cancelled(pending.Command);
        }

        /// <summary>
        /// Cancels the pending command if one is active.
        /// </summary>
        internal PendingResolution Cancel()
        {
            if (!_pendingCommand.HasValue)
                return PendingResolution.NoAction();

            var cancelled = _pendingCommand.Value;
            _pendingCommand = null;
            return PendingResolution.Cancelled(cancelled.Command);
        }

        /// <summary>
        /// Computes which required slots in the matched pattern are unfilled.
        /// </summary>
        internal static string[] ComputeUnfilledSlots(VoskCommand cmd, VoskCommandDefinition def)
        {
            if (cmd.MatchedPatternIndex < 0 ||
                cmd.MatchedPatternIndex >= def.Patterns.Length)
                return Array.Empty<string>();

            var pattern = def.Patterns[cmd.MatchedPatternIndex];
            List<string> unfilled = null;

            foreach (string element in pattern)
            {
                string slotName = VoskCommandParser.ExtractSlotName(element);
                if (slotName != null && !VoskCommandParser.IsOptionalSlot(element)
                    && !cmd.HasSlot(slotName))
                {
                    if (unfilled == null)
                        unfilled = new List<string>();
                    unfilled.Add(slotName);
                }
            }

            return unfilled?.ToArray() ?? Array.Empty<string>();
        }

        static bool IsVocabularyMatch(string normalized, string[] vocabulary)
        {
            for (int i = 0; i < vocabulary.Length; i++)
            {
                if (string.Equals(normalized, vocabulary[i], StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        // Test-only: force-set the pending command state for timeout testing.
        internal void ForceSetForTest(VoskPendingCommand pending)
        {
            _pendingCommand = pending;
        }
    }
}
