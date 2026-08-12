// ============================================================================
// Purpose:  MonoBehaviour orchestrating VOSK speech recognition lifecycle and result dispatch
// Layer:    Runtime
// Owns:     VoxrSpeechRecogniser (public MonoBehaviour)
// Depends:  VoxrResult, VoxrWord, VoxrBridgeErrorCode, EditorMicBackend, BridgeNative, ModelExtractor
// ============================================================================
using System;
using System.Collections;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UnityEngine;
using VoXR.Native;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

namespace VoXR
{
    [AddComponentMenu("VoXR/Speech Recogniser")]
    public class VoxrSpeechRecogniser : MonoBehaviour
    {
        [SerializeField]
        string modelRelativePath = "vosk-model-small-en-us-0.15";

        [SerializeField]
        float sampleRate = 16000f;

        [Tooltip(
            "AGC target audio level in dBFS. Higher values (e.g. -12) produce a louder "
                + "signal for VOSK; lower values (e.g. -24) are more conservative. "
                + "The default of -18 dBFS works well for typical speech on Quest 3."
        )]
        [SerializeField]
        float micGainTargetDb = -18f;

        public event Action<string> OnPartialResult;
        public event Action<string> OnFinalResult;

        public event Action<VoxrResult> OnResult;

        public event Action<VoxrBridgeErrorCode, string> OnError;
        public event Action OnModelReady;

        // Process-global bridge ownership (issue #57). The native bridge is
        // file-scope C++ state with no handle anywhere in its ABI
        // (NativeBridge~/src/vosk_bridge.cpp), so a process has exactly one
        // bridge however many components reference it. Ownership is claimed by
        // whichever component initialises it and released when that component
        // releases or is destroyed; any other instance stays inert towards the
        // bridge instead of silently sharing it — which used to mean inheriting
        // the owner's model path, sample rate and gain, and freeing the owner's
        // recognizer from its own OnDestroy.
        static VoxrSpeechRecogniser s_bridgeOwner;

        bool OwnsBridge => ReferenceEquals(s_bridgeOwner, this);

        // Unity's == null overload is deliberate: a destroyed owner compares
        // equal to null here, so its claim lapses even if its OnDestroy never
        // ran (a subclass can hide the base's private OnDestroy) and even when
        // the static survives a play-mode exit with domain reload disabled.
        bool BridgeOwnedByOther => s_bridgeOwner != null && !OwnsBridge;

        bool _bridgeAvailable = true;
        bool _initialising;
        bool _isRecognising;

#if UNITY_EDITOR_WIN
        EditorMicBackend _editorBackend;

        // Cached method-group → delegate to avoid per-frame allocation when handing
        // DispatchJsonResult to the editor backend's Tick/Stop.
        EditorJsonDispatcher _editorDispatcher;
#endif

#if UNITY_EDITOR
        internal VoxrResult EditorLastResult { get; private set; }
#endif

#if UNITY_EDITOR_WIN
        internal float EditorPreAgcRms => _editorBackend?.PreAgcRms ?? 0f;
        internal float EditorPostAgcRms => _editorBackend?.PostAgcRms ?? 0f;
        internal float EditorAgcGain => _editorBackend?.AgcGain ?? 1f;
#endif

        public bool IsModelReady { get; private set; }

        public bool IsInitialised
        {
            get
            {
                // Instance-backed, which is what issue #57 asked for: the native
                // getter answers for the *process*, so without this a component
                // that initialised nothing still reported true — and then
                // InitialiseAsync's `if (IsInitialised) return;` handed it the
                // owner's configuration. Keyed on ownership rather than on
                // "someone else owns it" so the answer stays honest even when the
                // bridge is initialised with no live claimant.
                if (!OwnsBridge)
                    return false;
#if UNITY_EDITOR_WIN
                return _editorBackend != null && _editorBackend.IsInitialised;
#else
                if (!_bridgeAvailable)
                    return false;
                try
                {
                    return BridgeNative.vosk_bridge_is_initialised() == 1;
                }
                catch (DllNotFoundException)
                {
                    MarkBridgeUnavailable();
                    return false;
                }
#endif
            }
        }

        public bool IsRecognising => IsRecognisingCore;

        // Test seam (issue #49). VoxrPushToTalkController's lifecycle state machine
        // reads recognition state and drives start/stop only through these three
        // internal virtual members, so a test double can subclass this component and
        // count the calls — "resume must not double-start" is a call-count assertion
        // no IsRecognising reading can express. Internal keeps the public surface
        // free of an extension point; overriding needs the InternalsVisibleTo grant
        // in Runtime/AssemblyInfo.cs.
        internal virtual bool IsRecognisingCore
        {
            get
            {
                // vosk_bridge_is_running() reports the process, not this
                // instance; only the owner can be the one running (#57).
                if (!OwnsBridge)
                    return false;
#if UNITY_EDITOR_WIN
                return _editorBackend != null && _editorBackend.IsRunning;
#else
                if (!_bridgeAvailable)
                    return false;
                try
                {
                    return BridgeNative.vosk_bridge_is_running() == 1;
                }
                catch (DllNotFoundException)
                {
                    MarkBridgeUnavailable();
                    return false;
                }
#endif
            }
        }

        public void Initialise()
        {
            _ = InitialiseAsync();
        }

        public async Task InitialiseAsync()
        {
            if (!_bridgeAvailable)
                return;
            if (_initialising)
                return;
            if (IsInitialised)
                return;
            if (RejectIfBridgeOwnedByOther(nameof(InitialiseAsync)))
                return;

            // Claim synchronously, before the first await. Unity runs this on the
            // main thread, so a check-and-set with no await between the two halves
            // cannot interleave with a second component's InitialiseAsync —
            // claiming after the model-extraction await would let both instances
            // past the check above (#57).
            s_bridgeOwner = this;
            bool claimHeld = false;

            _initialising = true;
            try
            {
                string modelPath = await ModelExtractor.ExtractModelAsync(
                    modelRelativePath,
                    FireError
                );
                if (modelPath == null)
                    return;

                // The claim was taken before the await above, and an async
                // continuation outlives the component that started it — a destroy
                // or an explicit ReleaseNativeResources during model extraction
                // hands the claim away while this method is still in flight.
                // Initialising anyway would leave the bridge initialised with no
                // owner, the one state in which every guard here is inert (#57).
                if (!OwnsBridge)
                    return;

#if UNITY_EDITOR_WIN
                _editorBackend = new EditorMicBackend();
                bool editorOk = await _editorBackend.InitialiseAsync(
                    modelPath,
                    sampleRate,
                    micGainTargetDb,
                    FireError
                );
                // Re-checked again: the backend's own load awaits too.
                if (editorOk && OwnsBridge)
                {
                    claimHeld = true;
                    IsModelReady = true;
                    OnModelReady?.Invoke();
                }
                else
                {
                    // A backend that came up after the claim was handed away owns
                    // a vosk model, a recognizer and a mic handle that only
                    // Release() frees, and nothing else will ever reach it.
                    if (editorOk)
                        _editorBackend?.Release();
                    _editorBackend = null;
                }
                return;
#else
                int result = BridgeNative.vosk_bridge_init(modelPath, sampleRate, micGainTargetDb);
                CheckBridgeError(result, "Initialise");

                if (result == 0)
                {
                    claimHeld = true;
                    IsModelReady = true;
                    OnModelReady?.Invoke();
                }
#endif
            }
            catch (DllNotFoundException)
            {
                MarkBridgeUnavailable();
            }
            catch (Exception ex)
            {
                FireError(VoxrBridgeErrorCode.ModelLoadFailed, $"Initialise failed: {ex.Message}");
            }
            finally
            {
                _initialising = false;
                // A failed attempt must not hold the bridge hostage — release the
                // claim so a sibling component can still initialise. Guarded on
                // OwnsBridge because ReleaseNativeResources may have run during
                // one of the awaits above and already cleared it (#57).
                if (!claimHeld && OwnsBridge)
                    s_bridgeOwner = null;
            }
        }

        public void ReleaseNativeResources()
        {
#if UNITY_EDITOR_WIN
            // Instance-owned, so it is released whether or not this component owns
            // the claim: skipping it for a non-owner would strand the vosk model,
            // recognizer and mic handle that only Release() frees.
            _editorBackend?.Release();
            _editorBackend = null;
#else
            // The process-global half is the opposite: vosk_bridge_destroy() takes
            // no handle, so calling it while another component owns the bridge
            // frees *its* recognizer and model — the cross-instance teardown of
            // #57, reached from this component's own OnDestroy.
            if (_bridgeAvailable && !BridgeOwnedByOther)
            {
                try
                {
                    BridgeNative.vosk_bridge_destroy();
                }
                catch (DllNotFoundException)
                {
                    MarkBridgeUnavailable();
                }
            }
#endif
            _isRecognising = false;
            IsModelReady = false;
            if (OwnsBridge)
                s_bridgeOwner = null;
        }

        public void StartRecognition() => StartRecognitionCore();

        internal virtual void StartRecognitionCore()
        {
            _ = StartRecognitionAsync();
        }

        public async Task StartRecognitionAsync()
        {
            if (!_bridgeAvailable)
                return;

            if (!IsInitialised)
                await InitialiseAsync();

            if (!IsInitialised)
                return;

#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
            {
                StartCoroutine(RequestMicPermissionThenStart());
                return;
            }
#endif

            StartRecognitionInternal();
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        IEnumerator RequestMicPermissionThenStart()
        {
            var callbacks = new PermissionCallbacks();
            bool responded = false;
            bool granted = false;

            callbacks.PermissionGranted += _ =>
            {
                granted = true;
                responded = true;
            };
            callbacks.PermissionDenied += _ =>
            {
                responded = true;
            };
            callbacks.PermissionDeniedAndDontAskAgain += _ =>
            {
                responded = true;
            };

            Permission.RequestUserPermission(Permission.Microphone, callbacks);

            while (!responded)
                yield return null;

            if (granted)
                StartRecognitionInternal();
            else
                FireError(
                    VoxrBridgeErrorCode.PermissionDenied,
                    "Microphone permission denied by user."
                );
        }
#endif

        void StartRecognitionInternal()
        {
#if UNITY_EDITOR_WIN
            if (_editorBackend != null && _editorBackend.Start(FireError))
                _isRecognising = true;
#else
            try
            {
                int result = BridgeNative.vosk_bridge_start();
                if (result == 0)
                    _isRecognising = true;
                else
                    CheckBridgeError(result, "StartRecognition");
            }
            catch (DllNotFoundException)
            {
                MarkBridgeUnavailable();
            }
#endif
        }

        public void StopRecognition() => StopRecognitionCore();

        internal virtual void StopRecognitionCore()
        {
            _isRecognising = false;
            // Quiet no-op for a non-owner: vosk_bridge_stop() would halt the
            // owner's recognition. The loud report already happened when this
            // component tried to initialise (#57), and stop is called often
            // enough (every push-to-talk release) that repeating it would spam.
            if (BridgeOwnedByOther)
                return;
#if UNITY_EDITOR_WIN
            if (_editorBackend != null)
            {
                // EditorMicBackend.Stop dispatches vosk_recognizer_final_result inline
                // through the supplied delegate, so the last utterance reaches listeners
                // without needing a drain step.
                _editorBackend.Stop(EnsureEditorDispatcher());
            }
#else
            if (!_bridgeAvailable)
                return;
            try
            {
                BridgeNative.vosk_bridge_stop();
                // vosk_bridge_stop joins the recognition thread, which pushes
                // vosk_recognizer_final_result before exit (vosk_bridge.cpp:130-134).
                // Drain it now for the same reason as the Editor branch above.
                IntPtr ptr;
                while (
                    (ptr = BridgeNative.vosk_bridge_get_result(out int isFinalInt, out int length))
                    != IntPtr.Zero
                )
                {
                    // Span wraps g_current_result.json, valid only until the next
                    // vosk_bridge_get_result call. Consume inline; do not store.
                    DispatchJsonResult(BridgeNative.SpanFromPtr(ptr, length), isFinalInt == 1);
                }
            }
            catch (DllNotFoundException)
            {
                MarkBridgeUnavailable();
            }
#endif
        }

        public void ResetRecogniser()
        {
            if (RejectIfBridgeOwnedByOther(nameof(ResetRecogniser)))
                return;
#if UNITY_EDITOR_WIN
            _editorBackend?.Reset();
#else
            if (!_bridgeAvailable)
                return;
            try
            {
                int result = BridgeNative.vosk_bridge_reset();
                CheckBridgeError(result, "ResetRecogniser");
            }
            catch (DllNotFoundException)
            {
                MarkBridgeUnavailable();
            }
#endif
        }

        public void SetGrammar(string grammarJson)
        {
            if (RejectIfBridgeOwnedByOther(nameof(SetGrammar)))
                return;
#if UNITY_EDITOR_WIN
            _editorBackend?.SetGrammar(grammarJson, FireError);
#else
            if (!_bridgeAvailable)
                return;
            try
            {
                int result = BridgeNative.vosk_bridge_set_grammar(grammarJson);
                CheckBridgeError(result, "SetGrammar");
            }
            catch (DllNotFoundException)
            {
                MarkBridgeUnavailable();
            }
#endif
        }

        public void InjectResult(string text, VoxrWord[] words = null)
        {
            AssertMainThread(nameof(InjectResult));
            DispatchFinalResult(text, words ?? Array.Empty<VoxrWord>());
        }

        public void InjectPartialResult(string text)
        {
            AssertMainThread(nameof(InjectPartialResult));
            OnPartialResult?.Invoke(text);
        }

        // ~average English word duration; used to give simulated words a plausible non-zero span.
        const float SimulatedWordDurationSeconds = 0.3f;

        public static VoxrWord[] CreateSimulatedWords(string text, float confidence = 1.0f)
        {
            if (string.IsNullOrWhiteSpace(text))
                return Array.Empty<VoxrWord>();

            var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var words = new VoxrWord[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
                words[i] = new VoxrWord(
                    tokens[i],
                    confidence,
                    i * SimulatedWordDurationSeconds,
                    (i + 1) * SimulatedWordDurationSeconds
                );
            return words;
        }

        void DispatchFinalResult(string text, VoxrWord[] words)
        {
            var result = new VoxrResult(text, words);
#if UNITY_EDITOR
            EditorLastResult = result;
#endif
            OnFinalResult?.Invoke(text);
            OnResult?.Invoke(result);
        }

        static void AssertMainThread(string method)
        {
            Debug.Assert(
                System.Threading.Thread.CurrentThread.ManagedThreadId == 1,
                $"{method} must be called from the Unity main thread."
            );
        }

        void Update()
        {
#if UNITY_EDITOR_WIN
            if (_editorBackend != null && _isRecognising)
            {
                _editorBackend.Tick(FireError, EnsureEditorDispatcher());
                if (!_editorBackend.IsRunning)
                    _isRecognising = false;
                return;
            }
#endif
            if (!_bridgeAvailable || !_isRecognising)
                return;

            try
            {
                bool hadActivity = false;
                IntPtr ptr;
                while (
                    (ptr = BridgeNative.vosk_bridge_get_result(out int isFinalInt, out int length))
                    != IntPtr.Zero
                )
                {
                    hadActivity = true;
                    // Span wraps g_current_result.json, valid only until the next
                    // vosk_bridge_get_result call. Consume inline; do not store,
                    // capture by closure, or hold across an await/coroutine yield.
                    DispatchJsonResult(BridgeNative.SpanFromPtr(ptr, length), isFinalInt == 1);
                }

                // Sync cached flag when native side stops (e.g. audio error).
                // Only probe native when the queue had activity this frame.
                if (hadActivity && !IsRecognising)
                    _isRecognising = false;
            }
            catch (DllNotFoundException)
            {
                MarkBridgeUnavailable();
            }
        }

#if UNITY_EDITOR_WIN
        EditorJsonDispatcher EnsureEditorDispatcher() => _editorDispatcher ??= DispatchJsonResult;

        // Test-only access to the editor playback seam (design §6.1): tests drive
        // EditorBackend.StartPlayback/TickPlayback with EditorDispatcher so replayed
        // audio flows through the same dispatch chain as live microphone results.
        internal EditorMicBackend EditorBackend => _editorBackend;
        internal EditorJsonDispatcher EditorDispatcher => EnsureEditorDispatcher();
#endif

        // Parses a VOSK JSON byte span and fires the appropriate event(s).
        // Shared by the Android native drain loop and the Editor backend.
        // The span wraps native memory whose lifetime ends at the next bridge
        // result call; this method consumes it synchronously and never stores it.
        void DispatchJsonResult(ReadOnlySpan<byte> json, bool isFinal)
        {
            if (json.Length == 0)
                return;

            // TODO: tighter check would be IndexOf("\"error\":") to avoid a false
            // positive when a recognised word happens to be "error". Pre-existing
            // limitation, not introduced by the byte-span refactor.
            if (json.IndexOf(VoxrJsonParser.KeyError.AsSpan()) >= 0)
            {
                var code = VoxrJsonParser.ParseErrorCode(json);
                FireError(code, code.ToDescription());
                return;
            }

            string text = VoxrJsonParser.ParseTextFromJson(json, isFinal);

            if (isFinal)
            {
                VoxrWord[] words = Array.Empty<VoxrWord>();

                bool needFullParse = OnResult != null;
#if UNITY_EDITOR
                needFullParse = true;
#endif
                if (needFullParse)
                    words = VoxrJsonParser.ParseWordsFromJson(json);

                DispatchFinalResult(text, words);
            }
            else
            {
                OnPartialResult?.Invoke(text);
            }
        }

        void OnDestroy()
        {
            ReleaseNativeResources();
        }

        void FireError(VoxrBridgeErrorCode code, string message)
        {
            OnError?.Invoke(code, message);
        }

        void CheckBridgeError(int returnCode, string context)
        {
            if (returnCode == 0)
                return;

            var code = (VoxrBridgeErrorCode)returnCode;
            string detail = BridgeNative.GetLastError();
            string message = string.IsNullOrEmpty(detail)
                ? $"{context}: {code.ToDescription()}"
                : $"{context}: {detail}";
            FireError(code, message);
        }

        // Reports true — and fails loudly — when another live component owns the
        // process-global bridge, so this one must not drive it (#57). Debug.LogError
        // as well as OnError because the silent-sharing this replaces was invisible
        // to a developer who had not subscribed to OnError.
        bool RejectIfBridgeOwnedByOther(string context)
        {
            if (!BridgeOwnedByOther)
                return false;

            string message =
                $"{context}: another VoxrSpeechRecogniser (on GameObject "
                + $"'{s_bridgeOwner.name}') already owns the native bridge. VoXR supports "
                + "one active recogniser per process — the bridge is file-scope native "
                + "state with no per-instance handle, so a second instance would inherit "
                + "the first's model path, sample rate and gain, and free its recognizer "
                + "on destroy. Call ReleaseNativeResources() on the existing recogniser "
                + "first — that frees the claim synchronously, whereas Object.Destroy "
                + "only frees it at the end of the frame.";
            Debug.LogError(message, this);
            FireError(VoxrBridgeErrorCode.AlreadyInitialised, message);
            return true;
        }

        void MarkBridgeUnavailable()
        {
            if (!_bridgeAvailable)
                return;
            _bridgeAvailable = false;
            _isRecognising = false;
            FireError(
                VoxrBridgeErrorCode.ModelLoadFailed,
                "Native bridge library (libvosk-bridge) not found. "
                    + "Ensure the native plugins are built and placed in Runtime/Plugins/Android/arm64-v8a/."
            );
        }
    }
}
