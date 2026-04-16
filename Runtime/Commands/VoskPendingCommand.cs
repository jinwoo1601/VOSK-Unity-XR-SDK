// ============================================================================
// Purpose:  Data types and vocabulary for the pending command state machine
// Layer:    Runtime.Commands
// Owns:     VoskPendingTimeoutBehavior (public enum), VoskPendingReason (internal enum), VoskPendingCommand (internal struct), VoskFollowUpVocabulary (internal static)
// Depends:  VoskCommand, VoskCommandDefinition
// ============================================================================
using System;

namespace VoskXR.Commands
{
    public enum VoskPendingTimeoutBehavior
    {
        Cancel,
        FireAsIs
    }

    internal enum VoskPendingReason
    {
        PartialMatch,
        AwaitingConfirmation
    }

    internal struct VoskPendingCommand
    {
        public VoskCommand Command;
        public VoskCommandDefinition Definition;
        public string[] UnfilledSlots;
        public VoskPendingReason Reason;
        public float CreatedTime;
    }

    internal static class VoskFollowUpVocabulary
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
