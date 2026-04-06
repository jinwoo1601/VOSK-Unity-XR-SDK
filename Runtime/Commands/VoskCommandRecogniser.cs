using System;
using System.Collections.Generic;
using UnityEngine;

namespace VoskXR.Commands
{
    /// <summary>
    /// Subscribes to <see cref="VoskSpeechRecogniser.OnResult"/> and parses
    /// recognised speech into structured <see cref="VoskCommand"/> events.
    /// Supports utterance buffering for split commands, sequential command
    /// extraction, and per-intent debounce.
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

        [Tooltip("Time in seconds to wait for additional speech before parsing. " +
                 "Longer values recover split commands but add latency. " +
                 "Set to 0 to disable buffering (v2.2 behaviour).")]
        [SerializeField] float bufferWindow = 1.5f;

        [Tooltip("Minimum seconds between firing the same intent. " +
                 "Prevents duplicate commands from rapid VOSK results. " +
                 "Set to 0 to disable debounce.")]
        [SerializeField] float commandCooldown = 0.3f;

        public event Action<VoskCommand> OnCommandRecognised;
        public event Action<VoskCommand[]> OnCommandsRecognised;
        public event Action<string> OnUnrecognisedSpeech;

        VoskCommandParser _parser;
        string _grammarJson;
        bool _grammarApplied;

        // Utterance buffer state
        readonly List<string> _bufferedTexts = new List<string>();
        readonly List<VoskWord> _bufferedWords = new List<VoskWord>();
        float _lastResultTime;
        bool _bufferActive;

        // Per-intent debounce state
        readonly Dictionary<string, float> _lastFireTime = new Dictionary<string, float>(StringComparer.Ordinal);

        // Command set state
        VoskSlotDefinition[] _slots;
        Dictionary<string, VoskCommandSet> _sets;
        string[] _activeSetNames = Array.Empty<string>();

        /// <summary>Names of the currently active command sets (snapshot copy).</summary>
        public string[] ActiveSetNames => (string[])_activeSetNames.Clone();

        /// <summary>
        /// Builds the command parser from the given slot and command definitions.
        /// If the speech recogniser model is already loaded and free-speech mode is off,
        /// applies the grammar immediately. All commands are active.
        /// </summary>
        public void Configure(VoskSlotDefinition[] slots, VoskCommandDefinition[] commands)
        {
            if (slots == null) throw new ArgumentNullException(nameof(slots));
            if (commands == null) throw new ArgumentNullException(nameof(commands));

            _lastFireTime.Clear();
            _slots = slots;
            _sets = null;
            _activeSetNames = Array.Empty<string>();

            _parser = new VoskCommandParser(slots, commands);
            _grammarJson = _parser.GenerateGrammarJson();
            _grammarApplied = false;

            if (!freeSpeechMode && speechRecogniser != null && speechRecogniser.IsModelReady)
            {
                speechRecogniser.SetGrammar(_grammarJson);
                _grammarApplied = true;
            }
        }

        /// <summary>
        /// Registers shared slots and named command sets. Does not activate any set —
        /// call <see cref="SetActiveSets"/> to activate one or more sets.
        /// </summary>
        public void Configure(VoskSlotDefinition[] slots, VoskCommandSet[] sets)
        {
            if (slots == null) throw new ArgumentNullException(nameof(slots));
            if (sets == null) throw new ArgumentNullException(nameof(sets));

            _lastFireTime.Clear();
            _slots = slots;
            _sets = new Dictionary<string, VoskCommandSet>(sets.Length, StringComparer.Ordinal);

            for (int i = 0; i < sets.Length; i++)
            {
                if (_sets.ContainsKey(sets[i].Name))
                    throw new ArgumentException($"Duplicate command set name: '{sets[i].Name}'.");
                _sets[sets[i].Name] = sets[i];
            }

            _parser = null;
            _grammarJson = null;
            _grammarApplied = false;
            _activeSetNames = Array.Empty<string>();
        }

        /// <summary>
        /// Activates the named command sets. Rebuilds the parser and grammar from
        /// only the commands in the active sets. If recognition is running, performs
        /// stop → set grammar → start.
        /// </summary>
        public void SetActiveSets(params string[] setNames)
        {
            if (_sets == null)
                throw new InvalidOperationException(
                    "Configure(slots, sets) must be called before SetActiveSets().");

            if (setNames == null)
                setNames = Array.Empty<string>();

            for (int i = 0; i < setNames.Length; i++)
            {
                if (!_sets.ContainsKey(setNames[i]))
                    throw new ArgumentException(
                        $"Unknown command set name: '{setNames[i]}'.", nameof(setNames));
            }

            int total = 0;
            for (int i = 0; i < setNames.Length; i++)
                total += _sets[setNames[i]].Commands.Length;

            VoskCommandDefinition[] commands;
            if (total == 0)
            {
                commands = Array.Empty<VoskCommandDefinition>();
            }
            else
            {
                commands = new VoskCommandDefinition[total];
                int offset = 0;
                for (int i = 0; i < setNames.Length; i++)
                {
                    var c = _sets[setNames[i]].Commands;
                    Array.Copy(c, 0, commands, offset, c.Length);
                    offset += c.Length;
                }
            }

            _activeSetNames = setNames.Length > 0
                ? (string[])setNames.Clone()
                : Array.Empty<string>();

            _lastFireTime.Clear();
            RebuildParserAndGrammar(commands);
        }

        /// <summary>
        /// Activates a single command set by name.
        /// </summary>
        public void SetActiveSet(string setName)
        {
            SetActiveSets(setName);
        }

        void RebuildParserAndGrammar(VoskCommandDefinition[] commands)
        {
            // Discard stale buffered speech from the previous grammar
            if (_bufferActive)
            {
                _bufferedTexts.Clear();
                _bufferedWords.Clear();
                _bufferActive = false;
            }

            _parser = new VoskCommandParser(_slots, commands);
            _grammarJson = _parser.GenerateGrammarJson();
            _grammarApplied = false;

            if (freeSpeechMode || speechRecogniser == null || !speechRecogniser.IsModelReady)
                return;

            bool wasRunning = speechRecogniser.IsRecognising;

            if (wasRunning)
                speechRecogniser.StopRecognition();

            speechRecogniser.SetGrammar(_grammarJson);
            _grammarApplied = true;

            if (wasRunning)
                speechRecogniser.StartRecognition();
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

            // Flush any pending buffer on disable
            if (_bufferActive)
                FlushBuffer();
        }

        void Update()
        {
            if (_bufferActive && Time.time - _lastResultTime >= bufferWindow)
                FlushBuffer();
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

            if (bufferWindow <= 0f)
            {
                ProcessParsedResults(result.Text, result.Words);
                return;
            }

            // Append to buffer and reset timer
            _bufferedTexts.Add(result.Text);
            if (result.Words != null && result.Words.Length > 0)
                _bufferedWords.AddRange(result.Words);

            _lastResultTime = Time.time;
            _bufferActive = true;
        }

        void FlushBuffer()
        {
            _bufferActive = false;

            if (_bufferedTexts.Count == 0)
                return;

            string text = string.Join(" ", _bufferedTexts);
            var words = _bufferedWords.Count > 0 ? _bufferedWords.ToArray() : Array.Empty<VoskWord>();

            _bufferedTexts.Clear();
            _bufferedWords.Clear();

            ProcessParsedResults(text, words);
        }

        void ProcessParsedResults(string text, VoskWord[] words)
        {
            var results = _parser.Parse(text, words);

            if (results.Length == 0)
            {
                OnUnrecognisedSpeech?.Invoke(text);
                return;
            }

            float now = Time.time;
            var accepted = new List<VoskCommand>();

            for (int i = 0; i < results.Length; i++)
            {
                var cmd = results[i].Command;

                // Reject if below score threshold
                if (cmd.Score < minScore)
                    continue;

                // Reject if below confidence threshold (skip when word data unavailable, i.e. -1)
                if (cmd.Confidence >= 0f && cmd.Confidence < minConfidence)
                    continue;

                // Per-intent debounce
                if (commandCooldown > 0f &&
                    _lastFireTime.TryGetValue(cmd.Intent, out float lastTime) &&
                    now - lastTime < commandCooldown)
                    continue;

                _lastFireTime[cmd.Intent] = now;
                accepted.Add(cmd);
            }

            if (accepted.Count == 0)
                return;

            // Fire per-command events in order
            for (int i = 0; i < accepted.Count; i++)
                OnCommandRecognised?.Invoke(accepted[i]);

            // Fire batch event
            if (OnCommandsRecognised != null)
                OnCommandsRecognised.Invoke(accepted.ToArray());
        }
    }
}
