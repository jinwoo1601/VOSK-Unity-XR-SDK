// ============================================================================
// Purpose:  Read-only view of a pending sibling-tie ambiguity, for wording the prompt
// Layer:    Runtime.Commands
// Owns:     VoxrPendingAmbiguity (public readonly struct)
// Depends:  VoxrCommand
// ============================================================================
namespace VoXR.Commands
{
    /// <summary>
    /// What the recogniser is asking about when a pending command is a sibling-tie
    /// disambiguation rather than a confirmation (issue #74). Read through
    /// <see cref="VoxrCommandRecogniser.PendingAmbiguity"/> from an
    /// <c>OnCommandPending</c> handler.
    /// </summary>
    /// <remarks>
    /// Without this the opt-in would be unusable. <c>OnCommandPending</c> is
    /// <c>Action&lt;VoxrCommand&gt;</c> and carries no reason, so an integrator already
    /// subscribed for <c>requiresConfirmation</c> would prompt "yes/no" — and "yes" does
    /// nothing under a disambiguation, so the pending would sit until it timed out and then
    /// fire nothing at all. Checking <c>PendingAmbiguity.HasValue</c> separates the two
    /// questions; reading this tells you how to word the one you got.
    ///
    /// This package ships no speech synthesis and no UI: wording and presenting the question
    /// is the integrator's job.
    /// </remarks>
    public readonly struct VoxrPendingAmbiguity
    {
        /// <summary>
        /// The commands the recogniser could not tell apart. <c>Choices[i]</c> is what fires if
        /// the speaker says <c>DiscriminatingValues[i]</c>.
        /// </summary>
        /// <remarks>
        /// Index 0 is the candidate that would have fired with <c>disambiguateSiblingTies</c>
        /// off. Beyond that the order is registration order and stable across runs — enough for
        /// a prompt that does not reorder itself between sessions, and deliberately not a
        /// ranking: these candidates tied on every key the selector has.
        /// </remarks>
        public readonly VoxrCommand[] Choices;

        /// <summary>
        /// The one word that tells each choice apart from the others — what the speaker says to
        /// pick it. Already in the decoder's grammar, because these are pattern literals.
        /// </summary>
        /// <remarks>
        /// Matched as a whole utterance, so saying more than the word alone is a re-utterance
        /// rather than an answer. A value that is also cancel vocabulary cancels instead of
        /// choosing — cancel keeps precedence, and the grammar author is warned about that
        /// collision at construction.
        /// </remarks>
        public readonly string[] DiscriminatingValues;

        /// <summary>
        /// True when the sibling set held more values than the runtime offers as choices, so the
        /// remaining intents are reachable only by saying the whole command again. Worth
        /// mentioning in the prompt when set.
        /// </summary>
        public readonly bool IsTruncated;

        internal VoxrPendingAmbiguity(
            VoxrCommand[] choices,
            string[] discriminatingValues,
            bool isTruncated
        )
        {
            Choices = choices;
            DiscriminatingValues = discriminatingValues;
            IsTruncated = isTruncated;
        }
    }
}
