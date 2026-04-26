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
        AwaitingConfirmation
    }

    internal struct VoxrPendingCommand
    {
        public VoxrCommand Command;
        public VoxrCommandDefinition Definition;
        public string[] UnfilledSlots;
        public VoxrPendingReason Reason;
        public float CreatedTime;
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
