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

        [Header("Inspector Authoring (optional — ignored if Configure() is called from code)")]
        [SerializeField] VoskSlotAsset[] slotAssets;
        [SerializeField] VoskCommandSetAsset[] commandSetAssets;

        [Tooltip("Command sets to activate on startup when using Inspector authoring.")]
        [SerializeField] string[] initialActiveSetNames;

        [Header("Follow-Up / Pending Commands")]
        [Tooltip("Maximum seconds a pending command waits for follow-up speech before timing out.")]
        [SerializeField] float pendingTimeout = 5.0f;

        [Tooltip("What happens when a pending command times out.")]
        [SerializeField] VoskPendingTimeoutBehavior pendingTimeoutBehavior = VoskPendingTimeoutBehavior.Cancel;

        [Tooltip("Phrases that confirm a pending command. " +
                 "Leave empty to use defaults (confirm, affirmative, yes, go ahead, do it).")]
        [SerializeField] string[] confirmVocabulary;

        [Tooltip("Phrases that cancel a pending command. " +
                 "Leave empty to use defaults (cancel, abort, negative, belay that, never mind).")]
        [SerializeField] string[] cancelVocabulary;

        public event Action<VoskCommand> OnCommandRecognised;
        public event Action<VoskCommand[]> OnCommandsRecognised;
        public event Action<string> OnUnrecognisedSpeech;

        /// <summary>Fires when a command enters pending state (partial match or awaiting confirmation).</summary>
        public event Action<VoskCommand> OnCommandPending;

        /// <summary>Fires when a pending command is confirmed (by follow-up speech or explicit confirmation).</summary>
        public event Action<VoskCommand> OnCommandConfirmed;

        /// <summary>Fires when a pending command is cancelled (timeout, explicit cancel, or preempted by a new command).</summary>
        public event Action<VoskCommand> OnCommandCancelled;

#if UNITY_EDITOR
        /// <summary>
        /// Diagnostic snapshot of the last utterance processed through the command pipeline.
        /// The debug window polls this via the <see cref="VoskMatchDiagnostics.Frame"/> field.
        /// </summary>
        internal VoskMatchDiagnostics LastMatchDiagnostics { get; private set; }

        /// <summary>Last partial result text from VOSK (updates as the user speaks).</summary>
        internal string LastPartialResult { get; private set; }
#endif

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
        VoskCommandDefinition[] _activeCommands;
        Dictionary<string, Func<string[]>> _valueProviders;
        Dictionary<string, VoskCommandDefinition> _commandLookup;

        // Pending command state
        VoskPendingCommand? _pendingCommand;
        bool _grammarRebuildDeferred;

        /// <summary>Names of the currently active command sets (snapshot copy).</summary>
        public string[] ActiveSetNames => (string[])_activeSetNames.Clone();

        /// <summary>True if a command is currently in pending state.</summary>
        public bool HasPendingCommand => _pendingCommand.HasValue;

        /// <summary>The currently pending command, or null if none.</summary>
        public VoskCommand? PendingCommand => _pendingCommand?.Command;

        /// <summary>
        /// Builds the command parser from the given slot and command definitions.
        /// If the speech recogniser model is already loaded and free-speech mode is off,
        /// applies the grammar immediately. All commands are active.
        /// </summary>
        public void Configure(VoskSlotDefinition[] slots, VoskCommandDefinition[] commands)
        {
            if (slots == null) throw new ArgumentNullException(nameof(slots));
            if (commands == null) throw new ArgumentNullException(nameof(commands));

            CancelPendingIfActive();

            _lastFireTime.Clear();
            _slots = slots;
            _sets = null;
            _activeSetNames = Array.Empty<string>();
            _activeCommands = commands;
            BuildCommandLookup(commands);

            _parser = new VoskCommandParser(BuildEffectiveSlots(), commands);
            _grammarJson = VoskCommandParser.GenerateGrammarJson(
                _slots, commands, GetFollowUpGrammarWords());
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

            CancelPendingIfActive();

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
            _activeCommands = null;
            _commandLookup = null;
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

            CancelPendingIfActive();

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
            BuildCommandLookup(commands);
        }

        /// <summary>
        /// Activates a single command set by name.
        /// </summary>
        public void SetActiveSet(string setName)
        {
            SetActiveSets(setName);
        }

        /// <summary>
        /// Injects text into the command pipeline as if it had arrived from VOSK, exercising
        /// the same <see cref="HandleResult"/> path (parser, threshold filter, buffer, debounce)
        /// as real audio.
        /// When <c>bufferWindow &gt; 0</c> the result is queued and events fire later from
        /// <see cref="Update"/>; call <see cref="FlushPendingBuffer"/> for synchronous events.
        /// Requires one of the <c>Configure</c> overloads (and <see cref="SetActiveSets"/> when
        /// using command sets) to have been called first. Main thread only.
        /// </summary>
        public void InjectText(string text, VoskWord[] words = null)
        {
            Debug.Assert(System.Threading.Thread.CurrentThread.ManagedThreadId == 1,
                "InjectText must be called from the Unity main thread.");

            if (string.IsNullOrWhiteSpace(text))
                return;

            if (_parser == null)
            {
                Debug.LogWarning("[VoskCommandRecogniser] InjectText called before parser is ready. " +
                    "Call Configure(slots, commands) or Configure(slots, sets) followed by SetActiveSets(...) first.");
                return;
            }

            HandleResult(new VoskResult(
                text,
                words ?? Array.Empty<VoskWord>(),
                Array.Empty<VoskAlternative>()));
        }

        /// <summary>
        /// Flushes any speech currently held in the utterance buffer, firing command events
        /// synchronously. Useful for push-to-talk release and scene transitions. No-op when
        /// the buffer is empty. Main thread only.
        /// </summary>
        public void FlushPendingBuffer()
        {
            Debug.Assert(System.Threading.Thread.CurrentThread.ManagedThreadId == 1,
                "FlushPendingBuffer must be called from the Unity main thread.");

            if (_bufferActive)
                FlushBuffer();
        }

        /// <summary>
        /// Cancels the currently pending command, if any. Fires <see cref="OnCommandCancelled"/>.
        /// No-op when no command is pending.
        /// </summary>
        public void CancelPendingCommand() => CancelPendingIfActive();

        // -------- Dynamic slot value providers --------

        /// <summary>
        /// Registers a function that controls which values of the named slot the
        /// parser will accept. Call <see cref="NotifySlotChanged"/> after the
        /// provider's return set changes to rebuild the parser.
        /// The grammar (VOSK vocabulary) is not affected — it always reflects the
        /// full universe of slot values registered via <see cref="Configure"/>.
        /// </summary>
        public void RegisterSlotValueProvider(string slotName, Func<string[]> valueProvider)
        {
            if (slotName == null) throw new ArgumentNullException(nameof(slotName));
            if (valueProvider == null) throw new ArgumentNullException(nameof(valueProvider));

            if (_valueProviders == null)
                _valueProviders = new Dictionary<string, Func<string[]>>(StringComparer.Ordinal);

            _valueProviders[slotName] = valueProvider;
        }

        /// <summary>
        /// Removes a previously registered value provider. The slot reverts to its
        /// full universe of values on the next parser rebuild.
        /// </summary>
        public bool UnregisterSlotValueProvider(string slotName)
        {
            if (slotName == null) throw new ArgumentNullException(nameof(slotName));
            return _valueProviders != null && _valueProviders.Remove(slotName);
        }

        /// <summary>
        /// Rebuilds the parser to reflect current value-provider results.
        /// Does not touch the grammar or VOSK recogniser. No-op if
        /// <see cref="Configure"/> has not been called or no commands are active.
        /// Performs a full parser rebuild — call only when provider values have
        /// actually changed, not every frame.
        /// </summary>
        public void NotifySlotChanged()
        {
            if (_activeCommands == null)
                return;

            RebuildParser();
        }

        /// <summary>
        /// Rebuilds only the parser from the current effective slots and active
        /// commands. The grammar and VOSK recogniser are untouched.
        /// </summary>
        public void RebuildParser()
        {
            if (_activeCommands == null)
                throw new InvalidOperationException(
                    "Configure must be called before RebuildParser().");

            _parser = new VoskCommandParser(BuildEffectiveSlots(), _activeCommands);
        }

        /// <summary>
        /// Rebuilds and re-applies the VOSK grammar from the full universe of slot
        /// values. Performs the stop → set grammar → start cycle when recognition
        /// is running. Clears the utterance buffer.
        /// If a command is currently pending, the rebuild is deferred until the
        /// pending command resolves.
        /// </summary>
        public void RebuildGrammar()
        {
            if (_activeCommands == null)
                throw new InvalidOperationException(
                    "Configure must be called before RebuildGrammar().");

            if (_pendingCommand.HasValue)
            {
                _grammarRebuildDeferred = true;
                return;
            }

            RebuildGrammarInternal();
        }

        void RebuildGrammarInternal()
        {
            if (_bufferActive)
            {
                _bufferedTexts.Clear();
                _bufferedWords.Clear();
                _bufferActive = false;
            }

            _grammarJson = VoskCommandParser.GenerateGrammarJson(
                _slots, _activeCommands, GetFollowUpGrammarWords());
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

        void DrainDeferredGrammarRebuild()
        {
            if (!_grammarRebuildDeferred)
                return;

            _grammarRebuildDeferred = false;
            RebuildGrammarInternal();
        }

        VoskSlotDefinition[] BuildEffectiveSlots()
        {
            if (_valueProviders == null || _valueProviders.Count == 0)
                return _slots;

            VoskSlotDefinition[] effective = null;

            for (int i = 0; i < _slots.Length; i++)
            {
                var slot = _slots[i];

                if (slot.Type == VoskSlotType.NumberSequence ||
                    !_valueProviders.TryGetValue(slot.Name, out var provider))
                {
                    if (effective != null)
                        effective[i] = slot;
                    continue;
                }

                var activeValues = provider();
                if (activeValues == null)
                {
                    if (effective != null)
                        effective[i] = slot;
                    continue;
                }

                if (effective == null)
                {
                    effective = new VoskSlotDefinition[_slots.Length];
                    Array.Copy(_slots, effective, i);
                }

                if (activeValues.Length == 0)
                {
                    effective[i] = new VoskSlotDefinition(slot.Name, Array.Empty<string>(), null);
                    continue;
                }

                var activeSet = new HashSet<string>(activeValues, StringComparer.Ordinal);

                Dictionary<string, string> filteredAliases = null;
                if (slot.Aliases != null)
                {
                    foreach (var kvp in slot.Aliases)
                    {
                        if (activeSet.Contains(kvp.Value))
                        {
                            if (filteredAliases == null)
                                filteredAliases = new Dictionary<string, string>(StringComparer.Ordinal);
                            filteredAliases[kvp.Key] = kvp.Value;
                        }
                    }
                }

                effective[i] = new VoskSlotDefinition(slot.Name, activeValues, filteredAliases);
            }

            return effective ?? _slots;
        }

        // Test-only setters. Production callers configure via the Inspector.
        internal float BufferWindow { set => bufferWindow = value; }
        internal float CommandCooldown { set => commandCooldown = value; }
        internal VoskSpeechRecogniser SpeechRecogniser
        {
            set
            {
                // Unsubscribe from the old recogniser if any.
                if (speechRecogniser != null)
                {
                    speechRecogniser.OnModelReady -= HandleModelReady;
                    speechRecogniser.OnResult -= HandleResult;
#if UNITY_EDITOR
                    speechRecogniser.OnPartialResult -= HandlePartialResult;
#endif
                }

                speechRecogniser = value;

                // Subscribe immediately when the component is already active
                // (Edit Mode tests may not re-trigger OnEnable after SetActive).
                if (value != null && isActiveAndEnabled)
                {
                    value.OnModelReady += HandleModelReady;
                    value.OnResult += HandleResult;
#if UNITY_EDITOR
                    value.OnPartialResult += HandlePartialResult;
#endif
                }
            }
        }

        void RebuildParserAndGrammar(VoskCommandDefinition[] commands)
        {
            _activeCommands = commands;
            RebuildParser();
            RebuildGrammarInternal();
        }

        void Awake()
        {
            // If user code already called Configure(), _slots is non-null and inspector assets are ignored.
            if (_slots != null)
                return;

            if (slotAssets == null || slotAssets.Length == 0)
                return;

            if (commandSetAssets == null || commandSetAssets.Length == 0)
                return;

            var slotList = new List<VoskSlotDefinition>(slotAssets.Length);
            for (int i = 0; i < slotAssets.Length; i++)
            {
                if (slotAssets[i] == null)
                {
                    Debug.LogWarning($"[VoskCommandRecogniser] slotAssets[{i}] is null — skipping.");
                    continue;
                }
                slotList.Add(slotAssets[i].ToDefinition());
            }

            var setList = new List<VoskCommandSet>(commandSetAssets.Length);
            for (int i = 0; i < commandSetAssets.Length; i++)
            {
                if (commandSetAssets[i] == null)
                {
                    Debug.LogWarning(
                        $"[VoskCommandRecogniser] commandSetAssets[{i}] is null — skipping.");
                    continue;
                }
                setList.Add(commandSetAssets[i].ToSet());
            }

            Configure(slotList.ToArray(), setList.ToArray());

            if (initialActiveSetNames != null && initialActiveSetNames.Length > 0)
                SetActiveSets(initialActiveSetNames);
        }

        void OnEnable()
        {
            if (speechRecogniser == null)
                return;

            speechRecogniser.OnModelReady += HandleModelReady;
            speechRecogniser.OnResult += HandleResult;
#if UNITY_EDITOR
            speechRecogniser.OnPartialResult += HandlePartialResult;
#endif

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
#if UNITY_EDITOR
            speechRecogniser.OnPartialResult -= HandlePartialResult;
#endif

            // Suppress deferred grammar rebuild — grammar will be re-evaluated on next enable/configure.
            // CancelPendingIfActive would drain the rebuild, which is unsafe during disable.
            _grammarRebuildDeferred = false;
            CancelPendingIfActive();

            // Flush any pending buffer on disable
            if (_bufferActive)
                FlushBuffer();
        }

        void Update()
        {
            if (_bufferActive && Time.time - _lastResultTime >= bufferWindow)
                FlushBuffer();

            if (_pendingCommand.HasValue &&
                Time.time - _pendingCommand.Value.CreatedTime >= pendingTimeout)
            {
                HandlePendingTimeout();
            }
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
            // ---- Step 1: Confirm/cancel check (before parsing) ----
            if (_pendingCommand.HasValue && TryHandleConfirmCancel(text))
            {
#if UNITY_EDITOR
                LastMatchDiagnostics = new VoskMatchDiagnostics(
                    text, words,
                    new[] { new VoskMatchAttempt(null, null, 0f, minScore, 0f, minConfidence,
                        null, "handled as confirm/cancel", false) },
                    Time.frameCount);
#endif
                return;
            }

            // ---- Step 2: Follow-up slot-fill attempt (before parsing) ----
            VoskCommand? followUpResult = null;
            if (_pendingCommand.HasValue)
                followUpResult = TryFollowUpSlotFill(text, words);

            // ---- Step 3: Normal parse ----
            var results = _parser.Parse(text, words);

#if UNITY_EDITOR
            var parseDiag = _parser.LastParseDiagnostics;
            string[] diagTokens = text.Split(VoskCommandParser.SplitSeparator, StringSplitOptions.RemoveEmptyEntries);
            Dictionary<string, float> diagWordConf = null;
            if (words != null && words.Length > 0)
            {
                diagWordConf = new Dictionary<string, float>(words.Length, StringComparer.Ordinal);
                foreach (var w in words)
                    if (!string.IsNullOrEmpty(w.Text) && !diagWordConf.ContainsKey(w.Text))
                        diagWordConf[w.Text] = w.Confidence;
            }
#endif

            // ---- Step 4: Determine if any normal result passes standard thresholds ----
            bool hasCompleteNewCommand = false;
            for (int i = 0; i < results.Length; i++)
            {
                var cmd = results[i].Command;
                if (cmd.Score >= minScore &&
                    (cmd.Confidence < 0f || cmd.Confidence >= minConfidence))
                {
                    hasCompleteNewCommand = true;
                    break;
                }
            }

            // ---- Step 5: Arbitrate follow-up vs new command ----
            if (followUpResult.HasValue && !hasCompleteNewCommand)
            {
                CompletePendingCommand(followUpResult.Value);
#if UNITY_EDITOR
                LastMatchDiagnostics = new VoskMatchDiagnostics(
                    text, words,
                    new[] { new VoskMatchAttempt(
                        followUpResult.Value.Intent, null, followUpResult.Value.Score,
                        minScore, followUpResult.Value.Confidence, minConfidence,
                        null, null, true) },
                    Time.frameCount);
#endif
                return;
            }

            // If new complete command preempts a pending, cancel the pending
            if (_pendingCommand.HasValue && hasCompleteNewCommand)
                CancelPendingIfActive();

            // ---- Step 6: No parse results ----
            if (results.Length == 0)
            {
#if UNITY_EDITOR
                LastMatchDiagnostics = new VoskMatchDiagnostics(
                    text, words,
                    new[] { new VoskMatchAttempt(null, null, 0f, minScore, 0f, minConfidence,
                        null, "no match", false) },
                    Time.frameCount);
#endif
                OnUnrecognisedSpeech?.Invoke(text);
                return;
            }

            // ---- Step 7: Process results with pending-aware logic ----
            float now = Time.time;
            var accepted = new List<VoskCommand>();
#if UNITY_EDITOR
            var attempts = new List<VoskMatchAttempt>(results.Length);
#endif

            for (int i = 0; i < results.Length; i++)
            {
                var cmd = results[i].Command;

                // Below score threshold — check AllowPartialMatch before rejecting
                if (cmd.Score < minScore)
                {
                    if (cmd.Score > 0f && _commandLookup != null &&
                        _commandLookup.TryGetValue(cmd.Intent, out var partialDef) &&
                        partialDef.AllowPartialMatch)
                    {
                        var unfilled = ComputeUnfilledSlots(cmd, partialDef);
                        if (unfilled.Length > 0)
                        {
                            EnterPendingState(cmd, partialDef, unfilled,
                                VoskPendingReason.PartialMatch);
#if UNITY_EDITOR
                            attempts.Add(BuildAttempt(cmd, parseDiag, i, diagTokens, diagWordConf,
                                $"entered pending (partial: unfilled [{string.Join(", ", unfilled)}])",
                                false));
#endif
                            continue;
                        }
                    }

#if UNITY_EDITOR
                    attempts.Add(BuildAttempt(cmd, parseDiag, i, diagTokens, diagWordConf,
                        $"score {cmd.Score:F2} < minScore {minScore:F2}", false));
#endif
                    continue;
                }

                // Reject if below confidence threshold (skip when word data unavailable, i.e. -1)
                if (cmd.Confidence >= 0f && cmd.Confidence < minConfidence)
                {
#if UNITY_EDITOR
                    attempts.Add(BuildAttempt(cmd, parseDiag, i, diagTokens, diagWordConf,
                        $"confidence {cmd.Confidence:F2} < minConfidence {minConfidence:F2}", false));
#endif
                    continue;
                }

                // Per-intent debounce
                if (commandCooldown > 0f &&
                    _lastFireTime.TryGetValue(cmd.Intent, out float lastTime) &&
                    now - lastTime < commandCooldown)
                {
#if UNITY_EDITOR
                    attempts.Add(BuildAttempt(cmd, parseDiag, i, diagTokens, diagWordConf,
                        $"debounced ({commandCooldown:F1}s cooldown)", false));
#endif
                    continue;
                }

                // Check RequiresConfirmation — enter pending instead of firing
                if (_commandLookup != null &&
                    _commandLookup.TryGetValue(cmd.Intent, out var confirmDef) &&
                    confirmDef.RequiresConfirmation)
                {
                    EnterPendingState(cmd, confirmDef, Array.Empty<string>(),
                        VoskPendingReason.AwaitingConfirmation);
#if UNITY_EDITOR
                    attempts.Add(BuildAttempt(cmd, parseDiag, i, diagTokens, diagWordConf,
                        "entered pending (awaiting confirmation)", false));
#endif
                    continue;
                }

                _lastFireTime[cmd.Intent] = now;
                accepted.Add(cmd);
#if UNITY_EDITOR
                attempts.Add(BuildAttempt(cmd, parseDiag, i, diagTokens, diagWordConf, null, true));
#endif
            }

#if UNITY_EDITOR
            LastMatchDiagnostics = new VoskMatchDiagnostics(
                text, words, attempts.ToArray(), Time.frameCount);
#endif

            if (accepted.Count == 0)
            {
                OnUnrecognisedSpeech?.Invoke(text);
                return;
            }

            // Fire per-command events in order
            for (int i = 0; i < accepted.Count; i++)
                OnCommandRecognised?.Invoke(accepted[i]);

            // Fire batch event
            if (OnCommandsRecognised != null)
                OnCommandsRecognised.Invoke(accepted.ToArray());
        }

        // -------- Pending command helpers --------

        bool TryHandleConfirmCancel(string text)
        {
            string[] tokens = text.Split(VoskCommandParser.SplitSeparator,
                StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                return false;

            string normalized = string.Join(" ", tokens);

            string[] effectiveCancel = cancelVocabulary != null && cancelVocabulary.Length > 0
                ? cancelVocabulary : VoskFollowUpVocabulary.DefaultCancel;
            string[] effectiveConfirm = confirmVocabulary != null && confirmVocabulary.Length > 0
                ? confirmVocabulary : VoskFollowUpVocabulary.DefaultConfirm;

            if (IsVocabularyMatch(normalized, effectiveCancel))
            {
                CancelPendingIfActive();
                return true;
            }

            if (IsVocabularyMatch(normalized, effectiveConfirm))
            {
                var confirmed = _pendingCommand.Value;
                _pendingCommand = null;
                FireConfirmedCommand(confirmed.Command);
                return true;
            }

            return false;
        }

        VoskCommand? TryFollowUpSlotFill(string text, VoskWord[] words)
        {
            var pending = _pendingCommand.Value;
            if (pending.UnfilledSlots == null || pending.UnfilledSlots.Length == 0)
                return null;

            string[] tokens = text.Split(VoskCommandParser.SplitSeparator,
                StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                return null;

            var newSlots = new List<VoskSlotMatch>(pending.Command.Slots);
            int tokenIdx = 0;

            foreach (string slotName in pending.UnfilledSlots)
            {
                // Try each token position (sliding start) for this slot
                bool found = false;
                for (int startIdx = tokenIdx; startIdx < tokens.Length; startIdx++)
                {
                    if (tokens[startIdx] == VoskCommandParser.UnkToken)
                        continue;

                    string value = _parser.TryMatchSlotByName(
                        tokens, startIdx, slotName, out int consumed);
                    if (value != null)
                    {
                        newSlots.Add(new VoskSlotMatch(slotName, value));
                        tokenIdx = startIdx + consumed;
                        found = true;
                        break;
                    }
                }

                if (!found)
                    break;
            }

            // Must have filled at least one new slot
            if (newSlots.Count == pending.Command.Slots.Length)
                return null;

            // Compute updated confidence from follow-up words
            Dictionary<string, float> wordConfidence = null;
            if (words != null && words.Length > 0)
            {
                wordConfidence = new Dictionary<string, float>(words.Length, StringComparer.Ordinal);
                foreach (var w in words)
                    if (!string.IsNullOrEmpty(w.Text) && !wordConfidence.ContainsKey(w.Text))
                        wordConfidence[w.Text] = w.Confidence;
            }
            float followUpConf = VoskCommandParser.ComputeConfidence(
                tokens, 0, tokens.Length, wordConfidence);

            float mergedConfidence = pending.Command.Confidence >= 0f && followUpConf >= 0f
                ? Math.Min(pending.Command.Confidence, followUpConf)
                : pending.Command.Confidence >= 0f ? pending.Command.Confidence : followUpConf;

            return new VoskCommand(
                pending.Command.Intent,
                newSlots.ToArray(),
                mergedConfidence,
                pending.Command.Score,
                pending.Command.RawText + " " + text,
                null,
                pending.Command.MatchedPatternIndex);
        }

        void EnterPendingState(VoskCommand command, VoskCommandDefinition definition,
            string[] unfilledSlots, VoskPendingReason reason)
        {
            CancelPendingIfActive();

            _pendingCommand = new VoskPendingCommand
            {
                Command = command,
                Definition = definition,
                UnfilledSlots = unfilledSlots,
                Reason = reason,
                CreatedTime = Time.time,
            };

            OnCommandPending?.Invoke(command);
        }

        void CompletePendingCommand(VoskCommand completed)
        {
            var pending = _pendingCommand.Value;
            _pendingCommand = null;

            // If the definition also requires confirmation and we were pending
            // for partial match, re-enter pending for confirmation
            if (pending.Definition.RequiresConfirmation &&
                pending.Reason == VoskPendingReason.PartialMatch)
            {
                _pendingCommand = new VoskPendingCommand
                {
                    Command = completed,
                    Definition = pending.Definition,
                    UnfilledSlots = Array.Empty<string>(),
                    Reason = VoskPendingReason.AwaitingConfirmation,
                    CreatedTime = Time.time,
                };
                OnCommandPending?.Invoke(completed);
                return;
            }

            FireConfirmedCommand(completed);
        }

        void HandlePendingTimeout()
        {
            var pending = _pendingCommand.Value;
            _pendingCommand = null;

            if (pendingTimeoutBehavior == VoskPendingTimeoutBehavior.FireAsIs)
            {
                FireConfirmedCommand(pending.Command);
            }
            else
            {
                OnCommandCancelled?.Invoke(pending.Command);
                DrainDeferredGrammarRebuild();
            }
        }

        void FireConfirmedCommand(VoskCommand command)
        {
            _lastFireTime[command.Intent] = Time.time;
            OnCommandConfirmed?.Invoke(command);
            OnCommandRecognised?.Invoke(command);
            if (OnCommandsRecognised != null)
                OnCommandsRecognised.Invoke(new[] { command });
            DrainDeferredGrammarRebuild();
        }

        void CancelPendingIfActive()
        {
            if (!_pendingCommand.HasValue)
                return;

            var cancelled = _pendingCommand.Value;
            _pendingCommand = null;
            OnCommandCancelled?.Invoke(cancelled.Command);
            DrainDeferredGrammarRebuild();
        }

        static bool IsVocabularyMatch(string normalized, string[] vocabulary)
        {
            for (int i = 0; i < vocabulary.Length; i++)
            {
                if (string.Equals(normalized, vocabulary[i], StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        string[] ComputeUnfilledSlots(VoskCommand cmd, VoskCommandDefinition def)
        {
            if (cmd.MatchedPatternIndex < 0 ||
                cmd.MatchedPatternIndex >= def.Patterns.Length)
                return Array.Empty<string>();

            var pattern = def.Patterns[cmd.MatchedPatternIndex];
            List<string> unfilled = null;

            foreach (string element in pattern)
            {
                string slotName = VoskCommandParser.ExtractSlotName(element);
                if (slotName != null && !VoskCommandParser.IsOptionalSlot(element)
                    && !cmd.HasSlot(slotName))
                {
                    if (unfilled == null)
                        unfilled = new List<string>();
                    unfilled.Add(slotName);
                }
            }

            return unfilled?.ToArray() ?? Array.Empty<string>();
        }

        void BuildCommandLookup(VoskCommandDefinition[] commands)
        {
            if (_commandLookup == null)
                _commandLookup = new Dictionary<string, VoskCommandDefinition>(
                    commands.Length, StringComparer.Ordinal);
            else
                _commandLookup.Clear();

            for (int i = 0; i < commands.Length; i++)
                _commandLookup[commands[i].Intent] = commands[i];
        }

        string[] GetFollowUpGrammarWords()
        {
            var words = new HashSet<string>(StringComparer.Ordinal);

            VoskFollowUpVocabulary.AddPhraseWords(words, VoskFollowUpVocabulary.DefaultConfirm);
            VoskFollowUpVocabulary.AddPhraseWords(words, VoskFollowUpVocabulary.DefaultCancel);

            if (confirmVocabulary != null)
                VoskFollowUpVocabulary.AddPhraseWords(words, confirmVocabulary);
            if (cancelVocabulary != null)
                VoskFollowUpVocabulary.AddPhraseWords(words, cancelVocabulary);

            var result = new string[words.Count];
            words.CopyTo(result);
            return result;
        }

        // Test-only setters
        internal float PendingTimeout { set => pendingTimeout = value; }
        internal VoskPendingTimeoutBehavior PendingTimeoutBehavior
        {
            set => pendingTimeoutBehavior = value;
        }
        internal string[] ConfirmVocabulary { set => confirmVocabulary = value; }
        internal string[] CancelVocabulary { set => cancelVocabulary = value; }

#if UNITY_EDITOR
        internal VoskPendingCommand? EditorPendingCommand => _pendingCommand;

        void HandlePartialResult(string text)
        {
            LastPartialResult = text;
        }

        VoskMatchAttempt BuildAttempt(VoskCommand cmd,
            VoskCommandParser.ParseDiagnosticEntry[] parseDiag, int index,
            string[] tokens, Dictionary<string, float> wordConf,
            string rejectReason, bool isAccepted)
        {
            string pattern = null;
            VoskDiagnosticSlotMatch[] diagSlots = Array.Empty<VoskDiagnosticSlotMatch>();

            if (parseDiag != null && index < parseDiag.Length)
            {
                pattern = parseDiag[index].PatternString;

                if (cmd.Slots.Length > 0 && parseDiag[index].SlotStartWords != null)
                {
                    int slotCount = Math.Min(cmd.Slots.Length, parseDiag[index].SlotStartWords.Length);
                    diagSlots = new VoskDiagnosticSlotMatch[slotCount];
                    for (int s = 0; s < slotCount; s++)
                    {
                        int sw = parseDiag[index].SlotStartWords[s];
                        int ew = parseDiag[index].SlotEndWords[s];
                        float slotConf = VoskCommandParser.ComputeConfidence(tokens, sw, ew, wordConf);
                        diagSlots[s] = new VoskDiagnosticSlotMatch(
                            cmd.Slots[s].Name, cmd.Slots[s].Value, sw, ew, slotConf);
                    }
                }
            }

            return new VoskMatchAttempt(
                cmd.Intent, pattern, cmd.Score, minScore,
                cmd.Confidence, minConfidence, diagSlots,
                rejectReason, isAccepted);
        }
#endif
    }
}
