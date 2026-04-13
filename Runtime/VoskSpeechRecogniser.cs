// ============================================================================
// Purpose:  MonoBehaviour orchestrating VOSK speech recognition lifecycle and result dispatch
// Layer:    Runtime
// Owns:     VoskSpeechRecogniser (public MonoBehaviour)
// Depends:  VoskResult, VoskWord, VoskAlternative, VoskBridgeErrorCode, EditorMicBackend, BridgeNative, ModelExtractor
// ============================================================================
using System;
using System.Collections;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UnityEngine;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif
using VoskXR.Native;

namespace VoskXR
{
    [AddComponentMenu("VOSK XR/Speech Recogniser")]
    public class VoskSpeechRecogniser : MonoBehaviour
    {
        [SerializeField] string modelRelativePath = "vosk-model-small-en-us-0.15";
        [SerializeField] float sampleRate = 16000f;

        [Tooltip("AGC target audio level in dBFS. Higher values (e.g. -12) produce a louder " +
                 "signal for VOSK; lower values (e.g. -24) are more conservative. " +
                 "The default of -18 dBFS works well for typical speech on Quest 3.")]
        [SerializeField] float micGainTargetDb = -18f;

        [Tooltip("Number of alternative hypotheses to return per utterance. " +
                 "0 (default) disables alternatives. When > 0, OnResult includes " +
                 "ranked alternative transcriptions that help diagnose which words " +
                 "VOSK is uncertain about.")]
        [SerializeField] int maxAlternatives = 0;

        public event Action<string> OnPartialResult;
        public event Action<string> OnFinalResult;

        /// <summary>
        /// Raised for each final recognition result with per-word confidence scores
        /// and timing data. Subscribe to this when you need to inspect which words
        /// VOSK is confident about and which it is not.
        /// </summary>
        public event Action<VoskResult> OnResult;

        public event Action<VoskBridgeErrorCode, string> OnError;
        public event Action OnModelReady;

        bool _bridgeAvailable = true;
        bool _initialising;
        bool _isRecognising;

#if UNITY_EDITOR_WIN
        EditorMicBackend _editorBackend;
#endif

#if UNITY_EDITOR
        internal VoskResult EditorLastResult { get; private set; }
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
#if UNITY_EDITOR_WIN
                return _editorBackend != null && _editorBackend.IsInitialised;
#else
                if (!_bridgeAvailable) return false;
                try { return BridgeNative.vosk_bridge_is_initialised() == 1; }
                catch (DllNotFoundException) { MarkBridgeUnavailable(); return false; }
#endif
            }
        }

        public bool IsRecognising
        {
            get
            {
#if UNITY_EDITOR_WIN
                return _editorBackend != null && _editorBackend.IsRunning;
#else
                if (!_bridgeAvailable) return false;
                try { return BridgeNative.vosk_bridge_is_running() == 1; }
                catch (DllNotFoundException) { MarkBridgeUnavailable(); return false; }
#endif
            }
        }

        public void Initialise()
        {
            _ = InitialiseAsync();
        }

        public async Task InitialiseAsync()
        {
            if (!_bridgeAvailable) return;
            if (_initialising) return;
            if (IsInitialised) return;

            _initialising = true;
            try
            {
                string modelPath = await ModelExtractor.ExtractModelAsync(modelRelativePath, FireError);
                if (modelPath == null)
                    return;

#if UNITY_EDITOR_WIN
                _editorBackend = new EditorMicBackend();
                bool editorOk = await _editorBackend.InitialiseAsync(
                    modelPath, sampleRate, micGainTargetDb, maxAlternatives, FireError);
                if (editorOk)
                {
                    IsModelReady = true;
                    OnModelReady?.Invoke();
                }
                else
                {
                    _editorBackend = null;
                }
                return;
#else
                int result = BridgeNative.vosk_bridge_init(modelPath, sampleRate, micGainTargetDb,
                    maxAlternatives);
                CheckBridgeError(result, "Initialise");

                if (result == 0)
                {
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
                FireError(VoskBridgeErrorCode.ModelLoadFailed, $"Initialise failed: {ex.Message}");
            }
            finally
            {
                _initialising = false;
            }
        }

        public void ReleaseNativeResources()
        {
#if UNITY_EDITOR_WIN
            _editorBackend?.Release();
            _editorBackend = null;
#else
            if (_bridgeAvailable)
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
        }

        public void StartRecognition()
        {
            _ = StartRecognitionAsync();
        }

        public async Task StartRecognitionAsync()
        {
            if (!_bridgeAvailable) return;

            if (!IsInitialised)
                await InitialiseAsync();

            if (!IsInitialised) return;

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

            callbacks.PermissionGranted += _ => { granted = true; responded = true; };
            callbacks.PermissionDenied += _ => { responded = true; };
            callbacks.PermissionDeniedAndDontAskAgain += _ => { responded = true; };

            Permission.RequestUserPermission(Permission.Microphone, callbacks);

            while (!responded)
                yield return null;

            if (granted)
                StartRecognitionInternal();
            else
                FireError(VoskBridgeErrorCode.PermissionDenied,
                    "Microphone permission denied by user.");
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

        public void StopRecognition()
        {
            _isRecognising = false;
#if UNITY_EDITOR_WIN
            _editorBackend?.Stop();
#else
            if (!_bridgeAvailable) return;
            try
            {
                BridgeNative.vosk_bridge_stop();
            }
            catch (DllNotFoundException)
            {
                MarkBridgeUnavailable();
            }
#endif
        }

        public void ResetRecogniser()
        {
#if UNITY_EDITOR_WIN
            _editorBackend?.Reset();
#else
            if (!_bridgeAvailable) return;
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
#if UNITY_EDITOR_WIN
            _editorBackend?.SetGrammar(grammarJson, FireError);
#else
            if (!_bridgeAvailable) return;
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

        /// <summary>
        /// Injects a final result as if VOSK had recognised it.
        /// Bypasses bridge/model/session state, so events fire even when no recognition
        /// session is active — gate downstream code that assumes an active session.
        /// Empty or null text is passed through to match the real audio path. Must be
        /// called from the main thread.
        /// </summary>
        public void InjectResult(string text, VoskWord[] words = null, VoskAlternative[] alternatives = null)
        {
            AssertMainThread(nameof(InjectResult));
            DispatchFinalResult(
                text,
                words ?? Array.Empty<VoskWord>(),
                alternatives ?? Array.Empty<VoskAlternative>());
        }

        /// <summary>
        /// Injects a partial result as if VOSK had recognised it. Empty or null text is
        /// passed through to match the real audio path. Must be called from the main thread.
        /// </summary>
        public void InjectPartialResult(string text)
        {
            AssertMainThread(nameof(InjectPartialResult));
            OnPartialResult?.Invoke(text);
        }

        // ~average English word duration; used to give simulated words a plausible non-zero span.
        const float SimulatedWordDurationSeconds = 0.3f;

        /// <summary>
        /// Synthesises a <see cref="VoskWord"/> array from a text string with uniform
        /// confidence and sequential timing.
        /// Confidence is passed through unchanged — values outside [0, 1] are valid but
        /// may interact unexpectedly with downstream <c>minConfidence</c> filters.
        /// </summary>
        public static VoskWord[] CreateSimulatedWords(string text, float confidence = 1.0f)
        {
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<VoskWord>();

            var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var words = new VoskWord[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
                words[i] = new VoskWord(
                    tokens[i],
                    confidence,
                    i * SimulatedWordDurationSeconds,
                    (i + 1) * SimulatedWordDurationSeconds);
            return words;
        }

        void DispatchFinalResult(string text, VoskWord[] words, VoskAlternative[] alternatives)
        {
            var result = new VoskResult(text, words, alternatives);
#if UNITY_EDITOR
            EditorLastResult = result;
#endif
            OnFinalResult?.Invoke(text);
            OnResult?.Invoke(result);
        }

        static void AssertMainThread(string method)
        {
            Debug.Assert(System.Threading.Thread.CurrentThread.ManagedThreadId == 1,
                $"{method} must be called from the Unity main thread.");
        }

        void Update()
        {
#if UNITY_EDITOR_WIN
            if (_editorBackend != null && _isRecognising)
            {
                _editorBackend.Tick(FireError);
                DrainEditorResults();
                if (!_editorBackend.IsRunning) _isRecognising = false;
                return;
            }
#endif
            if (!_bridgeAvailable || !_isRecognising)
                return;

            try
            {
                bool hadActivity = false;
                IntPtr ptr;
                while ((ptr = BridgeNative.vosk_bridge_get_result(out int isFinalInt)) != IntPtr.Zero)
                {
                    hadActivity = true;
                    string json = BridgeNative.MarshalResult(ptr);
                    DispatchJsonResult(json, isFinalInt == 1);
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
        void DrainEditorResults()
        {
            while (_editorBackend.TryDequeueResult(out string json, out bool isFinal))
                DispatchJsonResult(json, isFinal);
        }
#endif

        // Parses a VOSK JSON string and fires the appropriate event(s).
        // Shared by the Android native drain loop and the Editor backend drain.
        void DispatchJsonResult(string json, bool isFinal)
        {
            if (string.IsNullOrEmpty(json))
                return;

            if (json.Contains("\"error\""))
            {
                var code = VoskJsonParser.ParseErrorCode(json);
                FireError(code, code.ToDescription());
                return;
            }

            string text = VoskJsonParser.ParseTextFromJson(json, isFinal);

            if (isFinal)
            {
                VoskAlternative[] alternatives = Array.Empty<VoskAlternative>();
                VoskWord[] words = Array.Empty<VoskWord>();

                bool needFullParse = OnResult != null;
#if UNITY_EDITOR
                needFullParse = true;
#endif
                if (needFullParse)
                {
                    alternatives = VoskJsonParser.ParseAlternativesFromJson(json);
                    if (alternatives.Length > 0 && alternatives[0].Words.Length > 0)
                        words = alternatives[0].Words;
                    else
                        words = VoskJsonParser.ParseWordsFromJson(json);
                }

                DispatchFinalResult(text, words, alternatives);
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

        void FireError(VoskBridgeErrorCode code, string message)
        {
            OnError?.Invoke(code, message);
        }

        void CheckBridgeError(int returnCode, string context)
        {
            if (returnCode == 0) return;

            var code = (VoskBridgeErrorCode)returnCode;
            string detail = BridgeNative.GetLastError();
            string message = string.IsNullOrEmpty(detail)
                ? $"{context}: {code.ToDescription()}"
                : $"{context}: {detail}";
            FireError(code, message);
        }

        void MarkBridgeUnavailable()
        {
            if (!_bridgeAvailable) return;
            _bridgeAvailable = false;
            _isRecognising = false;
            FireError(VoskBridgeErrorCode.ModelLoadFailed,
                "Native bridge library (libvosk-bridge) not found. " +
                "Ensure the native plugins are built and placed in Runtime/Plugins/Android/arm64-v8a/.");
        }

    }
}
