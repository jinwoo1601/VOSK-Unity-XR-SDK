// ============================================================================
// Purpose:  P/Invoke bindings for libvosk.dll (Windows Editor desktop build)
// Layer:    Runtime.Native (UNITY_EDITOR_WIN only)
// Owns:     VoxrNative (internal static class)
// Depends:  (none)
// ============================================================================
#if UNITY_EDITOR_WIN
using System;
using System.Runtime.InteropServices;
using UnityEngine.Scripting;

namespace VoXR.Native
{
    [Preserve]
    internal static class VoxrNative
    {
        const string LibraryName = "libvosk";

        // Model

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] [Preserve]
        internal static extern IntPtr vosk_model_new(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string modelPath);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] [Preserve]
        internal static extern void vosk_model_free(IntPtr model);

        // Recognizer

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] [Preserve]
        internal static extern IntPtr vosk_recognizer_new(IntPtr model, float sampleRate);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] [Preserve]
        internal static extern IntPtr vosk_recognizer_new_grm(
            IntPtr model,
            float sampleRate,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string grammar);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] [Preserve]
        internal static extern void vosk_recognizer_free(IntPtr recognizer);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] [Preserve]
        internal static extern void vosk_recognizer_set_words(IntPtr recognizer, int words);

        // Audio ingestion — int16 path matches the existing Android pipeline.
        // The float variant (accept_waveform_f) is intentionally not bound here;
        // the Android comment in vosk_bridge.cpp notes the float path is unreliable
        // on some builds and the int16 path is the known-good choice.
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] [Preserve]
        internal static extern int vosk_recognizer_accept_waveform_s(
            IntPtr recognizer,
            [In] short[] data,
            int length);

        // Results — returned pointer is owned by the recognizer. Do NOT free it.
        // Marshal the string immediately; the pointer becomes invalid on the
        // next call into the recognizer.

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] [Preserve]
        internal static extern IntPtr vosk_recognizer_result(IntPtr recognizer);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] [Preserve]
        internal static extern IntPtr vosk_recognizer_partial_result(IntPtr recognizer);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] [Preserve]
        internal static extern IntPtr vosk_recognizer_final_result(IntPtr recognizer);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] [Preserve]
        internal static extern void vosk_recognizer_reset(IntPtr recognizer);

        // Misc

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] [Preserve]
        internal static extern void vosk_set_log_level(int logLevel);
    }
}
#endif
