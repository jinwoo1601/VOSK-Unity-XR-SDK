// ============================================================================
// Purpose:  Generates VOSK grammar JSON and coordinates stop/set/start lifecycle
// Layer:    Runtime.Commands
// Owns:     GrammarManager (internal sealed class)
// Depends:  VoskCommandParser, VoskSpeechRecogniser, VoskSlotDefinition, VoskCommandDefinition
// ============================================================================
using System;

namespace VoskXR.Commands
{
    internal sealed class GrammarManager
    {
        internal string CurrentJson { get; private set; }

        internal bool IsApplied { get; set; }

        internal bool GrammarRebuildDeferred { get; set; }

        internal void Rebuild(VoskSlotDefinition[] slots, VoskCommandDefinition[] commands,
            string[] followUpWords)
        {
            CurrentJson = VoskCommandParser.GenerateGrammarJson(slots, commands, followUpWords);
            IsApplied = false;
        }

        internal void ApplyIfReady(VoskSpeechRecogniser recogniser, bool freeSpeechMode)
        {
            if (freeSpeechMode || IsApplied || CurrentJson == null)
                return;

            if (recogniser == null || !recogniser.IsModelReady)
                return;

            recogniser.SetGrammar(CurrentJson);
            IsApplied = true;
        }

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

        internal void Reset()
        {
            CurrentJson = null;
            IsApplied = false;
            GrammarRebuildDeferred = false;
        }
    }
}
