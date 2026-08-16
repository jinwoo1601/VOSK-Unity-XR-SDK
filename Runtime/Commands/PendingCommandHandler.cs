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

        // The three choice arrays are optional trailing parameters, so the two call sites that
        // enter a PartialMatch or AwaitingConfirmation pending are unchanged.
        internal PendingResolution EnterPending(VoxrCommand command,
            VoxrCommandDefinition definition, string[] unfilledSlots,
            VoxrPendingReason reason, float currentTime,
            out PendingResolution cancelledPrevious,
            VoxrCommand[] choices = null,
            string[] choiceValues = null,
            VoxrCommandDefinition[] choiceDefinitions = null,
            bool choicesTruncated = false
        )
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
                Choices = choices,
                ChoiceValues = choiceValues,
                ChoiceDefinitions = choiceDefinitions,
                ChoicesTruncated = choicesTruncated,
            };

            return PendingResolution.Entered(command);
        }

        // currentTime is used only on the disambiguation path, where answering a choice whose
        // intent requires confirmation re-enters pending and that re-entry needs a fresh clock.
        internal PendingResolution TryHandleConfirmCancel(string[] tokens,
            string[] confirmVocab,
            string[] cancelVocab,
            float currentTime
        )
        {
            if (tokens.Length == 0)
                return PendingResolution.NoAction();

            // Guarded like Cancel() below, because the deref moved out of the confirm branch
            // when the choice arm landed: the sole caller checks HasPending first, so this is
            // latent robustness for a second caller rather than a live path.
            if (!_pendingCommand.HasValue)
                return PendingResolution.NoAction();

            string[] effectiveCancel = cancelVocab != null && cancelVocab.Length > 0
                ? cancelVocab : VoxrFollowUpVocabulary.DefaultCancel;
            string[] effectiveConfirm = confirmVocab != null && confirmVocab.Length > 0
                ? confirmVocab : VoxrFollowUpVocabulary.DefaultConfirm;

            // Cancel first, under every reason. That order is what gives design §5.5's
            // collision its direction — a discriminating value that IS a cancel word cancels
            // rather than choosing, safety wins, and the author was told at construction.
            if (IsVocabularyMatchTokens(tokens, effectiveCancel))
                return Cancel();

            var pending = _pendingCommand.Value;
            if (pending.Reason == VoxrPendingReason.AwaitingDisambiguation)
            {
                // The discriminating values are the choice vocabulary (DR-4), matched through
                // the same whole-utterance matcher confirm and cancel use — so "set alpha mode
                // on" is NOT read as the bare choice "mode". That utterance is a full
                // re-utterance and belongs to the parse path, which preempts this pending before
                // the follow-up ever runs.
                if (pending.ChoiceValues != null)
                {
                    for (int i = 0; i < pending.ChoiceValues.Length; i++)
                    {
                        // The single-phrase primitive directly — IsVocabularyMatchTokens is
                        // just a loop over it, and the choices have to be tried one at a time
                        // so the index of the match is known.
                        if (!MatchPhraseAgainstTokens(tokens, pending.ChoiceValues[i].AsSpan()))
                            continue;

                        // Through the ordinary Complete path rather than a new one — that is
                        // DR-4's dividend, and it is what sequences "which?" before "are you
                        // sure?". Complete reads _pendingCommand itself, so it is not cleared
                        // here.
                        return Complete(
                            pending.Choices[i],
                            pending.ChoiceDefinitions[i],
                            currentTime
                        );
                    }
                }

                // Confirm is inert here, and deliberately NOT a cancel. "Yes" is not an answer
                // to "which?", but it is not an instruction to abandon either — leaving the
                // pending live lets the speaker follow it with the actual answer inside the same
                // timeout window.
                return PendingResolution.NoAction();
            }

            if (IsVocabularyMatchTokens(tokens, effectiveConfirm))
            {
                _pendingCommand = null;
                return PendingResolution.Confirmed(pending.Command);
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
        // The timeout clock restarts on the fill. pendingTimeout measures how long the command
        // waits for the speaker, and a speaker who just answered is not the one it exists to give
        // up on — an absolute window from entry would cut off a conversation mid-answer, since the
        // default 5 s has to cover every utterance of a multi-slot exchange plus a bufferWindow
        // each. What still bounds the pending is silence: each fill buys one more window, and the
        // first one nobody answers ends it.
        internal PendingResolution AdvanceSlotFill(VoxrCommand partiallyFilled, float currentTime)
        {
            var pending = _pendingCommand.Value;

            _pendingCommand = new VoxrPendingCommand
            {
                Command = partiallyFilled,
                Definition = pending.Definition,
                UnfilledSlots = ComputeUnfilledSlots(partiallyFilled, pending.Definition),
                Reason = pending.Reason,
                CreatedTime = currentTime,

                // Carried, though an AwaitingDisambiguation pending cannot reach here today: it
                // always has UnfilledSlots empty (a command missing a required argument is
                // routed to PartialMatch by issue #73's gate before the fire path, so anything
                // that reached the tie was complete), and TryFollowUpSlotFill returns null on an
                // empty list. Copying Reason forward while dropping these would produce a
                // pending claiming to be a disambiguation with no choices — which the choice arm
                // would dereference. Three assignments to keep the argument true if either end
                // changes.
                Choices = pending.Choices,
                ChoiceValues = pending.ChoiceValues,
                ChoiceDefinitions = pending.ChoiceDefinitions,
                ChoicesTruncated = pending.ChoicesTruncated,
            };

            return PendingResolution.ReEntered(partiallyFilled);
        }

        // resolvedDefinition is the definition of the command actually being completed, which is
        // not always pending.Definition. Under AwaitingDisambiguation the pending carries the
        // WINNER's definition while the speaker may have chosen a rival, so reading
        // pending.Definition here would take the confirmation decision from the wrong command: a
        // destructive rival marked requiresConfirmation would fire without asking, or a benign
        // one would be gratuitously confirmed because the winner required it. The two existing
        // callers pass pending.Definition and are unchanged in behaviour.
        internal PendingResolution Complete(
            VoxrCommand completed,
            VoxrCommandDefinition resolvedDefinition,
            float currentTime
        )
        {
            var pending = _pendingCommand.Value;
            _pendingCommand = null;

            // If the resolved definition also requires confirmation, and we were not ALREADY
            // awaiting one, re-enter pending for confirmation. Written as "not already
            // confirming" rather than "was a partial match" so the third reason is covered:
            // you cannot confirm an intent you have not identified, so "which?" comes first and
            // "are you sure?" follows.
            if (
                resolvedDefinition.RequiresConfirmation
                && pending.Reason != VoxrPendingReason.AwaitingConfirmation
            )
            {
                _pendingCommand = new VoxrPendingCommand
                {
                    Command = completed,
                    Definition = resolvedDefinition,
                    UnfilledSlots = Array.Empty<string>(),
                    Reason = VoxrPendingReason.AwaitingConfirmation,
                    // Restarts for the same reason the fill path does: confirmation is a fresh
                    // question, and the speaker who just finished filling the slots should not
                    // have less time to answer it than one whose command arrived complete.
                    CreatedTime = currentTime,
                };
                return PendingResolution.ReEntered(completed);
            }

            return PendingResolution.Confirmed(completed);
        }

        internal PendingResolution HandleTimeout(VoxrPendingTimeoutBehavior behavior)
        {
            var pending = _pendingCommand.Value;
            _pendingCommand = null;

            // DR-6: under ambiguity FireAsIs degrades to Cancel, and the argument is semantic
            // rather than a preference. FireAsIs means "the intent is known, fire it with the
            // slots I have" — under ambiguity the INTENT itself is unknown, which is a different
            // situation wearing the same flag. Firing the first-registered after a pause coin-
            // flips anyway, merely later, and that is incoherent with an integrator who opted in
            // specifically to stop coin-flipping.
            //
            // No third value is added to the public VoxrPendingTimeoutBehavior; that was
            // rejected at DR-6 as public API for an edge case, and can still be added later
            // without breaking anything.
            if (
                behavior == VoxrPendingTimeoutBehavior.FireAsIs
                && pending.Reason != VoxrPendingReason.AwaitingDisambiguation
            )
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
