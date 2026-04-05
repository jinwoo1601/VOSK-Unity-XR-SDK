using System;
using UnityEngine;

namespace VoskXR.Commands
{
    /// <summary>
    /// Subscribes to <see cref="VoskSpeechRecogniser.OnResult"/> and parses
    /// recognised speech into structured <see cref="VoskCommand"/> events.
    /// </summary>
    [AddComponentMenu("VOSK XR/Command Recogniser")]
    public class VoskCommandRecogniser : MonoBehaviour
    {
        [SerializeField] VoskSpeechRecogniser speechRecogniser;

        [Tooltip("Bypasses grammar constraints so VOSK recognises freely (like pre-v2.0). " +
                 "Useful for on-device testing to see what VOSK actually hears before " +
                 "grammar constrains it. The parser still runs against the output so you " +
                 "can see what matches and what doesn't. Disable for release builds.")]
        [SerializeField] bool freeSpeechMode = false;

        [Tooltip("Reject commands where the minimum word confidence is below this threshold. " +
                 "Prevents phantom commands from background noise.")]
        [SerializeField] float minConfidence = 0.4f;

        [Tooltip("Reject matches where the pattern score is below this threshold. " +
                 "Prevents partial or garbled matches.")]
        [SerializeField] float minScore = 0.6f;

        public event Action<VoskCommand> OnCommandRecognised;
        public event Action<string> OnUnrecognisedSpeech;

        VoskCommandParser _parser;
        string _grammarJson;
        bool _grammarApplied;

        /// <summary>
        /// Builds the command parser from the given slot and command definitions.
        /// If the speech recogniser model is already loaded and free-speech mode is off,
        /// applies the grammar immediately.
        /// </summary>
        public void Configure(VoskSlotDefinition[] slots, VoskCommandDefinition[] commands)
        {
            _parser = new VoskCommandParser(slots, commands);
            _grammarJson = _parser.GenerateGrammarJson();
            _grammarApplied = false;

            if (!freeSpeechMode && speechRecogniser != null && speechRecogniser.IsModelReady)
            {
                speechRecogniser.SetGrammar(_grammarJson);
                _grammarApplied = true;
            }
        }

        void OnEnable()
        {
            if (speechRecogniser == null)
                return;

            speechRecogniser.OnModelReady += HandleModelReady;
            speechRecogniser.OnResult += HandleResult;

            if (!Debug.isDebugBuild && freeSpeechMode)
            {
                Debug.LogWarning("[VoskCommandRecogniser] Free-speech mode is active in a " +
                    "release build — grammar constraints are disabled.");
            }
        }

        void OnDisable()
        {
            if (speechRecogniser == null)
                return;

            speechRecogniser.OnModelReady -= HandleModelReady;
            speechRecogniser.OnResult -= HandleResult;
        }

        void HandleModelReady()
        {
            if (!freeSpeechMode && !_grammarApplied && _grammarJson != null)
            {
                speechRecogniser.SetGrammar(_grammarJson);
                _grammarApplied = true;
            }
        }

        void HandleResult(VoskResult result)
        {
            if (string.IsNullOrWhiteSpace(result.Text))
                return;

            if (_parser == null)
                return;

            var parsed = _parser.Parse(result.Text, result.Words);

            if (parsed.IsMatch)
            {
                var cmd = parsed.Command;

                // Reject if below score threshold
                if (cmd.Score < minScore)
                {
                    OnUnrecognisedSpeech?.Invoke(parsed.RawText);
                    return;
                }

                // Reject if below confidence threshold (skip when word data unavailable, i.e. -1)
                if (cmd.Confidence >= 0f && cmd.Confidence < minConfidence)
                {
                    OnUnrecognisedSpeech?.Invoke(parsed.RawText);
                    return;
                }

                OnCommandRecognised?.Invoke(cmd);
            }
            else
            {
                OnUnrecognisedSpeech?.Invoke(parsed.RawText);
            }
        }
    }
}
