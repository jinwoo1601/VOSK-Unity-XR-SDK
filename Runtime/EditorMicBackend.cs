#if UNITY_EDITOR_WIN
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using VoskXR.Dsp;
using VoskXR.Native;

namespace VoskXR
{
    /// <summary>
    /// Editor-only live microphone backend for the Windows Unity Editor.
    ///
    /// Captures audio via <see cref="UnityEngine.Microphone"/>, runs it through
    /// the C# ports of the existing downsampler and AGC, and feeds the resulting
    /// 16 kHz int16 PCM directly to <c>libvosk.dll</c> via P/Invoke. Produces the
    /// same JSON result strings as the Android native bridge, so the result-drain
    /// code in <see cref="VoskSpeechRecogniser"/> can share its parser helpers
    /// between the two paths.
    ///
    /// Single-threaded by design — every method on this class must be called
    /// from the Unity main thread. The only exception is the background
    /// <c>Task.Run</c> wrapped around the blocking <c>vosk_model_new</c> call
    /// inside <see cref="InitialiseAsync"/>.
    /// </summary>
    internal sealed class EditorMicBackend
    {
        const int CaptureLengthSeconds = 5;
        const int SourceSampleRate = 48000;

        IntPtr _model;
        IntPtr _recognizer;
        float _sampleRate;
        int _maxAlternatives;

        // Set by Release() so a still-in-flight model-load continuation knows
        // to free its result instead of assigning it to a dead instance.
        // volatile because the Task.Run continuation may resume on the thread
        // pool before the UnitySynchronizationContext posts it back to main.
        volatile bool _released;

        readonly Downsampler _downsampler = new Downsampler();
        readonly Agc _agc = new Agc();

        AudioClip _clip;
        int _lastSamplePos;
        bool _firstTick;

        float[] _readBuffer;
        float[] _workBuffer;
        float[] _downsampledBuffer;
        short[] _int16Buffer;

        readonly Queue<(string Json, bool IsFinal)> _resultQueue = new Queue<(string, bool)>();
        string _lastPartialJson;

        internal bool IsInitialised { get; private set; }
        internal bool IsRunning { get; private set; }

        internal float PreAgcRms { get; private set; }
        internal float PostAgcRms { get; private set; }
        internal float AgcGain => _agc.CurrentGain;

        internal async Task<bool> InitialiseAsync(
            string modelPath,
            float sampleRate,
            float micGainTargetDb,
            int maxAlternatives,
            Action<VoskBridgeErrorCode, string> fireError)
        {
            if (IsInitialised) return true;

            _sampleRate = sampleRate;
            _maxAlternatives = maxAlternatives;

            try
            {
                VoskNative.vosk_set_log_level(-1);

                // vosk_model_new loads and parses the acoustic model from disk
                // and can take 1–3 seconds. Run on a background thread to keep
                // the Editor main thread responsive.
                IntPtr model = await Task.Run(() => VoskNative.vosk_model_new(modelPath));

                // If Release() fired while the model was loading, the continuation
                // is resuming on a torn-down instance. Free the just-loaded model
                // locally to avoid leaking ~150 MB of native memory.
                if (_released)
                {
                    if (model != IntPtr.Zero)
                        VoskNative.vosk_model_free(model);
                    return false;
                }

                if (model == IntPtr.Zero)
                {
                    fireError?.Invoke(VoskBridgeErrorCode.ModelLoadFailed,
                        $"vosk_model_new returned NULL for path: {modelPath}");
                    return false;
                }
                _model = model;

                _recognizer = VoskNative.vosk_recognizer_new(_model, _sampleRate);
                if (_recognizer == IntPtr.Zero)
                {
                    fireError?.Invoke(VoskBridgeErrorCode.ModelLoadFailed,
                        "vosk_recognizer_new returned NULL");
                    VoskNative.vosk_model_free(_model);
                    _model = IntPtr.Zero;
                    return false;
                }

                VoskNative.vosk_recognizer_set_words(_recognizer, 1);
                if (_maxAlternatives > 0)
                    VoskNative.vosk_recognizer_set_max_alternatives(_recognizer, _maxAlternatives);

                _agc.Configure(micGainTargetDb, _sampleRate);

                IsInitialised = true;
                return true;
            }
            catch (DllNotFoundException ex)
            {
                fireError?.Invoke(VoskBridgeErrorCode.ModelLoadFailed,
                    "libvosk.dll (or a MinGW runtime dependency) not found in " +
                    "Runtime/Plugins/x86_64/. Download vosk-win64 from " +
                    $"https://github.com/alphacep/vosk-api/releases and drop the " +
                    $"DLLs into that folder. Details: {ex.Message}");
                return false;
            }
            catch (EntryPointNotFoundException ex)
            {
                fireError?.Invoke(VoskBridgeErrorCode.ModelLoadFailed,
                    $"libvosk.dll is missing an expected export — version mismatch? " +
                    $"Details: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                fireError?.Invoke(VoskBridgeErrorCode.ModelLoadFailed,
                    $"EditorMicBackend.InitialiseAsync failed: {ex.Message}");
                return false;
            }
        }

        internal bool Start(Action<VoskBridgeErrorCode, string> fireError)
        {
            if (!IsInitialised)
            {
                fireError?.Invoke(VoskBridgeErrorCode.NotInitialised,
                    "EditorMicBackend.Start called before InitialiseAsync.");
                return false;
            }
            if (IsRunning) return true;

            if (Microphone.devices.Length == 0)
            {
                fireError?.Invoke(VoskBridgeErrorCode.AudioDeviceUnavailable,
                    "No microphone devices detected on this system.");
                return false;
            }

            _clip = Microphone.Start(
                deviceName: null,
                loop: true,
                lengthSec: CaptureLengthSeconds,
                frequency: SourceSampleRate);

            if (_clip == null)
            {
                fireError?.Invoke(VoskBridgeErrorCode.AudioDeviceUnavailable,
                    "Microphone.Start returned null — audio device unavailable or " +
                    "Windows microphone permission denied.");
                return false;
            }

            _lastSamplePos = 0;
            _firstTick = true;
            _lastPartialJson = null;
            _resultQueue.Clear();

            // Per-frame worst case at 60 fps is ≈ SourceSampleRate / 60 ≈ 800 samples.
            // Size the work buffer generously at one tenth of the clip (500 ms worth)
            // so a dropped frame during a GC spike does not overrun.
            int clipSamples = _clip.samples;
            int workSize = Math.Max(clipSamples / 10, SourceSampleRate / 10);
            _readBuffer = new float[clipSamples];
            _workBuffer = new float[workSize];
            _downsampledBuffer = new float[workSize / Downsampler.DecimationFactor + 1];
            _int16Buffer = new short[_downsampledBuffer.Length];

            _downsampler.Reset();
            _agc.Reset();
            VoskNative.vosk_recognizer_reset(_recognizer);

            IsRunning = true;
            return true;
        }

        internal void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;

            // Flush any in-progress utterance so the last command spoken before
            // StopRecognition() is not silently discarded. Matches what the
            // Android recognition thread does at exit (vosk_bridge.cpp:130-134).
            if (_recognizer != IntPtr.Zero)
            {
                string finalJson = BridgeNative.MarshalResult(
                    VoskNative.vosk_recognizer_final_result(_recognizer));
                if (!string.IsNullOrEmpty(finalJson))
                    _resultQueue.Enqueue((finalJson, true));
            }

            if (_clip != null)
            {
                Microphone.End(null);
                _clip = null;
            }
        }

        internal void Reset()
        {
            if (!IsInitialised) return;
            VoskNative.vosk_recognizer_reset(_recognizer);
            _downsampler.Reset();
            _agc.Reset();
            _lastPartialJson = null;
            _resultQueue.Clear();
        }

        internal void SetGrammar(
            string grammarJson,
            Action<VoskBridgeErrorCode, string> fireError)
        {
            if (!IsInitialised)
            {
                fireError?.Invoke(VoskBridgeErrorCode.NotInitialised,
                    "EditorMicBackend.SetGrammar called before InitialiseAsync.");
                return;
            }
            if (IsRunning)
            {
                fireError?.Invoke(VoskBridgeErrorCode.AlreadyRunning,
                    "EditorMicBackend.SetGrammar called while recognition is running. " +
                    "Stop recognition first.");
                return;
            }

            // VOSK has no grammar-swap API — we must free and recreate the recognizer.
            if (_recognizer != IntPtr.Zero)
            {
                VoskNative.vosk_recognizer_free(_recognizer);
                _recognizer = IntPtr.Zero;
            }

            if (!string.IsNullOrEmpty(grammarJson))
            {
                _recognizer = VoskNative.vosk_recognizer_new_grm(
                    _model, _sampleRate, grammarJson);
                // Grammar mode + alternatives produces unreliable results in the
                // Android bridge; match that by skipping set_max_alternatives here.
            }
            else
            {
                _recognizer = VoskNative.vosk_recognizer_new(_model, _sampleRate);
                if (_recognizer != IntPtr.Zero && _maxAlternatives > 0)
                    VoskNative.vosk_recognizer_set_max_alternatives(_recognizer, _maxAlternatives);
            }

            if (_recognizer == IntPtr.Zero)
            {
                fireError?.Invoke(VoskBridgeErrorCode.ModelLoadFailed,
                    "vosk_recognizer_new failed during SetGrammar.");
                return;
            }

            VoskNative.vosk_recognizer_set_words(_recognizer, 1);
        }

        internal void Release()
        {
            _released = true;
            Stop();

            if (_recognizer != IntPtr.Zero)
            {
                VoskNative.vosk_recognizer_free(_recognizer);
                _recognizer = IntPtr.Zero;
            }
            if (_model != IntPtr.Zero)
            {
                VoskNative.vosk_model_free(_model);
                _model = IntPtr.Zero;
            }

            _readBuffer = null;
            _workBuffer = null;
            _downsampledBuffer = null;
            _int16Buffer = null;
            _resultQueue.Clear();
            _lastPartialJson = null;

            IsInitialised = false;
        }

        /// <summary>
        /// Pulls new microphone samples, runs them through downsample → AGC →
        /// int16 → VOSK, and enqueues any resulting JSON for the main Update()
        /// loop to drain via <see cref="TryDequeueResult"/>.
        /// </summary>
        internal void Tick(Action<VoskBridgeErrorCode, string> fireError)
        {
            if (!IsRunning || _clip == null) return;

            if (_firstTick)
            {
                _firstTick = false;
                int actualRate = _clip.frequency;
                if (actualRate != SourceSampleRate)
                {
                    fireError?.Invoke(VoskBridgeErrorCode.AudioDeviceUnavailable,
                        $"Microphone returned {actualRate} Hz; {SourceSampleRate} Hz is required. " +
                        "The 48 kHz → 16 kHz downsampler cannot handle other input rates in v3.1.");
                    Stop();
                    return;
                }
            }

            int clipSamples = _clip.samples;
            int currentPos = Microphone.GetPosition(null);
            int available = (currentPos - _lastSamplePos + clipSamples) % clipSamples;
            if (available == 0) return;

            // If this frame produced more samples than the work buffer can hold
            // (dropped frame / GC spike), cap the pull — the ring buffer has
            // already advanced past the oldest data anyway.
            if (available > _workBuffer.Length)
                available = _workBuffer.Length;

            // Read the entire clip once, then copy the new window into the work
            // buffer with modular-wrap indexing. Reading the full clip is a single
            // native memcpy and sidesteps AudioClip.GetData's wrap semantics.
            _clip.GetData(_readBuffer, 0);

            int firstChunk = Math.Min(available, clipSamples - _lastSamplePos);
            Array.Copy(_readBuffer, _lastSamplePos, _workBuffer, 0, firstChunk);
            int remaining = available - firstChunk;
            if (remaining > 0)
                Array.Copy(_readBuffer, 0, _workBuffer, firstChunk, remaining);

            _lastSamplePos = (_lastSamplePos + available) % clipSamples;

            int dsCount = _downsampler.Process(_workBuffer, available, _downsampledBuffer);
            if (dsCount == 0) return;

            PreAgcRms = ComputeRms(_downsampledBuffer, dsCount);
            _agc.Process(_downsampledBuffer, dsCount);
            PostAgcRms = ComputeRms(_downsampledBuffer, dsCount);

            FloatToInt16(_downsampledBuffer, _int16Buffer, dsCount);

            int result = VoskNative.vosk_recognizer_accept_waveform_s(
                _recognizer, _int16Buffer, dsCount);

            if (result == 1)
            {
                string json = BridgeNative.MarshalResult(
                    VoskNative.vosk_recognizer_result(_recognizer));
                if (!string.IsNullOrEmpty(json))
                    _resultQueue.Enqueue((json, true));
            }
            else
            {
                string json = BridgeNative.MarshalResult(
                    VoskNative.vosk_recognizer_partial_result(_recognizer));
                if (!string.IsNullOrEmpty(json) && json != _lastPartialJson)
                {
                    _lastPartialJson = json;
                    _resultQueue.Enqueue((json, false));
                }
            }
        }

        internal bool TryDequeueResult(out string json, out bool isFinal)
        {
            if (_resultQueue.Count == 0)
            {
                json = null; isFinal = false; return false;
            }
            var r = _resultQueue.Dequeue();
            json = r.Json; isFinal = r.IsFinal;
            return true;
        }

        static float ComputeRms(float[] samples, int count)
        {
            float sum = 0f;
            for (int i = 0; i < count; i++)
                sum += samples[i] * samples[i];
            return count > 0 ? (float)Math.Sqrt(sum / count) : 0f;
        }

        // Float [-1, 1] → int16, with clamping. Matches vosk_bridge.cpp:44-51.
        static void FloatToInt16(float[] src, short[] dst, int count)
        {
            for (int i = 0; i < count; i++)
            {
                float v = src[i] * 32767f;
                if (v > 32767f) v = 32767f;
                else if (v < -32768f) v = -32768f;
                dst[i] = (short)v;
            }
        }
    }
}
#endif
