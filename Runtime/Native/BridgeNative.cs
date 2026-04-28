// ============================================================================
// Purpose:  P/Invoke bindings for the Android vosk-bridge native library
// Layer:    Runtime.Native
// Owns:     BridgeNative (internal static class)
// Depends:  (none)
// ============================================================================
using System;
using System.Runtime.InteropServices;
using UnityEngine.Scripting;

namespace VoXR.Native
{
    [Preserve]
    internal static class BridgeNative
    {
        const string LibraryName = "vosk-bridge";

        [DllImport(LibraryName)] [Preserve]
        internal static extern int vosk_bridge_init(string modelPath, float sampleRate,
            float micGainTargetDb);

        [DllImport(LibraryName)] [Preserve]
        internal static extern void vosk_bridge_destroy();

        [DllImport(LibraryName)] [Preserve]
        internal static extern int vosk_bridge_start();

        [DllImport(LibraryName)] [Preserve]
        internal static extern void vosk_bridge_stop();

        [DllImport(LibraryName)] [Preserve]
        internal static extern int vosk_bridge_reset();

        [DllImport(LibraryName)] [Preserve]
        internal static extern IntPtr vosk_bridge_get_result(out int isFinal, out int length);

        [DllImport(LibraryName)] [Preserve]
        internal static extern int vosk_bridge_is_running();

        [DllImport(LibraryName)] [Preserve]
        internal static extern int vosk_bridge_is_initialised();

        [DllImport(LibraryName)] [Preserve]
        internal static extern int vosk_bridge_get_error(byte[] buf, int bufSize);

        [DllImport(LibraryName)] [Preserve]
        internal static extern int vosk_bridge_set_grammar(string grammarJson);

        // The returned span wraps native memory owned by the bridge (g_current_result.json
        // for Android, libvosk's internal result buffer in the Editor). That memory is
        // valid until the next vosk_bridge_get_result / vosk_recognizer_*_result call.
        // The span MUST NOT be stored, captured by a closure, or held across an
        // await/coroutine yield — it must be fully consumed within the body of the
        // dispatching call.
        internal static unsafe ReadOnlySpan<byte> SpanFromPtr(IntPtr ptr, int length)
            => ptr == IntPtr.Zero ? default : new ReadOnlySpan<byte>((void*)ptr, length);

        // Same lifetime contract as SpanFromPtr. Used for libvosk's null-terminated
        // result strings on the Editor path, where no length is returned.
        internal static unsafe ReadOnlySpan<byte> SpanFromNullTerminated(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return default;
            byte* p = (byte*)ptr;
            int len = 0;
            while (p[len] != 0) len++;
            return new ReadOnlySpan<byte>(p, len);
        }

        static readonly byte[] ErrorBuf = new byte[512];

        internal static string GetLastError()
        {
            vosk_bridge_get_error(ErrorBuf, ErrorBuf.Length);
            int length = Array.IndexOf(ErrorBuf, (byte)0);
            if (length < 0) length = ErrorBuf.Length;
            return System.Text.Encoding.UTF8.GetString(ErrorBuf, 0, length);
        }
    }
}
