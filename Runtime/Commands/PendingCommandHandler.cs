// ============================================================================
// Purpose:  State machine for partial-match follow-up, confirmation, cancellation, and timeout
// Layer:    Runtime.Commands
// Owns:     PendingCommandHandler (internal sealed class), PendingOutcome (internal enum), PendingResolution (internal readonly struct)
// Depends:  VoxrCommand, VoxrCommandDefinition, VoxrPendingCommand, VoxrFollowUpVocabulary, VoxrCommandParser
// ============================================================================
using System;
using System.Collections.Generic;

namespace VoXR.Commands
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
        internal readonly VoxrCommand Command;

        PendingResolution(PendingOutcome outcome, VoxrCommand command)
        {
            Outcome = outcome;
            Command = command;
        }

        internal static PendingResolution Confirmed(VoxrCommand cmd)
            => new PendingResolution(PendingOutcome.Confirmed, cmd);

        internal static PendingResolution Cancelled(VoxrCommand cmd)
            => new PendingResolution(PendingOutcome.Cancelled, cmd);

        internal static PendingResolution Entered(VoxrCommand cmd)
            => new PendingResolution(PendingOutcome.Entered, cmd);

        internal static PendingResolution ReEntered(VoxrCommand cmd)
            => new PendingResolution(PendingOutcome.ReEnteredPending, cmd);

        internal static PendingResolution NoAction()
            => new PendingResolution(PendingOutcome.None, default);
    }

    internal sealed class PendingCommandHandler
    {
        VoxrPendingCommand? _pendingCommand;

        readonly List<VoxrSlotMatch> _followUpSlotBuf = new List<VoxrSlotMatch>();
        readonly List<string> _unfilledBuf = new List<string>();

        internal bool HasPending => _pendingCommand.HasValue;
        internal VoxrPendingCommand? Current => _pendingCommand;
        internal VoxrCommand? PendingCommand => _pendingCommand?.Command;

        internal PendingResolution EnterPending(VoxrCommand command,
            VoxrCommandDefinition definition, string[] unfilledSlots,
            VoxrPendingReason reason, float currentTime,
            out PendingResolution cancelledPrevious)
        {
            cancelledPrevious = _pendingCommand.HasValue
                ? Cancel()
                : PendingResolution.NoAction();

            _pendingCommand = new VoxrPendingCommand
            {
                Command = command,
                Definition = definition,
                UnfilledSlots = unfilledSlots,
                Reason = reason,
                CreatedTime = currentTime,
            };

            return PendingResolution.Entered(command);
        }

        internal PendingResolution TryHandleConfirmCancel(string[] tokens,
            string[] confirmVocab, string[] cancelVocab)
        {
            if (tokens.Length == 0)
                return PendingResolution.NoAction();

            string[] effectiveCancel = cancelVocab != null && cancelVocab.Length > 0
                ? cancelVocab : VoxrFollowUpVocabulary.DefaultCancel;
            string[] effectiveConfirm = confirmVocab != null && confirmVocab.Length > 0
                ? confirmVocab : VoxrFollowUpVocabulary.DefaultConfirm;

            if (IsVocabularyMatchTokens(tokens, effectiveCancel))
                return Cancel();

            if (IsVocabularyMatchTokens(tokens, effectiveConfirm))
            {
                var confirmed = _pendingCommand.Value;
                _pendingCommand = null;
                return PendingResolution.Confirmed(confirmed.Command);
            }

            return PendingResolution.NoAction();
        }

        internal VoxrCommand? TryFollowUpSlotFill(string text, string[] tokens,
            Dictionary<string, float> wordConfidence, VoxrCommandParser parser)
        {
            var pending = _pendingCommand.Value;
            if (pending.UnfilledSlots == null || pending.UnfilledSlots.Length == 0)
                return null;

            if (tokens.Length == 0)
                return null;

            _followUpSlotBuf.Clear();
            var existingSlots = pending.Command.Slots;
            for (int i = 0; i < existingSlots.Length; i++)
                _followUpSlotBuf.Add(existingSlots[i]);

            int tokenIdx = 0;

            foreach (string slotName in pending.UnfilledSlots)
            {
                bool found = false;
                for (int startIdx = tokenIdx; startIdx < tokens.Length; startIdx++)
                {
                    if (tokens[startIdx] == VoxrCommandParser.UnkToken)
                        continue;

                    string value = parser.TryMatchSlotByName(
                        tokens, startIdx, slotName, out int consumed);
                    if (value != null)
                    {
                        _followUpSlotBuf.Add(new VoxrSlotMatch(slotName, value));
                        tokenIdx = startIdx + consumed;
                        found = true;
                        break;
                    }
                }

                if (!found)
                    break;
            }

            // Must have filled at least one new slot
            if (_followUpSlotBuf.Count == existingSlots.Length)
                return null;

            float followUpConf = VoxrCommandParser.ComputeConfidence(
                tokens, 0, tokens.Length, wordConfidence);

            float mergedConfidence = pending.Command.Confidence >= 0f && followUpConf >= 0f
                ? Math.Min(pending.Command.Confidence, followUpConf)
                : pending.Command.Confidence >= 0f ? pending.Command.Confidence : followUpConf;

            // Allocate a fresh array — this command crosses into public events
            // (OnCommandConfirmed/OnCommandRecognised) where subscribers may retain it,
            // so it must not be pool-borrowed.
            int slotCount = _followUpSlotBuf.Count;
            var slotsArray = new VoxrSlotMatch[slotCount];
            for (int i = 0; i < slotCount; i++)
                slotsArray[i] = _followUpSlotBuf[i];

            // Re-score against the matched pattern now that slots are filled.
            // The original partial-match score penalised missing elements; with
            // follow-up data the score should reflect the completed match.
            float mergedScore = parser.ScoreFollowUp(
                pending.Command.Intent, pending.Command.MatchedPatternIndex,
                _followUpSlotBuf);

            return new VoxrCommand(
                pending.Command.Intent,
                slotsArray,
                mergedConfidence,
                mergedScore,
                pending.Command.RawText + " " + text,
                null,
                pending.Command.MatchedPatternIndex);
        }

        // A follow-up that filled some — but not all — of the still-unfilled required slots.
        // TryFollowUpSlotFill stops at the first slot it cannot fill and returns as soon as one
        // new slot is filled, so on a pending with two or more unfilled required slots its result
        // can still be missing an argument (issue #77). That is progress toward the command, not
        // the command: re-arm the pending on what is filled now, so the next utterance continues
        // from here instead of starting over.
        //
        // CreatedTime carries over unchanged, so the pending keeps the single lifetime it was
        // entered with rather than being extended by each fill — the same choice Complete makes
        // when it re-enters for confirmation.
        internal PendingResolution AdvanceSlotFill(VoxrCommand partiallyFilled)
        {
            var pending = _pendingCommand.Value;

            _pendingCommand = new VoxrPendingCommand
            {
                Command = partiallyFilled,
                Definition = pending.Definition,
                UnfilledSlots = ComputeUnfilledSlots(partiallyFilled, pending.Definition),
                Reason = pending.Reason,
                CreatedTime = pending.CreatedTime,
            };

            return PendingResolution.ReEntered(partiallyFilled);
        }

        internal PendingResolution Complete(VoxrCommand completed)
        {
            var pending = _pendingCommand.Value;
            _pendingCommand = null;

            // If the definition also requires confirmation and we were pending
            // for partial match, re-enter pending for confirmation
            if (pending.Definition.RequiresConfirmation &&
                pending.Reason == VoxrPendingReason.PartialMatch)
            {
                _pendingCommand = new VoxrPendingCommand
                {
                    Command = completed,
                    Definition = pending.Definition,
                    UnfilledSlots = Array.Empty<string>(),
                    Reason = VoxrPendingReason.AwaitingConfirmation,
                    CreatedTime = pending.CreatedTime,
                };
                return PendingResolution.ReEntered(completed);
            }

            return PendingResolution.Confirmed(completed);
        }

        internal PendingResolution HandleTimeout(VoxrPendingTimeoutBehavior behavior)
        {
            var pending = _pendingCommand.Value;
            _pendingCommand = null;

            if (behavior == VoxrPendingTimeoutBehavior.FireAsIs)
                return PendingResolution.Confirmed(pending.Command);

            return PendingResolution.Cancelled(pending.Command);
        }

        internal PendingResolution Cancel()
        {
            if (!_pendingCommand.HasValue)
                return PendingResolution.NoAction();

            var cancelled = _pendingCommand.Value;
            _pendingCommand = null;
            return PendingResolution.Cancelled(cancelled.Command);
        }

        internal string[] ComputeUnfilledSlots(VoxrCommand cmd, VoxrCommandDefinition def)
        {
            if (cmd.MatchedPatternIndex < 0 ||
                cmd.MatchedPatternIndex >= def.Patterns.Length)
                return Array.Empty<string>();

            var pattern = def.Patterns[cmd.MatchedPatternIndex];
            _unfilledBuf.Clear();

            foreach (string element in pattern)
            {
                string slotName = VoxrCommandParser.ExtractSlotName(element);
                if (slotName != null && !VoxrCommandParser.IsOptionalSlot(element)
                    && !cmd.HasSlot(slotName))
                {
                    _unfilledBuf.Add(slotName);
                }
            }

            return _unfilledBuf.Count > 0 ? _unfilledBuf.ToArray() : Array.Empty<string>();
        }

        static bool IsVocabularyMatchTokens(string[] tokens, string[] vocabulary)
        {
            for (int v = 0; v < vocabulary.Length; v++)
            {
                if (MatchPhraseAgainstTokens(tokens, vocabulary[v].AsSpan()))
                    return true;
            }
            return false;
        }

        static bool MatchPhraseAgainstTokens(string[] tokens, ReadOnlySpan<char> phrase)
        {
            int tokenIdx = 0;
            int pos = 0;

            while (pos < phrase.Length)
            {
                // Skip spaces.
                if (phrase[pos] == ' ') { pos++; continue; }

                // Find word end.
                int wordStart = pos;
                while (pos < phrase.Length && phrase[pos] != ' ') pos++;

                if (tokenIdx >= tokens.Length)
                    return false;

                // Compare phrase word span against token.
                if (!phrase.Slice(wordStart, pos - wordStart)
                        .SequenceEqual(tokens[tokenIdx].AsSpan()))
                    return false;

                tokenIdx++;
            }

            // All tokens must be consumed.
            return tokenIdx == tokens.Length;
        }

        // Test-only: force-set the pending command state for timeout testing.
        internal void ForceSetForTest(VoxrPendingCommand pending)
        {
            _pendingCommand = pending;
        }
    }
}
