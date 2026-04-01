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

        public event Action<string> OnPartialResult;
        public event Action<string> OnFinalResult;
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

                int result = BridgeNative.vosk_bridge_init(modelPath, sampleRate, micGainTargetDb);
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

        void Update()
        {
            if (!_bridgeAvailable || !_isRecognising)
                return;

            try
            {
                IntPtr ptr;
                while ((ptr = BridgeNative.vosk_bridge_get_result(out int isFinalInt)) != IntPtr.Zero)
                {
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
                        OnFinalResult?.Invoke(text);
                    else
                        OnPartialResult?.Invoke(text);
                }

                // Sync cached flag when native side stops (e.g. audio error)
                if (!IsRecognising)
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

        // VOSK returns JSON: {"text": "..."} for final, {"partial": "..."} for partial
        static string ParseTextFromJson(string json, bool isFinal)
        {
            string key = isFinal ? "\"text\"" : "\"partial\"";
            int keyIndex = json.IndexOf(key, StringComparison.Ordinal);
            if (keyIndex < 0)
                return string.Empty;

            int colonIndex = json.IndexOf(':', keyIndex + key.Length);
            if (colonIndex < 0)
                return string.Empty;

            int openQuote = json.IndexOf('"', colonIndex + 1);
            if (openQuote < 0)
                return string.Empty;

            // Find closing quote, skipping escaped characters
            int closeQuote = -1;
            for (int i = openQuote + 1; i < json.Length; i++)
            {
                if (json[i] == '\\') { i++; continue; }
                if (json[i] == '"') { closeQuote = i; break; }
            }
            if (closeQuote < 0)
                return string.Empty;

            return json.Substring(openQuote + 1, closeQuote - openQuote - 1)
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\");
        }
    }
}
