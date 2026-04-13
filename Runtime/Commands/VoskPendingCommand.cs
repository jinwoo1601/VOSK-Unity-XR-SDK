using System;

namespace VoskXR.Commands
{
    /// <summary>
    /// Determines what happens when a pending command's timeout expires.
    /// </summary>
    public enum VoskPendingTimeoutBehavior
    {
        /// <summary>The pending command is cancelled and discarded.</summary>
        Cancel,

        /// <summary>The pending command fires as-is with whatever slots were filled.</summary>
        FireAsIs
    }

    /// <summary>
    /// Why a command entered pending state.
    /// </summary>
    internal enum VoskPendingReason
    {
        /// <summary>Matched with unfilled required slots; awaiting follow-up speech.</summary>
        PartialMatch,

        /// <summary>Fully matched but requires explicit confirmation before firing.</summary>
        AwaitingConfirmation
    }

    /// <summary>
    /// Tracks a single pending command awaiting follow-up or confirmation.
    /// </summary>
    internal struct VoskPendingCommand
    {
        public VoskCommand Command;
        public VoskCommandDefinition Definition;
        public string[] UnfilledSlots;
        public VoskPendingReason Reason;
        public float CreatedTime;
    }

    /// <summary>
    /// Default confirm and cancel vocabulary for pending commands.
    /// Used when the developer does not supply custom vocabulary.
    /// </summary>
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
