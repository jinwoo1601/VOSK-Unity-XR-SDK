// ============================================================================
// Purpose:  Data types and vocabulary for the pending command state machine
// Layer:    Runtime.Commands
// Owns:     VoxrPendingTimeoutBehavior (public enum), VoxrPendingReason (internal enum), VoxrPendingCommand (internal struct), VoxrFollowUpVocabulary (internal static)
// Depends:  VoxrCommand, VoxrCommandDefinition
// ============================================================================
using System;

namespace VoXR.Commands
{
    public enum VoxrPendingTimeoutBehavior
    {
        Cancel,
        FireAsIs
    }

    internal enum VoxrPendingReason
    {
        PartialMatch,
        AwaitingConfirmation,

        // The recogniser could not tell two sibling commands apart, so it is asking which was
        // meant instead of guessing (issue #74 DR-4). Only reachable with
        // disambiguateSiblingTies set.
        //
        // APPENDED, not inserted. This enum is internal, but nothing stops a serialized or
        // logged ordinal from existing, and appending costs nothing while inserting would
        // silently renumber the two values above.
        AwaitingDisambiguation,
    }

    internal struct VoxrPendingCommand
    {
        public VoxrCommand Command;
        public VoxrCommandDefinition Definition;
        public string[] UnfilledSlots;
        public VoxrPendingReason Reason;
        public float CreatedTime;

        // AwaitingDisambiguation only; null under the other two reasons. Three parallel arrays:
        // saying ChoiceValues[i] fires Choices[i], whose definition is ChoiceDefinitions[i].
        //
        // Index 0 is always the candidate that would have fired with the flag off, so the order
        // an integrator renders is registration order — stable across runs, which the tests that
        // assert on it and the prompt a speaker reads both need.
        //
        // Arrays rather than one array of a triple: they are handed out to public code (Choices
        // and ChoiceValues become VoxrPendingAmbiguity) and a triple would either leak
        // VoxrCommandDefinition — internal — or need a second projection.
        public VoxrCommand[] Choices;
        public string[] ChoiceValues;
        public VoxrCommandDefinition[] ChoiceDefinitions;

        // The sibling set held more values than the runtime offers as choices, so the ones past
        // the cap are reachable only by saying the whole command again.
        public bool ChoicesTruncated;
    }

    internal static class VoxrFollowUpVocabulary
    {
        internal static readonly string[] DefaultConfirm =
            { "confirm", "affirmative", "yes", "go ahead", "do it" };

        internal static readonly string[] DefaultCancel =
            { "cancel", "abort", "negative", "belay that", "never mind" };

        internal static void AddPhraseWords(System.Collections.Generic.HashSet<string> set, string[] phrases)
        {
            foreach (string phrase in phrases)
            {
                foreach (string word in phrase.Split(' '))
                {
                    if (word.Length > 0)
                        set.Add(word);
                }
            }
        }
    }
}
