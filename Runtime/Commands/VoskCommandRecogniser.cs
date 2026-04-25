// ============================================================================
// Purpose:  MonoBehaviour facade: speech-to-command pipeline with buffer, debounce, pending, grammar
// Layer:    Runtime.Commands
// Owns:     VoskCommandRecogniser (public MonoBehaviour)
// Depends:  VoskSpeechRecogniser, VoskCommandParser, VoskCommand, VoskCommandDefinition, VoskSlotDefinition, VoskPendingCommand, VoskMatchDiagnostics
// ============================================================================
using System;
using System.Collections.Generic;
using UnityEngine;

namespace VoskXR.Commands
{
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
                 "Default 1.5s matches typical PC latency; bump to 2.0s on Quest 3, " +
                 "where VOSK adds ~0.5–1.0s to inter-result gaps. " +
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

        public event Action<VoskCommand> OnCommandPending;

        public event Action<VoskCommand> OnCommandConfirmed;

        public event Action<VoskCommand> OnCommandCancelled;

#if UNITY_EDITOR
        internal VoskMatchDiagnostics LastMatchDiagnostics { get; private set; }

        internal string LastPartialResult { get; private set; }
#endif

        VoskCommandParser _parser;
        readonly GrammarManager _grammar = new GrammarManager();

        // Utterance buffer
        readonly UtteranceBuffer _buffer = new UtteranceBuffer();

        // Per-intent debounce
        readonly CommandDebouncer _debouncer = new CommandDebouncer();

        // Command set and slot state
        VoskSlotDefinition[] _slots;
        VoskCommandDefinition[] _activeCommands;
        readonly CommandSetManager _setManager = new CommandSetManager();
        readonly DynamicSlotManager _slotManager = new DynamicSlotManager();

        // Pending command state
        readonly PendingCommandHandler _pending = new PendingCommandHandler();

        // Pre-allocated buffer for accepted commands (avoids per-utterance List allocation)
        VoskCommand[] _acceptedBuf;

        public string[] ActiveSetNames => _setManager.ActiveSetNames;

        public bool HasPendingCommand => _pending.HasPending;

        public VoskCommand? PendingCommand => _pending.PendingCommand;

        public void Configure(VoskSlotDefinition[] slots, VoskCommandDefinition[] commands)
        {
            if (slots == null) throw new ArgumentNullException(nameof(slots));
            if (commands == null) throw new ArgumentNullException(nameof(commands));

            InterpretResolution(_pending.Cancel());

            _debouncer.Clear();
            _slots = slots;
            _setManager.Reset();
            _activeCommands = commands;
            _setManager.BuildLookup(commands);
            EnsureAcceptedBuffer(commands.Length);

            _parser = new VoskCommandParser(_slotManager.BuildEffectiveSlots(_slots), commands);
            _grammar.Rebuild(_slots, commands, GetFollowUpGrammarWords());

            if (!freeSpeechMode && speechRecogniser != null && speechRecogniser.IsModelReady)
            {
                speechRecogniser.SetGrammar(_grammar.CurrentJson);
                _grammar.IsApplied = true;
            }
        }

        public void Configure(VoskSlotDefinition[] slots, VoskCommandSet[] sets)
        {
            if (slots == null) throw new ArgumentNullException(nameof(slots));
            if (sets == null) throw new ArgumentNullException(nameof(sets));

            InterpretResolution(_pending.Cancel());

            _debouncer.Clear();
            _slots = slots;
            _setManager.Configure(sets);

            _parser = null;
            _grammar.Reset();
            _activeCommands = null;
        }

        public void SetActiveSets(params string[] setNames)
        {
            if (!_setManager.HasSets)
                throw new InvalidOperationException(
                    "Configure(slots, sets) must be called before SetActiveSets().");

            InterpretResolution(_pending.Cancel());

            var commands = _setManager.Activate(setNames);
            _activeCommands = commands;
            EnsureAcceptedBuffer(commands.Length);
            _debouncer.Clear();
            RebuildParserAndGrammar();
        }

        public void SetActiveSet(string setName)
        {
            SetActiveSets(setName);
        }

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

        public void FlushPendingBuffer()
        {
            Debug.Assert(System.Threading.Thread.CurrentThread.ManagedThreadId == 1,
                "FlushPendingBuffer must be called from the Unity main thread.");

            if (_buffer.IsActive)
                FlushBuffer();
        }

        public void CancelPendingCommand() => InterpretResolution(_pending.Cancel());

        // -------- Dynamic slot value providers --------

        public void RegisterSlotValueProvider(string slotName, Func<string[]> valueProvider)
        {
            _slotManager.Register(slotName, valueProvider);
        }

        public bool UnregisterSlotValueProvider(string slotName)
        {
            return _slotManager.Unregister(slotName);
        }

        public void NotifySlotChanged()
        {
            if (_activeCommands == null)
                return;

            RebuildParser();
        }

        public void RebuildParser()
        {
            if (_activeCommands == null)
                throw new InvalidOperationException(
                    "Configure must be called before RebuildParser().");

            _parser = new VoskCommandParser(_slotManager.BuildEffectiveSlots(_slots), _activeCommands);
        }

        public void RebuildGrammar()
        {
            if (_activeCommands == null)
                throw new InvalidOperationException(
                    "Configure must be called before RebuildGrammar().");

            if (_pending.HasPending)
            {
                _grammar.GrammarRebuildDeferred = true;
                return;
            }

            RebuildGrammarInternal();
        }

        void RebuildGrammarInternal()
        {
            _buffer.Reset();
            _grammar.Rebuild(_slots, _activeCommands, GetFollowUpGrammarWords());
            _grammar.ForceApply(speechRecogniser, freeSpeechMode);
        }

        void DrainDeferredGrammarRebuild()
        {
            if (!_grammar.GrammarRebuildDeferred)
                return;

            _grammar.GrammarRebuildDeferred = false;
            RebuildGrammarInternal();
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

        void EnsureAcceptedBuffer(int commandCount)
        {
            if (_acceptedBuf == null || _acceptedBuf.Length < commandCount)
                _acceptedBuf = new VoskCommand[Math.Max(commandCount, 1)];
        }

        void RebuildParserAndGrammar()
        {
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
            _grammar.GrammarRebuildDeferred = false;
            InterpretResolution(_pending.Cancel());

            // Flush any pending buffer on disable
            if (_buffer.IsActive)
                FlushBuffer();
        }

        void Update()
        {
            if (_buffer.IsActive && _buffer.ShouldFlush(Time.time, bufferWindow))
                FlushBuffer();

            if (_pending.HasPending &&
                Time.time - _pending.Current.Value.CreatedTime >= pendingTimeout)
            {
                var resolution = _pending.HandleTimeout(pendingTimeoutBehavior);
                InterpretResolution(resolution);
#if UNITY_EDITOR
                bool timedOutConfirmed = resolution.Outcome == PendingOutcome.Confirmed;
                string timeoutLabel = timedOutConfirmed
                    ? "timeout — fired as-is" : "timeout — cancelled";
                LastMatchDiagnostics = new VoskMatchDiagnostics(
                    resolution.Command.RawText ?? "", Array.Empty<VoskWord>(),
                    new[] { new VoskMatchAttempt(
                        resolution.Command.Intent, null,
                        resolution.Command.Score, minScore,
                        resolution.Command.Confidence, minConfidence,
                        null, timedOutConfirmed ? null : timeoutLabel,
                        timedOutConfirmed) },
                    Time.frameCount);
#endif
            }
        }

        void HandleModelReady()
        {
            _grammar.ApplyIfReady(speechRecogniser, freeSpeechMode);
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
            _buffer.Append(result.Text, result.Words, Time.time);
        }

        void FlushBuffer()
        {
            string text = _buffer.Flush();
            if (text.Length == 0)
            {
                _buffer.ClearWords();
                return;
            }

            var words = _buffer.GetWordsSpan();
            ProcessParsedResults(text, words);
            _buffer.ClearWords();
        }

        void ProcessParsedResults(string text, VoskWord[] words)
            => ProcessParsedResultsCore(text, words, _parser.InstanceBuildWordConfidence(words));

        void ProcessParsedResults(string text, ReadOnlySpan<VoskWord> words)
            => ProcessParsedResultsCore(text, words, _parser.InstanceBuildWordConfidence(words));

        void ProcessParsedResultsCore(string text, ReadOnlySpan<VoskWord> words,
            Dictionary<string, float> wordConfidence)
        {
            // Split once — shared by pending handlers, diagnostics, and the parser.
            string[] tokens = text.Split(VoskCommandParser.SplitSeparator,
                StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                return;

#if UNITY_EDITOR
            // Editor diagnostics need VoskWord[] — copy once for the editor path only.
            VoskWord[] diagWords = words.ToArray();
#endif

            // ---- Step 1: Confirm/cancel check (before parsing) ----
            if (_pending.HasPending)
            {
                var ccResolution = _pending.TryHandleConfirmCancel(
                    tokens, confirmVocabulary, cancelVocabulary);
                if (ccResolution.Outcome != PendingOutcome.None)
                {
                    InterpretResolution(ccResolution);
#if UNITY_EDITOR
                    bool wasConfirmed = ccResolution.Outcome == PendingOutcome.Confirmed;
                    string ccLabel = wasConfirmed ? "confirmed" : "cancelled";
                    LastMatchDiagnostics = new VoskMatchDiagnostics(
                        text, diagWords,
                        new[] { new VoskMatchAttempt(
                            ccResolution.Command.Intent, null,
                            ccResolution.Command.Score, minScore,
                            ccResolution.Command.Confidence, minConfidence,
                            null, wasConfirmed ? null : $"{ccLabel} via vocabulary",
                            wasConfirmed) },
                        Time.frameCount);
#endif
                    return;
                }
            }

            // ---- Step 2: Follow-up slot-fill attempt (before parsing) ----
            VoskCommand? followUpResult = null;
            if (_pending.HasPending)
                followUpResult = _pending.TryFollowUpSlotFill(
                    text, tokens, wordConfidence, _parser);

            // ---- Step 3: Normal parse (internal path — no duplicate split/dict) ----
            int resultCount = _parser.ParseInternal(tokens, text, wordConfidence);

#if UNITY_EDITOR
            var parseDiag = _parser.LastParseDiagnostics;
            // wordConfidence is already built above — reuse for diagnostics.
            Dictionary<string, float> diagWordConf = wordConfidence;
#endif

            // ---- Step 4: Determine if any normal result passes standard thresholds ----
            bool hasCompleteNewCommand = false;
            var resultBuf = _parser.ResultBuffer;
            for (int i = 0; i < resultCount; i++)
            {
                var cmd = resultBuf[i].Command;
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
                var completeRes = _pending.Complete(followUpResult.Value);
                InterpretResolution(completeRes);
#if UNITY_EDITOR
                LastMatchDiagnostics = new VoskMatchDiagnostics(
                    text, diagWords,
                    new[] { new VoskMatchAttempt(
                        followUpResult.Value.Intent, null, followUpResult.Value.Score,
                        minScore, followUpResult.Value.Confidence, minConfidence,
                        null, null, true) },
                    Time.frameCount);
#endif
                return;
            }

            // If new complete command preempts a pending, cancel the pending
            if (_pending.HasPending && hasCompleteNewCommand)
                InterpretResolution(_pending.Cancel());

            // ---- Step 6: No parse results ----
            if (resultCount == 0)
            {
#if UNITY_EDITOR
                LastMatchDiagnostics = new VoskMatchDiagnostics(
                    text, diagWords,
                    new[] { new VoskMatchAttempt(null, null, 0f, minScore, 0f, minConfidence,
                        null, "no match", false) },
                    Time.frameCount);
#endif
                OnUnrecognisedSpeech?.Invoke(text);
                return;
            }

            // ---- Step 7: Process results with pending-aware logic ----
            float now = Time.time;
            int acceptedCount = 0;
            bool anyThresholdFiltered = false;
#if UNITY_EDITOR
            var attempts = new List<VoskMatchAttempt>(resultCount);
#endif

            for (int i = 0; i < resultCount; i++)
            {
                var cmd = resultBuf[i].Command;

                // Below score threshold — check AllowPartialMatch before rejecting
                if (cmd.Score < minScore)
                {
                    if (cmd.Score > 0f &&
                        _setManager.TryLookupCommand(cmd.Intent, out var partialDef) &&
                        partialDef.AllowPartialMatch)
                    {
                        var unfilled = _pending.ComputeUnfilledSlots(cmd, partialDef);
                        if (unfilled.Length > 0)
                        {
                            var enterRes = _pending.EnterPending(cmd, partialDef, unfilled,
                                VoskPendingReason.PartialMatch, Time.time,
                                out var cancelRes);
                            InterpretResolution(cancelRes);
                            InterpretResolution(enterRes);
#if UNITY_EDITOR
                            attempts.Add(BuildAttempt(cmd, parseDiag, i, tokens, diagWordConf,
                                $"entered pending (partial: unfilled [{string.Join(", ", unfilled)}])",
                                false));
#endif
                            continue;
                        }
                    }

#if UNITY_EDITOR
                    attempts.Add(BuildAttempt(cmd, parseDiag, i, tokens, diagWordConf,
                        $"score {cmd.Score:F2} < minScore {minScore:F2}", false));
#endif
                    continue;
                }

                // Reject if below confidence threshold (skip when word data unavailable, i.e. -1)
                if (cmd.Confidence >= 0f && cmd.Confidence < minConfidence)
                {
                    anyThresholdFiltered = true;
#if UNITY_EDITOR
                    attempts.Add(BuildAttempt(cmd, parseDiag, i, tokens, diagWordConf,
                        $"confidence {cmd.Confidence:F2} < minConfidence {minConfidence:F2}", false));
#endif
                    continue;
                }

                // Per-intent debounce
                if (commandCooldown > 0f &&
                    _debouncer.IsOnCooldown(cmd.Intent, now, commandCooldown))
                {
                    anyThresholdFiltered = true;
#if UNITY_EDITOR
                    attempts.Add(BuildAttempt(cmd, parseDiag, i, tokens, diagWordConf,
                        $"debounced ({commandCooldown:F1}s cooldown)", false));
#endif
                    continue;
                }

                // Check RequiresConfirmation — enter pending instead of firing
                if (_setManager.TryLookupCommand(cmd.Intent, out var confirmDef) &&
                    confirmDef.RequiresConfirmation)
                {
                    var enterConfRes = _pending.EnterPending(cmd, confirmDef,
                        Array.Empty<string>(), VoskPendingReason.AwaitingConfirmation, Time.time,
                        out var cancelConfRes);
                    InterpretResolution(cancelConfRes);
                    InterpretResolution(enterConfRes);
#if UNITY_EDITOR
                    attempts.Add(BuildAttempt(cmd, parseDiag, i, tokens, diagWordConf,
                        "entered pending (awaiting confirmation)", false));
#endif
                    continue;
                }

                _debouncer.RecordFire(cmd.Intent, now);
                _acceptedBuf[acceptedCount++] = cmd;
#if UNITY_EDITOR
                attempts.Add(BuildAttempt(cmd, parseDiag, i, tokens, diagWordConf, null, true));
#endif
            }

#if UNITY_EDITOR
            LastMatchDiagnostics = new VoskMatchDiagnostics(
                text, diagWords, attempts.ToArray(), Time.frameCount);
#endif

            if (acceptedCount == 0)
            {
                // Only fire OnUnrecognisedSpeech when the speech genuinely didn't match
                // any command. Threshold-filtered results (confidence, debounce) are
                // silently dropped — the user said a valid command, just not confidently
                // or too soon after the last one.
                if (!anyThresholdFiltered)
                    OnUnrecognisedSpeech?.Invoke(text);
                return;
            }

            // Fire per-command events in order
            for (int i = 0; i < acceptedCount; i++)
                OnCommandRecognised?.Invoke(_acceptedBuf[i]);

            // Fire batch event
            if (OnCommandsRecognised != null)
            {
                var batch = new VoskCommand[acceptedCount];
                Array.Copy(_acceptedBuf, batch, acceptedCount);
                OnCommandsRecognised.Invoke(batch);
            }

            // Clear stale references
            Array.Clear(_acceptedBuf, 0, acceptedCount);
        }

        // -------- Pending resolution interpreter --------

        void InterpretResolution(PendingResolution resolution)
        {
            switch (resolution.Outcome)
            {
                case PendingOutcome.None:
                    return;

                case PendingOutcome.Confirmed:
                    _debouncer.RecordFire(resolution.Command.Intent, Time.time);
                    OnCommandConfirmed?.Invoke(resolution.Command);
                    OnCommandRecognised?.Invoke(resolution.Command);
                    if (OnCommandsRecognised != null)
                        OnCommandsRecognised.Invoke(new[] { resolution.Command });
                    DrainDeferredGrammarRebuild();
                    break;

                case PendingOutcome.Cancelled:
                    OnCommandCancelled?.Invoke(resolution.Command);
                    DrainDeferredGrammarRebuild();
                    break;

                case PendingOutcome.Entered:
                case PendingOutcome.ReEnteredPending:
                    OnCommandPending?.Invoke(resolution.Command);
                    break;
            }
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

        // Test-only setters/getters
        internal float PendingTimeout { set => pendingTimeout = value; }
        internal VoskPendingTimeoutBehavior PendingTimeoutBehavior
        {
            set => pendingTimeoutBehavior = value;
        }
        internal string[] ConfirmVocabulary { set => confirmVocabulary = value; }
        internal string[] CancelVocabulary { set => cancelVocabulary = value; }
        internal string TestGrammarJson => _grammar.CurrentJson;
        internal bool TestGrammarRebuildDeferred => _grammar.GrammarRebuildDeferred;

        internal void TestForceTimeoutNow()
        {
            if (!_pending.HasPending) return;
            var current = _pending.Current.Value;
            current.CreatedTime = -1000f;
            _pending.ForceSetForTest(current);
            SendMessage("Update", SendMessageOptions.DontRequireReceiver);
        }

#if UNITY_EDITOR
        internal VoskPendingCommand? EditorPendingCommand => _pending.Current;

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
