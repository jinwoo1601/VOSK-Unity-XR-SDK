using System;
using System.Runtime.InteropServices;
using UnityEngine.Scripting;

namespace VoskXR.Native
{
    [Preserve]
    internal static class BridgeNative
    {
        const string LibraryName = "vosk-bridge";

        [DllImport(LibraryName)] [Preserve]
        internal static extern int vosk_bridge_init(string modelPath, float sampleRate,
            float micGainTargetDb, int maxAlternatives);

        [DllImport(LibraryName)] [Preserve]
        internal static extern void vosk_bridge_destroy();

        [DllImport(LibraryName)] [Preserve]
        internal static extern int vosk_bridge_start();

        [DllImport(LibraryName)] [Preserve]
        internal static extern void vosk_bridge_stop();

        [DllImport(LibraryName)] [Preserve]
        internal static extern int vosk_bridge_reset();

        [DllImport(LibraryName)] [Preserve]
        internal static extern IntPtr vosk_bridge_get_result(out int isFinal);

        [DllImport(LibraryName)] [Preserve]
        internal static extern int vosk_bridge_is_running();

        [DllImport(LibraryName)] [Preserve]
        internal static extern int vosk_bridge_is_initialised();

        [DllImport(LibraryName)] [Preserve]
        internal static extern int vosk_bridge_get_error(byte[] buf, int bufSize);

        [DllImport(LibraryName)] [Preserve]
        internal static extern int vosk_bridge_set_grammar(string grammarJson);

        internal static string MarshalResult(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero)
                return null;
            return Marshal.PtrToStringUTF8(ptr);
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
