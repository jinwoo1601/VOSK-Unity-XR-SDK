// ============================================================================
// Purpose:  Generates VOSK grammar JSON and coordinates stop/set/start lifecycle
// Layer:    Runtime.Commands
// Owns:     GrammarManager (internal sealed class)
// Depends:  VoskCommandParser, VoskSpeechRecogniser, VoskSlotDefinition, VoskCommandDefinition
// ============================================================================
using System;

namespace VoskXR.Commands
{
    /// <summary>
    /// Manages grammar JSON generation, application, and the deferred-rebuild flag.
    /// The recogniser delegates grammar operations here to keep grammar lifecycle
    /// separate from command matching and event dispatch.
    /// </summary>
    internal sealed class GrammarManager
    {
        /// <summary>The current grammar JSON string, or null if not yet generated.</summary>
        internal string CurrentJson { get; private set; }

        /// <summary>True when the grammar has been applied to the speech recogniser.</summary>
        internal bool IsApplied { get; set; }

        /// <summary>
        /// True when a grammar rebuild was requested while a pending command was active.
        /// The recogniser drains this after the pending command resolves.
        /// </summary>
        internal bool GrammarRebuildDeferred { get; set; }

        /// <summary>
        /// Generates grammar JSON from the given slots and commands.
        /// Does not apply it to the speech recogniser.
        /// </summary>
        internal void Rebuild(VoskSlotDefinition[] slots, VoskCommandDefinition[] commands,
            string[] followUpWords)
        {
            CurrentJson = VoskCommandParser.GenerateGrammarJson(slots, commands, followUpWords);
            IsApplied = false;
        }

        /// <summary>
        /// Applies the current grammar if the model is ready and free-speech mode is off.
        /// Used when the model becomes ready after grammar was generated.
        /// </summary>
        internal void ApplyIfReady(VoskSpeechRecogniser recogniser, bool freeSpeechMode)
        {
            if (freeSpeechMode || IsApplied || CurrentJson == null)
                return;

            if (recogniser == null || !recogniser.IsModelReady)
                return;

            recogniser.SetGrammar(CurrentJson);
            IsApplied = true;
        }

        /// <summary>
        /// Performs the stop → set grammar → start cycle. Used when grammar changes
        /// while recognition is running.
        /// </summary>
        internal void ForceApply(VoskSpeechRecogniser recogniser, bool freeSpeechMode)
        {
            if (freeSpeechMode || recogniser == null || !recogniser.IsModelReady)
                return;

            bool wasRunning = recogniser.IsRecognising;

            if (wasRunning)
                recogniser.StopRecognition();

            recogniser.SetGrammar(CurrentJson);
            IsApplied = true;

            if (wasRunning)
                recogniser.StartRecognition();
        }

        /// <summary>
        /// Resets grammar state — clears JSON, applied flag, and deferred flag.
        /// </summary>
        internal void Reset()
        {
            CurrentJson = null;
            IsApplied = false;
            GrammarRebuildDeferred = false;
        }
    }
}
