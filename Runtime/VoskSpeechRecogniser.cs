using System;
using System.Collections;
using System.Globalization;
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

        public bool IsModelReady { get; private set; }

        public bool IsInitialised
        {
            get
            {
                if (!_bridgeAvailable) return false;
                try { return BridgeNative.vosk_bridge_is_initialised() == 1; }
                catch (DllNotFoundException) { MarkBridgeUnavailable(); return false; }
            }
        }

        public bool IsRecognising
        {
            get
            {
                if (!_bridgeAvailable) return false;
                try { return BridgeNative.vosk_bridge_is_running() == 1; }
                catch (DllNotFoundException) { MarkBridgeUnavailable(); return false; }
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

                int result = BridgeNative.vosk_bridge_init(modelPath, sampleRate, micGainTargetDb,
                    maxAlternatives);
                CheckBridgeError(result, "Initialise");

                if (result == 0)
                {
                    IsModelReady = true;
                    OnModelReady?.Invoke();
                }
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
            if (!_bridgeAvailable) return;

            try
            {
                BridgeNative.vosk_bridge_destroy();
            }
            catch (DllNotFoundException)
            {
                MarkBridgeUnavailable();
            }

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
        }

        public void StopRecognition()
        {
            _isRecognising = false;
            if (!_bridgeAvailable) return;

            try
            {
                BridgeNative.vosk_bridge_stop();
            }
            catch (DllNotFoundException)
            {
                MarkBridgeUnavailable();
            }
        }

        public void ResetRecogniser()
        {
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
        }

        public void SetGrammar(string grammarJson)
        {
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
        }

        void Update()
        {
            if (!_bridgeAvailable || !_isRecognising)
                return;

            try
            {
                bool hadActivity = false;
                IntPtr ptr;
                while ((ptr = BridgeNative.vosk_bridge_get_result(out int isFinalInt)) != IntPtr.Zero)
                {
                    hadActivity = true;
                    bool isFinal = isFinalInt == 1;
                    string json = BridgeNative.MarshalResult(ptr);

                    if (json == null)
                        continue;

                    // Native side sends {"error": "...", "code": N} for errors
                    if (json.Contains("\"error\""))
                    {
                        var errorCode = ParseErrorCode(json);
                        FireError(errorCode, errorCode.ToDescription());
                        continue;
                    }

                    string text = ParseTextFromJson(json, isFinal);

                    if (isFinal)
                    {
                        OnFinalResult?.Invoke(text);

                        if (OnResult != null)
                        {
                            var alternatives = ParseAlternativesFromJson(json);
                            VoskWord[] words;
                            if (alternatives.Length > 0 && alternatives[0].Words.Length > 0)
                                words = alternatives[0].Words;
                            else
                                words = ParseWordsFromJson(json);
                            OnResult.Invoke(new VoskResult(text, words, alternatives));
                        }
                    }
                    else
                    {
                        OnPartialResult?.Invoke(text);
                    }
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

        static VoskBridgeErrorCode ParseErrorCode(string json)
        {
            const string key = "\"code\":";
            int idx = json.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0)
                return VoskBridgeErrorCode.RingBufferOverflow;

            idx += key.Length;
            while (idx < json.Length && json[idx] == ' ') idx++;

            int code = 0;
            while (idx < json.Length && json[idx] >= '0' && json[idx] <= '9')
            {
                code = code * 10 + (json[idx] - '0');
                idx++;
            }

            return (VoskBridgeErrorCode)code;
        }

        // VOSK returns JSON with word confidence when vosk_recognizer_set_words(1) is set:
        // {"result": [{"conf":0.95,"end":0.6,"start":0.1,"word":"hello"}, ...], "text":"hello"}
        // When there is no speech the "result" key is absent and "text" is empty.
        internal static VoskWord[] ParseWordsFromJson(string json)
            => ParseWordsInRange(json, 0, json.Length);

        static VoskWord[] ParseWordsInRange(string json, int rangeStart, int rangeEnd)
        {
            const string key = "\"result\"";
            int keyIdx = json.IndexOf(key, rangeStart, rangeEnd - rangeStart, StringComparison.Ordinal);
            if (keyIdx < 0)
                return Array.Empty<VoskWord>();

            int arrayStart = json.IndexOf('[', keyIdx + key.Length);
            if (arrayStart < 0 || arrayStart >= rangeEnd)
                return Array.Empty<VoskWord>();

            int arrayEnd = json.IndexOf(']', arrayStart);
            if (arrayEnd < 0 || arrayEnd > rangeEnd)
                return Array.Empty<VoskWord>();

            // Count word objects
            int count = 0;
            for (int i = arrayStart; i < arrayEnd; i++)
                if (json[i] == '{') count++;

            if (count == 0)
                return Array.Empty<VoskWord>();

            var words = new VoskWord[count];
            int wordIdx = 0;
            int pos = arrayStart + 1;

            while (wordIdx < count && pos < arrayEnd)
            {
                int objStart = json.IndexOf('{', pos);
                if (objStart < 0 || objStart >= arrayEnd) break;

                int objEnd = json.IndexOf('}', objStart);
                if (objEnd < 0 || objEnd > arrayEnd) break;

                float conf = ParseFloatValue(json, objStart, objEnd, "\"conf\"");
                float start = ParseFloatValue(json, objStart, objEnd, "\"start\"");
                float end = ParseFloatValue(json, objStart, objEnd, "\"end\"");
                string word = ParseStringValue(json, objStart, objEnd, "\"word\"");

                words[wordIdx++] = new VoskWord(word, conf, start, end);
                pos = objEnd + 1;
            }

            return words;
        }

        // When max_alternatives > 0, VOSK wraps results in:
        // {"alternatives": [{"confidence":123.4,"result":[...],"text":"hello"}, ...]}
        internal static VoskAlternative[] ParseAlternativesFromJson(string json)
        {
            const string key = "\"alternatives\"";
            int keyIdx = json.IndexOf(key, StringComparison.Ordinal);
            if (keyIdx < 0)
                return Array.Empty<VoskAlternative>();

            int arrayStart = json.IndexOf('[', keyIdx + key.Length);
            if (arrayStart < 0)
                return Array.Empty<VoskAlternative>();

            // Find matching ']' — must handle nested arrays ("result":[...])
            int arrayEnd = FindMatchingDelimiter(json, arrayStart, '[', ']');
            if (arrayEnd < 0)
                return Array.Empty<VoskAlternative>();

            // Alternatives contain nested "result":[{...}] arrays, so a simple '{'
            // count would overcount. Use a List and walk depth-1 objects instead.
            var alternatives = new System.Collections.Generic.List<VoskAlternative>();
            int pos = arrayStart + 1;

            while (pos < arrayEnd)
            {
                int objStart = json.IndexOf('{', pos);
                if (objStart < 0 || objStart >= arrayEnd) break;

                int objEnd = FindMatchingDelimiter(json, objStart, '{', '}');
                if (objEnd < 0 || objEnd > arrayEnd) break;

                string text = ParseStringValue(json, objStart, objEnd, "\"text\"");
                float confidence = ParseFloatValue(json, objStart, objEnd, "\"confidence\"");
                var words = ParseWordsInRange(json, objStart, objEnd);

                alternatives.Add(new VoskAlternative(text, confidence, words));
                pos = objEnd + 1;
            }

            return alternatives.Count > 0
                ? alternatives.ToArray()
                : Array.Empty<VoskAlternative>();
        }

        static int FindMatchingDelimiter(string json, int openPos, char open, char close)
        {
            int depth = 1;
            for (int i = openPos + 1; i < json.Length; i++)
            {
                if (json[i] == open) depth++;
                else if (json[i] == close) { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        static float ParseFloatValue(string json, int start, int end, string key)
        {
            int keyIdx = json.IndexOf(key, start, end - start, StringComparison.Ordinal);
            if (keyIdx < 0) return 0f;

            int colonIdx = json.IndexOf(':', keyIdx + key.Length);
            if (colonIdx < 0 || colonIdx >= end) return 0f;

            int valStart = colonIdx + 1;
            while (valStart < end && json[valStart] == ' ') valStart++;

            int valEnd = valStart;
            while (valEnd < end && json[valEnd] != ',' && json[valEnd] != '}' && json[valEnd] != ' ')
                valEnd++;

            if (valEnd <= valStart) return 0f;

            if (float.TryParse(json.AsSpan(valStart, valEnd - valStart),
                NumberStyles.Float, CultureInfo.InvariantCulture, out float result))
                return result;

            return 0f;
        }

        static string ParseStringValue(string json, int start, int end, string key)
        {
            int keyIdx = json.IndexOf(key, start, end - start, StringComparison.Ordinal);
            if (keyIdx < 0) return string.Empty;

            int colonIdx = json.IndexOf(':', keyIdx + key.Length);
            if (colonIdx < 0 || colonIdx >= end) return string.Empty;

            int openQuote = json.IndexOf('"', colonIdx + 1);
            if (openQuote < 0 || openQuote >= end) return string.Empty;

            int closeQuote = -1;
            for (int i = openQuote + 1; i < end; i++)
            {
                if (json[i] == '\\') { i++; continue; }
                if (json[i] == '"') { closeQuote = i; break; }
            }
            if (closeQuote < 0) return string.Empty;

            return json.Substring(openQuote + 1, closeQuote - openQuote - 1);
        }

        static string ParseTextFromJson(string json, bool isFinal)
        {
            string key = isFinal ? "\"text\"" : "\"partial\"";
            string raw = ParseStringValue(json, 0, json.Length, key);
            if (raw.Length == 0 || raw.IndexOf('\\') < 0) return raw;
            return raw.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
    }
}
