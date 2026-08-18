// ============================================================================
// Purpose:  MonoBehaviour facade: speech-to-command pipeline with buffer, debounce, pending, grammar
// Layer:    Runtime.Commands
// Owns:     VoxrCommandRecogniser (public MonoBehaviour)
// Depends:  VoxrSpeechRecogniser, VoxrCommandParser, VoxrCommand, VoxrCommandDefinition, VoxrSlotDefinition, VoxrPendingCommand, VoxrPendingAmbiguity, VoxrMatchDiagnostics
// ============================================================================
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace VoXR.Commands
{
    [AddComponentMenu("VoXR/Command Recogniser")]
    public class VoxrCommandRecogniser : MonoBehaviour
    {
        [SerializeField] VoxrSpeechRecogniser speechRecogniser;

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

        [Tooltip(
            "How much each in-grammar word a match leaves unexplained counts against "
                + "the score — both the words the parser skipped to reach the match and the "
                + "ones left over after it. The parser slides its start point through the "
                + "utterance, so without this a short pattern found anywhere inside a longer "
                + "sentence scores a full 1.0 and fires, discarding the rest. At 1.0 the "
                + "score is close to the fraction of the utterance the pattern covers, so a "
                + "one-word command needs to be most of what was said. Unrecognised ([unk]) "
                + "filler is never charged, and a leftover word that could begin another "
                + "command or a confirm/cancel reply usually is not either, so chained "
                + "commands still work — the exception is a word the pattern itself tried "
                + "and failed to match, which is always charged. Set to 0 to disable — note "
                + "that this also restores the behaviour where a spoken argument can be "
                + "silently dropped in favour of a shorter pattern."
        )]
        // DR-4: carries the value across the rename for this field's OWN serialized data, so
        // a scene or asset that set the old name deserializes onto the new one. The field is
        // private, so there is no public property or constant needing a separate forwarder.
        //
        // Scope, deliberately narrow: [FormerlySerializedAs] governs field deserialization.
        // A prefab-INSTANCE override is stored as a literal propertyPath string in the
        // instance's m_Modifications and applied against the already-loaded prefab, and the
        // same is true of .preset assets and of any editor script calling FindProperty with
        // the old name. Whether Unity remaps those paths through this attribute is not
        // established here and no automated instrument in this package can settle it — it
        // needs a real prefab-override round-trip in a host project. Nothing shipped in this
        // package is affected: no prefab, no preset, no CustomEditor, and no sample scene
        // serializes this field at all.
        [FormerlySerializedAs("skippedWordPenalty")]
        [SerializeField]
        float coverageWeight = VoxrCommandParser.DefaultCoverageWeight;

        [Tooltip("Time in seconds to wait for additional speech before parsing. " +
                 "Longer values recover split commands but add latency. " +
                 "Default 0.5s matches typical PC latency; use 2.0s on Quest 3, " +
                 "where VOSK adds ~0.5–1.0s to inter-result gaps and the measured " +
                 "gap between results runs ~1.9–2.1s. Past ~2.5s unrelated " +
                 "utterances start merging. " +
                 "Set to 0 to disable buffering (v2.2 behaviour).")]
        [SerializeField] float bufferWindow = 0.5f;

        [Tooltip("When the buffered speech already forms a complete command that cannot be " +
                 "extended or completed by more words, flush and fire immediately instead " +
                 "of waiting out bufferWindow. Zero latency for unambiguous commands; " +
                 "commands that are a prefix of a longer one still wait the full window. " +
                 "Opt-in — off preserves the time-only buffering behaviour.")]
        [SerializeField] bool eagerFlushOnCompleteMatch = false;

        [Tooltip("How long a complete command that more speech could still extend — one " +
                 "that is a prefix of a longer command, or ends in a slot whose value could " +
                 "grow — waits for that continuation before firing. Only the continuation is " +
                 "being waited for, and a speaker who is continuing does so almost " +
                 "immediately, so this can be much shorter than bufferWindow (0.5–0.8s is " +
                 "usually enough; scale up with bufferWindow on Quest 3). Requires " +
                 "eagerFlushOnCompleteMatch. Never lengthens the wait: values above " +
                 "bufferWindow are ignored. Set to 0 to keep waiting the full bufferWindow.")]
        [SerializeField] float prefixHoldSeconds = 0f;

        [Tooltip("Minimum seconds between firing the same intent. " +
                 "Prevents duplicate commands from rapid VOSK results. " +
                 "Set to 0 to disable debounce.")]
        [SerializeField] float commandCooldown = 0.3f;

        [Header("Inspector Authoring (optional — ignored if Configure() is called from code)")]
        [SerializeField] VoxrSlotAsset[] slotAssets;
        [SerializeField] VoxrCommandSetAsset[] commandSetAssets;

        [Tooltip("Command sets to activate on startup when using Inspector authoring.")]
        [SerializeField] string[] initialActiveSetNames;

        [Header("Follow-Up / Pending Commands")]
        [Tooltip("Maximum seconds a pending command waits for follow-up speech before timing out.")]
        [SerializeField] float pendingTimeout = 5.0f;

        [Tooltip("What happens when a pending command times out.")]
        [SerializeField] VoxrPendingTimeoutBehavior pendingTimeoutBehavior = VoxrPendingTimeoutBehavior.Cancel;

        [Tooltip("Phrases that confirm a pending command. " +
                 "Leave empty to use defaults (confirm, affirmative, yes, go ahead, do it).")]
        [SerializeField] string[] confirmVocabulary;

        [Tooltip("Phrases that cancel a pending command. " +
                 "Leave empty to use defaults (cancel, abort, negative, belay that, never mind).")]
        [SerializeField] string[] cancelVocabulary;

        [Tooltip(
            "When the recogniser cannot tell two commands apart — they differ only by one "
                + "word and the recogniser dropped it — ask instead of guessing. The pending "
                + "command is raised through OnCommandPending with PendingAmbiguity set; the "
                + "speaker answers with the distinguishing word. Off by default: with no "
                + "OnCommandPending subscriber an ambiguous utterance would fire nothing at all."
        )]
        [SerializeField]
        bool disambiguateSiblingTies = false;

        public event Action<VoxrCommand> OnCommandRecognised;
        public event Action<VoxrCommand[]> OnCommandsRecognised;
        public event Action<string> OnUnrecognisedSpeech;

        public event Action<VoxrCommand> OnCommandPending;

        public event Action<VoxrCommand> OnCommandConfirmed;

        public event Action<VoxrCommand> OnCommandCancelled;

#if UNITY_EDITOR
        // Raised every time diagnostics are published, so editor-side collectors can capture
        // every utterance losslessly instead of polling LastMatchDiagnostics (which drops
        // entries whenever two utterances land between polls).
        internal static event Action<VoxrCommandRecogniser, VoxrMatchDiagnostics> DiagnosticsPublished;

        VoxrMatchDiagnostics _lastMatchDiagnostics;

        internal VoxrMatchDiagnostics LastMatchDiagnostics
        {
            get => _lastMatchDiagnostics;
            private set
            {
                _lastMatchDiagnostics = value;
                DiagnosticsPublished?.Invoke(this, value);
            }
        }

        internal string LastPartialResult { get; private set; }
#endif

        VoxrCommandParser _parser;
        readonly GrammarManager _grammar = new GrammarManager();

        // Utterance buffer
        readonly UtteranceBuffer _buffer = new UtteranceBuffer();

        // Set when the buffer holds a complete command that more speech could still extend,
        // so Update waits only prefixHoldSeconds for that continuation (issue #32).
        // Re-derived on every result and cleared whenever the buffer empties, so it never
        // outlives the buffer contents it was derived from.
        bool _eagerHoldArmed;

        // Per-intent debounce
        readonly CommandDebouncer _debouncer = new CommandDebouncer();

        // Command set and slot state
        VoxrSlotDefinition[] _slots;
        VoxrCommandDefinition[] _activeCommands;
        readonly CommandSetManager _setManager = new CommandSetManager();
        readonly DynamicSlotManager _slotManager = new DynamicSlotManager();

        // Pending command state
        readonly PendingCommandHandler _pending = new PendingCommandHandler();

        // Pre-allocated buffer for accepted commands (avoids per-utterance List allocation)
        VoxrCommand[] _acceptedBuf;

        public string[] ActiveSetNames => _setManager.ActiveSetNames;

        public bool HasPendingCommand => _pending.HasPending;

        public VoxrCommand? PendingCommand => _pending.PendingCommand;

        /// <summary>
        /// The ambiguity a pending command is waiting on, or <c>null</c> when there is no pending
        /// command or it is waiting on something else (a confirmation, or a missing argument).
        /// </summary>
        /// <remarks>
        /// <c>HasValue</c> is the reason signal. <c>OnCommandPending</c> carries only the command,
        /// so an integrator subscribed for <c>requiresConfirmation</c> would otherwise treat a
        /// "which did you mean?" as a "are you sure?" and prompt yes/no — which under a
        /// disambiguation does nothing, leaving the pending to time out and fire nothing.
        ///
        /// Only ever non-null with <c>disambiguateSiblingTies</c> enabled. Read it while the
        /// pending is live. The arrays are allocated once at entry and are safe to retain, but
        /// they are the live pending's own arrays rather than copies — do not write to them, as
        /// that would change which word resolves the question and what fires when it does.
        /// </remarks>
        public VoxrPendingAmbiguity? PendingAmbiguity
        {
            get
            {
                var pending = _pending.Current;
                if (
                    !pending.HasValue
                    || pending.Value.Reason != VoxrPendingReason.AwaitingDisambiguation
                    // Guards ChoiceValues, the same field TryHandleConfirmCancel checks, so the
                    // two readers of this record cannot disagree about what "present" means and
                    // hand out an ambiguity whose DiscriminatingValues is null.
                    || pending.Value.ChoiceValues == null
                )
                    return null;

                return new VoxrPendingAmbiguity(
                    pending.Value.Choices,
                    pending.Value.ChoiceValues,
                    pending.Value.ChoicesTruncated
                );
            }
        }

        public void Configure(VoxrSlotDefinition[] slots, VoxrCommandDefinition[] commands)
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

            // Same word list to both: the decoder can return follow-up vocabulary as real
            // tokens, so the parser's coverage rule has to know those words are legitimate
            // rather than charge them as unexplained (issue #65 §5.2).
            _parser = new VoxrCommandParser(_slotManager.BuildEffectiveSlots(_slots), commands,
                coverageWeight,
                GetFollowUpGrammarWords(),
                cancelVocabulary,
                disambiguateSiblingTies
            );
            _grammar.Rebuild(_slots, commands, GetFollowUpGrammarWords());

            if (!freeSpeechMode && speechRecogniser != null && speechRecogniser.IsModelReady)
            {
                speechRecogniser.SetGrammar(_grammar.CurrentJson);
                _grammar.IsApplied = true;
            }
        }

        public void Configure(VoxrSlotDefinition[] slots, VoxrCommandSet[] sets)
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

        public void InjectText(string text, VoxrWord[] words = null)
        {
            Debug.Assert(System.Threading.Thread.CurrentThread.ManagedThreadId == 1,
                "InjectText must be called from the Unity main thread.");

            if (string.IsNullOrWhiteSpace(text))
                return;

            if (_parser == null)
            {
                Debug.LogWarning("[VoxrCommandRecogniser] InjectText called before parser is ready. " +
                    "Call Configure(slots, commands) or Configure(slots, sets) followed by SetActiveSets(...) first.");
                return;
            }

            HandleResult(new VoxrResult(text, words ?? Array.Empty<VoxrWord>()));
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

            _parser = new VoxrCommandParser(_slotManager.BuildEffectiveSlots(_slots), _activeCommands,
                coverageWeight,
                GetFollowUpGrammarWords(),
                cancelVocabulary,
                disambiguateSiblingTies
            );
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
            _eagerHoldArmed = false;
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
        internal bool EagerFlushOnCompleteMatch { set => eagerFlushOnCompleteMatch = value; }
        internal float PrefixHoldSeconds { set => prefixHoldSeconds = value; }
        internal float CommandCooldown { set => commandCooldown = value; }
        internal VoxrSpeechRecogniser SpeechRecogniser
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
                _acceptedBuf = new VoxrCommand[Math.Max(commandCount, 1)];
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

            // Slot assets are optional — an all-literal grammar declares no slots — so an absent
            // or empty array converts with zero of them. Command sets are what carry the
            // commands, so an empty array leaves nothing to convert; warn rather than return in
            // silence when slot assets were assigned, since silence there is indistinguishable
            // from a recogniser that never hears anything.
            if (commandSetAssets == null || commandSetAssets.Length == 0)
            {
                if (slotAssets != null && slotAssets.Length > 0)
                {
                    Debug.LogWarning(
                        "[VoxrCommandRecogniser] Slot assets are assigned but "
                            + "Command Set Assets is empty — skipping Inspector conversion, so no "
                            + "command will be recognised."
                    );
                }
                return;
            }

            int slotCount = slotAssets?.Length ?? 0;
            var slotList = new List<VoxrSlotDefinition>(slotCount);
            for (int i = 0; i < slotCount; i++)
            {
                if (slotAssets[i] == null)
                {
                    Debug.LogWarning($"[VoxrCommandRecogniser] slotAssets[{i}] is null — skipping.");
                    continue;
                }
                slotList.Add(slotAssets[i].ToDefinition());
            }

            var setList = new List<VoxrCommandSet>(commandSetAssets.Length);
            for (int i = 0; i < commandSetAssets.Length; i++)
            {
                if (commandSetAssets[i] == null)
                {
                    Debug.LogWarning(
                        $"[VoxrCommandRecogniser] commandSetAssets[{i}] is null — skipping.");
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
                Debug.LogWarning("[VoxrCommandRecogniser] Free-speech mode is active in a " +
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
            if (_buffer.IsActive && _buffer.ShouldFlush(Time.time, EffectiveBufferWindow))
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
                LastMatchDiagnostics = new VoxrMatchDiagnostics(
                    resolution.Command.RawText ?? "", Array.Empty<VoxrWord>(),
                    new[] { new VoxrMatchAttempt(
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

        void HandleResult(VoxrResult result)
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

            // The verdict belongs to the buffer as it stands, so re-derive it from scratch
            // for every result rather than carrying the previous one forward.
            _eagerHoldArmed = false;

            // Eager flush: if the buffered speech already forms a complete command that
            // cannot be extended or completed by more words, flush now instead of waiting
            // out bufferWindow. If it can still be extended, arm the shorter prefix hold
            // instead. Skipped while a command is pending so confirm/follow-up stays on the
            // timer path.
            if (eagerFlushOnCompleteMatch && !_pending.HasPending)
            {
                var verdict = ProbeEagerCommit();
                if (verdict == EagerCommitVerdict.Commit)
                    FlushBuffer();
                else
                    _eagerHoldArmed = verdict == EagerCommitVerdict.HoldExtendable;
            }
        }

        // How long the buffer waits for more speech. A complete-but-extendable match is
        // only waiting on a continuation the speaker would begin almost immediately, so
        // prefixHoldSeconds may cut that wait short (issue #32) — never lengthen it.
        float EffectiveBufferWindow =>
            _eagerHoldArmed && prefixHoldSeconds > 0f && prefixHoldSeconds < bufferWindow
                ? prefixHoldSeconds
                : bufferWindow;

        void FlushBuffer()
        {
            _eagerHoldArmed = false;
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

        // Speculative check used by HandleResult: peek (don't consume) the buffer and ask
        // the parser whether it already forms one complete, confident command, and whether
        // more speech could still extend it.
        EagerCommitVerdict ProbeEagerCommit()
        {
            string text = _buffer.PeekText();
            if (text.Length == 0)
                return EagerCommitVerdict.None;

            string[] tokens = text.Split(VoxrCommandParser.SplitSeparator,
                StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                return EagerCommitVerdict.None;

            var words = _buffer.GetWordsSpan();
            var wordConfidence = _parser.InstanceBuildWordConfidence(words);
            return _parser.TryEagerCommit(tokens, wordConfidence, minScore, minConfidence);
        }

        void ProcessParsedResults(string text, VoxrWord[] words)
            => ProcessParsedResultsCore(text, words, _parser.InstanceBuildWordConfidence(words));

        void ProcessParsedResults(string text, ReadOnlySpan<VoxrWord> words)
            => ProcessParsedResultsCore(text, words, _parser.InstanceBuildWordConfidence(words));

        void ProcessParsedResultsCore(string text, ReadOnlySpan<VoxrWord> words,
            Dictionary<string, float> wordConfidence)
        {
            // Split once — shared by pending handlers, diagnostics, and the parser.
            string[] tokens = text.Split(VoxrCommandParser.SplitSeparator,
                StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                return;

#if UNITY_EDITOR
            // Editor diagnostics need VoxrWord[] — copy once for the editor path only.
            VoxrWord[] diagWords = words.ToArray();
#endif

            // ---- Step 1: Confirm/cancel check (before parsing) ----
            if (_pending.HasPending)
            {
                var ccResolution = _pending.TryHandleConfirmCancel(
                    tokens,
                    confirmVocabulary,
                    cancelVocabulary,
                    Time.time
                );
                if (ccResolution.Outcome != PendingOutcome.None)
                {
                    InterpretResolution(ccResolution);
#if UNITY_EDITOR
                    // Three-way since the choice arm landed. Answering a disambiguation whose
                    // chosen intent requires confirmation resolves as ReEnteredPending, and a
                    // two-way "confirmed or cancelled" label reported that as "cancelled via
                    // vocabulary" with accepted=false — in LastMatchDiagnostics, the debug window
                    // and the exported session log — while the runtime had in fact advanced to
                    // asking "are you sure?".
                    bool wasConfirmed = ccResolution.Outcome == PendingOutcome.Confirmed;
                    string ccReject;
                    switch (ccResolution.Outcome)
                    {
                        case PendingOutcome.Confirmed:
                            ccReject = null;
                            break;
                        case PendingOutcome.ReEnteredPending:
                            ccReject = "chosen via vocabulary, now awaiting confirmation";
                            break;
                        default:
                            ccReject = "cancelled via vocabulary";
                            break;
                    }
                    LastMatchDiagnostics = new VoxrMatchDiagnostics(
                        text, diagWords,
                        new[] { new VoxrMatchAttempt(
                            ccResolution.Command.Intent, null,
                            ccResolution.Command.Score, minScore,
                            ccResolution.Command.Confidence, minConfidence,
                                null,
                                ccReject,
                            wasConfirmed) },
                        Time.frameCount);
#endif
                    return;
                }
            }

            // ---- Step 2: Follow-up slot-fill attempt (before parsing) ----
            VoxrCommand? followUpResult = null;
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
            //
            // The completeness term (issue #73) has to be here as well as in Step 7, and this
            // is the read that is easy to miss. This flag decides two things below: whether a
            // follow-up slot-fill is preempted by the new utterance (Step 5), and whether a
            // live pending command is cancelled outright (Step 6). A command missing a required
            // argument no longer fires in Step 7, so letting it set this flag would cancel the
            // user's pending command in favour of one that then goes nowhere — losing the
            // half-finished command to an utterance that produces nothing.
            bool hasCompleteNewCommand = false;
            // Snapshot the parser itself alongside its buffer. Both are read across iterations
            // of the Step 7 loop, which raises public events a subscriber may answer by calling
            // Configure (setting _parser to null) or SetActiveSets (installing a parser with
            // differently-sized buffers). One instance for the whole walk, or the two halves of
            // the ResultBuffer/TiedSiblingBuffer parallel-array contract can come from different
            // parsers mid-loop.
            var parser = _parser;
            var resultBuf = parser.ResultBuffer;
            // Snapshotting the parser does not make this loop re-entrant. These are the
            // parser's pooled arrays, not copies, so a subscriber that answers one of the events
            // below by calling InjectText synchronously re-enters ParseInternal on this same
            // instance and overwrites them mid-walk. That is pre-existing and unsupported;
            // handlers should queue rather than inject.
            for (int i = 0; i < resultCount; i++)
            {
                var cmd = resultBuf[i].Command;
                if (
                    cmd.Score >= minScore
                    && (cmd.Confidence < 0f || cmd.Confidence >= minConfidence)
                    && !IsIncomplete(cmd)
                )
                {
                    hasCompleteNewCommand = true;
                    break;
                }
            }

            // ---- Step 5: Arbitrate follow-up vs new command ----
            if (followUpResult.HasValue && !hasCompleteNewCommand)
            {
                // The `Score <= 0` floor both flush paths carry (CompareCandidate's first test
                // and ParseInternal's bestScore check), on the one fire path that never had it
                // (issue #113). scoring.md §1 states the rule without qualification — a
                // candidate scoring zero or less is discarded and never competes — and a merged
                // command reaches a subscriber without ever passing either of those tests, so
                // the rule has to be restated here or it is not the rule.
                //
                // Reachable because ScoreFollowUp and IsIncomplete resolve an intent to
                // different definitions when two are registered under one intent: the former
                // scans the parser's command array and breaks on the FIRST match, the latter
                // reads CommandSetManager's dictionary, which BuildLookup fills last-write-wins.
                // The short definition then calls the command complete while the long one
                // charges it for required slots the matched pattern never had. Floored rather
                // than reconciled deliberately: the floor holds whatever the two disagree
                // about, and a non-positive score is not fireable for any reason.
                //
                // Refusing rather than re-arming. The merged command is complete by slots, so
                // AdvanceSlotFill would install a pending with nothing left to fill — one
                // TryFollowUpSlotFill declines forever and FireAsIs would eventually fire
                // carrying this same score. Leaving the pending untouched keeps pendingTimeout
                // the thing that ends the exchange, and keeps the command it would fire the one
                // that legitimately scored on the first utterance. The utterance is reported
                // unrecognised because it resolved nothing — which is what Step 6 would say for
                // it anyway, a follow-up answer being a slot value rather than a command.
                if (followUpResult.Value.Score <= 0f)
                {
#if UNITY_EDITOR
                    LastMatchDiagnostics = new VoxrMatchDiagnostics(
                        text, diagWords,
                        new[] { new VoxrMatchAttempt(
                            followUpResult.Value.Intent, null,
                            followUpResult.Value.Score, minScore,
                            followUpResult.Value.Confidence, minConfidence,
                            null,
                            $"follow-up re-score {followUpResult.Value.Score:F2} <= 0",
                            false) },
                        Time.frameCount);
#endif
                    OnUnrecognisedSpeech?.Invoke(text);
                    return;
                }

                // A follow-up result is not necessarily a complete command (issue #77). The
                // slot-fill walks the unfilled slots in order, stops at the first one it cannot
                // fill, and returns as soon as ONE new slot is filled — so a pending with two or
                // more unfilled required slots yields a command still missing an argument. Firing
                // it here would fire exactly the shape #73 refuses on the flush path, on the very
                // path #73 routes those commands to, and the re-score does not stand in for the
                // test: ScoreFollowUp re-scores against the matched pattern, so a partly filled
                // command can score alongside a complete one.
                //
                // Keeping the pending alive rather than discarding the fill is what makes this a
                // refusal to fire rather than a refusal to progress: each utterance fills what it
                // can and the command waits for the rest.
                bool followUpIncomplete = IsIncomplete(followUpResult.Value);
                var followUpRes = followUpIncomplete
                    ? _pending.AdvanceSlotFill(followUpResult.Value, Time.time)
                    // The pending's own definition: this path fills a slot on the command that
                    // is already pending, so the winner IS the resolved command. Only the
                    // disambiguation path resolves to a different one.
                    : _pending.Complete(
                        followUpResult.Value,
                        _pending.Current.Value.Definition,
                        Time.time
                    );
#if UNITY_EDITOR
                // Read the re-armed pending BEFORE the resolution is interpreted. Interpreting it
                // invokes OnCommandPending, whose subscribers may cancel, reconfigure, or disable
                // the recogniser — any of which clears the pending and would make this read throw
                // on a Nullable with no value. The rest of the method's diagnostics capture their
                // locals ahead of the events for the same reason.
                string followUpReason = followUpIncomplete
                    ? "still pending (partial: unfilled "
                        + $"[{string.Join(", ", _pending.Current.Value.UnfilledSlots)}])"
                    : null;
#endif
                InterpretResolution(followUpRes);
#if UNITY_EDITOR
                LastMatchDiagnostics = new VoxrMatchDiagnostics(
                    text, diagWords,
                    new[] { new VoxrMatchAttempt(
                        followUpResult.Value.Intent, null, followUpResult.Value.Score,
                        minScore, followUpResult.Value.Confidence, minConfidence,
                            null,
                            followUpReason,
                            !followUpIncomplete
                        ),
                    },
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
                LastMatchDiagnostics = new VoxrMatchDiagnostics(
                    text, diagWords,
                    new[] { new VoxrMatchAttempt(null, null, 0f, minScore, 0f, minConfidence,
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
            var attempts = new List<VoxrMatchAttempt>(resultCount);
#endif

            for (int i = 0; i < resultCount; i++)
            {
                var cmd = resultBuf[i].Command;

                // Below score threshold, OR missing a required argument — either way this is
                // not a command to fire. Check AllowPartialMatch before rejecting.
                //
                // The completeness half is issue #73 and is deliberately independent of
                // minScore: a missing argument is a missing argument at any score. Until now
                // only the arithmetic held these down, and only by coincidence — a five-element
                // pattern with one missed required slot lands on exactly 0.60 and cleared the
                // default gate, firing a command whose argument the handler never receives.
                // TryEagerCommit has refused this shape since #66; this is the same rule on the
                // path that is actually on by default.
                //
                // Routing rather than refusing outright is what makes the two halves one branch:
                // a command opted into AllowPartialMatch now reaches the pending/slot-fill path
                // it was always meant to reach, instead of being fired incomplete for the sole
                // reason that it scored well. With the flag off (the default) it falls through
                // to the reject below, and the utterance is reported unrecognised.
                bool incomplete = IsIncomplete(cmd);
                if (cmd.Score < minScore || incomplete)
                {
                    if (cmd.Score > 0f &&
                        _setManager.TryLookupCommand(cmd.Intent, out var partialDef) &&
                        partialDef.AllowPartialMatch)
                    {
                        var unfilled = _pending.ComputeUnfilledSlots(cmd, partialDef);
                        if (unfilled.Length > 0)
                        {
                            var enterRes = _pending.EnterPending(cmd, partialDef, unfilled,
                                VoxrPendingReason.PartialMatch, Time.time,
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
                    // Report the condition that actually rejected it. An incomplete command can
                    // sit well above minScore, so reusing the score wording here would print a
                    // comparison that is plainly false in the session log and the debug window.
                    attempts.Add(BuildAttempt(cmd, parseDiag, i, tokens, diagWordConf,
                            cmd.Score < minScore
                                ? $"score {cmd.Score:F2} < minScore {minScore:F2}"
                                : "required slot unfilled",
                            false
                        )
                    );
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

                // Sibling tie — ask which intent was meant instead of firing the first-registered
                // one (issue #74 item 3). AFTER the debounce check, because a command on cooldown
                // should not raise a question the speaker then answers into a cooldown; BEFORE
                // the confirmation check, because "which?" precedes "are you sure?" and Complete
                // sequences the two for free once the choice resolves.
                if (
                    disambiguateSiblingTies
                    && TryBuildAmbiguity(
                        parser,
                        i,
                        tokens,
                        wordConfidence,
                        out var choices,
                        out var choiceValues,
                        out var choiceDefs,
                        out bool choicesTruncated
                    )
                )
                {
                    // Without this the utterance reaches acceptedCount == 0 with the flag clear
                    // and raises OnUnrecognisedSpeech — telling the integrator the speech was not
                    // understood in the same frame it was asked to prompt about it.
                    anyThresholdFiltered = true;

                    var enterAmbRes = _pending.EnterPending(
                        cmd,
                        choiceDefs[0],
                        Array.Empty<string>(),
                        VoxrPendingReason.AwaitingDisambiguation,
                        Time.time,
                        out var cancelAmbRes,
                        choices,
                        choiceValues,
                        choiceDefs,
                        choicesTruncated
                    );
                    InterpretResolution(cancelAmbRes);
                    InterpretResolution(enterAmbRes);
#if UNITY_EDITOR
                    attempts.Add(
                        BuildAttempt(
                            cmd,
                            parseDiag,
                            i,
                            tokens,
                            diagWordConf,
                            $"entered pending (awaiting disambiguation, {choices.Length} choices)",
                            false
                        )
                    );
#endif
                    continue;
                }

                // Check RequiresConfirmation — enter pending instead of firing
                if (_setManager.TryLookupCommand(cmd.Intent, out var confirmDef) &&
                    confirmDef.RequiresConfirmation)
                {
                    var enterConfRes = _pending.EnterPending(cmd, confirmDef,
                        Array.Empty<string>(), VoxrPendingReason.AwaitingConfirmation, Time.time,
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
            LastMatchDiagnostics = new VoxrMatchDiagnostics(
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
                var batch = new VoxrCommand[acceptedCount];
                Array.Copy(_acceptedBuf, batch, acceptedCount);
                OnCommandsRecognised.Invoke(batch);
            }

            // Clear stale references
            Array.Clear(_acceptedBuf, 0, acceptedCount);
        }

        // Builds the choice list for result i, or answers false and asks nothing.
        //
        // `parser` is passed in rather than read from the field, and that is load-bearing: the
        // Step 7 loop raises public events between iterations, and a subscriber is allowed to
        // call Configure — which sets _parser to null — or SetActiveSets, which installs a new
        // parser whose buffers are sized to the new command count. The loop already snapshots
        // ResultBuffer for exactly this reason; reading _parser live here reintroduced the
        // hazard that snapshot exists to remove.
        //
        // Allocates — three small arrays and one VoxrCommand per alternative — and that is
        // deliberate: this runs once per AMBIGUITY, never per candidate, and everything it
        // produces crosses into a public event where a subscriber can retain it. The parse path
        // itself stays allocation-free (the parser records rivals into preallocated buffers);
        // this is the boundary where that stops being true, following the same rule
        // PendingCommandHandler already applies to anything reaching a subscriber.
        bool TryBuildAmbiguity(
            VoxrCommandParser parser,
            int i,
            string[] tokens,
            Dictionary<string, float> wordConfidence,
            out VoxrCommand[] choices,
            out string[] choiceValues,
            out VoxrCommandDefinition[] choiceDefinitions,
            out bool truncated
        )
        {
            choices = null;
            choiceValues = null;
            choiceDefinitions = null;

            var record = parser.TiedSiblingBuffer[i];
            truncated = record.Truncated;
            if (record.RivalCount == 0)
                return false;

            var winner = parser.ResultBuffer[i].Command;
            if (!_setManager.TryLookupCommand(winner.Intent, out var winnerDef))
                return false;

            // Index 0 is always the candidate that would have fired with the flag off, so the
            // order an integrator renders is the order registration would have produced.
            // Locals, not pooled fields. This runs once per ambiguity and allocates three
            // arrays at the end regardless, so pooling bought nothing measurable while keeping
            // the winner's and rivals' commands alive after the question resolved — the exact
            // staleness the Step 7 tail clears out of _acceptedBuf.
            var choiceBuf = new List<VoxrCommand>(1 + record.RivalCount);
            var valueBuf = new List<string>(1 + record.RivalCount);
            var defBuf = new List<VoxrCommandDefinition>(1 + record.RivalCount);
            choiceBuf.Add(winner);
            valueBuf.Add(record.WinnerValue);
            defBuf.Add(winnerDef);

            for (int n = 0; n < record.RivalCount; n++)
            {
                // A chosen alternative is NOT re-tested against the debounce, and that is a
                // decision rather than an omission. A review found that the Step 7 cooldown
                // check tests only the winner, and gating each rival here was tried and
                // reverted: on a two-way set it drops the only rival, the choice list falls
                // below two, and the winner fires — silently degrading to the coin flip this
                // feature exists to remove, with the truncation signal discarded on the way out.
                //
                // It also could not have been doing its job. An answer always waits out
                // bufferWindow (the eager path is skipped while a pending is live), so the
                // earliest a choice can fire is now + bufferWindow, while exclusion requires
                // now - lastFire < commandCooldown. At the shipped 0.5s and 0.3s the cooldown
                // has always expired before the answer could fire. And the bias runs backwards:
                // the intent on cooldown is the one the speaker just used.
                //
                // The pre-existing confirmation path already settles the principle — it enters
                // pending after the debounce check and fires on confirm without re-checking. A
                // deliberate answer to a question the recogniser asked is not the duplicate
                // VOSK result CommandDebouncer exists to suppress.
                //
                // Confidence needs no test either: a rival's span differs from the winner's only
                // by trailing [unk], which ComputeConfidence skips, so their confidences are
                // equal by construction and the winner already cleared the floor.
                //
                // Unreachable today, and kept anyway — the same treatment AdvanceSlotFill's
                // array carry gets. Every construction site builds the parser and the set
                // manager's lookup from ONE command array (Configure passes the same array to
                // both; SetActiveSets takes what Activate returns, and Activate calls
                // BuildLookup on exactly that), so an intent the parser can report is always
                // resolvable. A test asserting this branch was written and then removed: it
                // registered the rival in an inactive set, which does not reach the shape —
                // the parser is rebuilt from the ACTIVE commands, so it stops seeing the tie
                // at all. The guard is what keeps that argument true if either end changes.
                if (
                    !_setManager.TryLookupCommand(parser.SiblingRivalIntent(i, n), out var rivalDef)
                )
                {
                    // Nameable in a prompt but not fireable, so not offered — and reported,
                    // because an answer the speaker could have given is going unoffered.
                    truncated = true;
                    continue;
                }

                choiceBuf.Add(parser.BuildSiblingRivalCommand(i, n, tokens, wordConfidence));
                valueBuf.Add(parser.TiedSiblingRivalAt(i, n).Value);
                defBuf.Add(rivalDef);
            }

            // Fewer than two survivors is not a question. Fall through and fire the winner, as
            // the flag-off path would.
            if (choiceBuf.Count < 2)
                return false;

            choices = choiceBuf.ToArray();
            choiceValues = valueBuf.ToArray();
            choiceDefinitions = defBuf.ToArray();
            return true;
        }

        // Whether a command is missing one of its own required arguments (issue #73). The flush
        // path's completeness condition, and the counterpart to the two COMPLETENESS conditions
        // TryEagerCommit enforces (#66, #70) — that gate also refuses on an ambiguous sibling
        // tie (#74), which is not a completeness question and has no flush-side counterpart
        // here. Issue #77 added the third caller: the follow-up slot-fill exit,
        // whose input is not a parse result at all but a pending command merged with a fill.
        //
        // An intent with no definition in the active sets is treated as complete: we cannot read
        // a pattern we do not have, and inventing a refusal there would silence commands for a
        // reason unrelated to their arguments. In practice the lookup only fails if the active
        // set changed between the parse and this loop.
        bool IsIncomplete(VoxrCommand cmd)
        {
            return _setManager.TryLookupCommand(cmd.Intent, out var def)
                && VoxrCommandParser.HasUnfilledRequiredSlot(cmd, def);
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

            VoxrFollowUpVocabulary.AddPhraseWords(words, VoxrFollowUpVocabulary.DefaultConfirm);
            VoxrFollowUpVocabulary.AddPhraseWords(words, VoxrFollowUpVocabulary.DefaultCancel);

            if (confirmVocabulary != null)
                VoxrFollowUpVocabulary.AddPhraseWords(words, confirmVocabulary);
            if (cancelVocabulary != null)
                VoxrFollowUpVocabulary.AddPhraseWords(words, cancelVocabulary);

            var result = new string[words.Count];
            words.CopyTo(result);
            return result;
        }

        // Test-only setters/getters
        internal float PendingTimeout { set => pendingTimeout = value; }
        internal VoxrPendingTimeoutBehavior PendingTimeoutBehavior
        {
            set => pendingTimeoutBehavior = value;
        }
        internal string[] ConfirmVocabulary { set => confirmVocabulary = value; }
        internal string[] CancelVocabulary { set => cancelVocabulary = value; }

        // Like cancelVocabulary, this is frozen into the parser at Configure/RebuildParser time,
        // so a test must set it BEFORE Configure. Getting that wrong is invisible here — the
        // parser records ties whenever the flag is set OR UNITY_EDITOR is defined, and every
        // Unity test runs in the Editor — so it shows up only as the flush not routing.
        internal bool DisambiguateSiblingTies
        {
            set => disambiguateSiblingTies = value;
        }
        internal string TestGrammarJson => _grammar.CurrentJson;
        internal bool TestGrammarRebuildDeferred => _grammar.GrammarRebuildDeferred;
        internal float TestEffectiveBufferWindow => EffectiveBufferWindow;

        // Read-only, and read by the completeness tests (issue #76) rather than copied as a
        // literal. Those tests assert that a candidate CLEARS this gate, so that the refusal
        // they then observe can only have come from the completeness term. Against a hard-coded
        // 0.60 that assertion cannot fail: raising the serialized default would drop the
        // candidates below the gate, reject them on score, and leave the tests passing without
        // ever reaching the branch they exist to pin.
        internal float MinScore => minScore;

        internal void TestForceTimeoutNow()
        {
            if (!_pending.HasPending) return;
            var current = _pending.Current.Value;
            current.CreatedTime = -1000f;
            _pending.ForceSetForTest(current);
            SendMessage("Update", SendMessageOptions.DontRequireReceiver);
        }

#if UNITY_EDITOR
        internal VoxrPendingCommand? EditorPendingCommand => _pending.Current;

        void HandlePartialResult(string text)
        {
            LastPartialResult = text;
        }

        VoxrMatchAttempt BuildAttempt(VoxrCommand cmd,
            VoxrCommandParser.ParseDiagnosticEntry[] parseDiag, int index,
            string[] tokens, Dictionary<string, float> wordConf,
            string rejectReason, bool isAccepted)
        {
            string pattern = null;
            string tiedRival = null;
            bool tiedRivalIsSibling = false;
            VoxrDiagnosticSlotMatch[] diagSlots = Array.Empty<VoxrDiagnosticSlotMatch>();

            if (parseDiag != null && index < parseDiag.Length)
            {
                pattern = parseDiag[index].PatternString;
                tiedRival = parseDiag[index].DescribeTiedRival();
                tiedRivalIsSibling = parseDiag[index].TiedRivalIsSibling;

                if (cmd.Slots.Length > 0 && parseDiag[index].SlotStartWords != null)
                {
                    int slotCount = Math.Min(cmd.Slots.Length, parseDiag[index].SlotStartWords.Length);
                    diagSlots = new VoxrDiagnosticSlotMatch[slotCount];
                    for (int s = 0; s < slotCount; s++)
                    {
                        int sw = parseDiag[index].SlotStartWords[s];
                        int ew = parseDiag[index].SlotEndWords[s];
                        float slotConf = VoxrCommandParser.ComputeConfidence(tokens, sw, ew, wordConf);
                        diagSlots[s] = new VoxrDiagnosticSlotMatch(
                            cmd.Slots[s].Name, cmd.Slots[s].Value, sw, ew, slotConf);
                    }
                }
            }

            return new VoxrMatchAttempt(
                cmd.Intent, pattern, cmd.Score, minScore,
                cmd.Confidence, minConfidence, diagSlots,
                rejectReason,
                isAccepted,
                tiedRival,
                tiedRivalIsSibling
            );
        }
#endif
    }
}
