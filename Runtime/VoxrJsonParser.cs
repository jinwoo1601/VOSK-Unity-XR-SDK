// ============================================================================
// Purpose:  Zero-allocation hand-rolled JSON parser for VOSK result/error JSON
// Layer:    Runtime
// Owns:     VoxrJsonParser (internal static class)
// Depends:  VoxrWord, VoxrBridgeErrorCode
// ============================================================================
using System;
using System.Buffers.Text;
using System.Text;

namespace VoXR
{
    internal static class VoxrJsonParser
    {
        // UTF-8 byte keys. We use static readonly byte[] (not "..."u8 literals) because
        // Unity 6000.3's default LangVersion is C# 9, which does not support UTF-8 string
        // literals. If a future Unity bumps the default to C# 11+, switch to "..."u8.
        internal static readonly byte[] KeyResult  = Encoding.UTF8.GetBytes("\"result\"");
        internal static readonly byte[] KeyText    = Encoding.UTF8.GetBytes("\"text\"");
        internal static readonly byte[] KeyPartial = Encoding.UTF8.GetBytes("\"partial\"");
        internal static readonly byte[] KeyConf    = Encoding.UTF8.GetBytes("\"conf\"");
        internal static readonly byte[] KeyStart   = Encoding.UTF8.GetBytes("\"start\"");
        internal static readonly byte[] KeyEnd     = Encoding.UTF8.GetBytes("\"end\"");
        internal static readonly byte[] KeyWord    = Encoding.UTF8.GetBytes("\"word\"");
        internal static readonly byte[] KeyCode    = Encoding.UTF8.GetBytes("\"code\":");
        internal static readonly byte[] KeyError   = Encoding.UTF8.GetBytes("\"error\"");

        [ThreadStatic] static byte[] _unescapeBuffer;

        internal static VoxrBridgeErrorCode ParseErrorCode(ReadOnlySpan<byte> json)
        {
            int idx = json.IndexOf(KeyCode.AsSpan());
            if (idx < 0)
                return VoxrBridgeErrorCode.RingBufferOverflow;

            idx += KeyCode.Length;
            while (idx < json.Length && json[idx] == (byte)' ') idx++;

            int code = 0;
            while (idx < json.Length && json[idx] >= (byte)'0' && json[idx] <= (byte)'9')
            {
                code = code * 10 + (json[idx] - (byte)'0');
                idx++;
            }

            return (VoxrBridgeErrorCode)code;
        }

        // VOSK returns JSON with word confidence when vosk_recognizer_set_words(1) is set:
        // {"result": [{"conf":0.95,"end":0.6,"start":0.1,"word":"hello"}, ...], "text":"hello"}
        // When there is no speech the "result" key is absent and "text" is empty.
        internal static VoxrWord[] ParseWordsFromJson(ReadOnlySpan<byte> json)
        {
            int keyIdx = json.IndexOf(KeyResult.AsSpan());
            if (keyIdx < 0)
                return Array.Empty<VoxrWord>();

            int arrayStart = IndexOf(json, (byte)'[', keyIdx + KeyResult.Length);
            if (arrayStart < 0)
                return Array.Empty<VoxrWord>();

            int arrayEnd = IndexOf(json, (byte)']', arrayStart);
            if (arrayEnd < 0)
                return Array.Empty<VoxrWord>();

            int count = 0;
            for (int i = arrayStart; i < arrayEnd; i++)
                if (json[i] == (byte)'{') count++;

            if (count == 0)
                return Array.Empty<VoxrWord>();

            var words = new VoxrWord[count];
            int wordIdx = 0;
            int pos = arrayStart + 1;

            while (wordIdx < count && pos < arrayEnd)
            {
                int objStart = IndexOf(json, (byte)'{', pos);
                if (objStart < 0 || objStart >= arrayEnd) break;

                int objEnd = IndexOf(json, (byte)'}', objStart);
                if (objEnd < 0 || objEnd > arrayEnd) break;

                float conf = ParseFloatValue(json, objStart, objEnd, KeyConf);
                float start = ParseFloatValue(json, objStart, objEnd, KeyStart);
                float end = ParseFloatValue(json, objStart, objEnd, KeyEnd);
                string word = ParseStringValue(json, objStart, objEnd, KeyWord);

                words[wordIdx++] = new VoxrWord(word, conf, start, end);
                pos = objEnd + 1;
            }

            return words;
        }

        internal static float ParseFloatValue(ReadOnlySpan<byte> json, int start, int end, ReadOnlySpan<byte> key)
        {
            int keyIdx = FindKey(json, start, end, key);
            if (keyIdx < 0) return 0f;

            int colonIdx = IndexOf(json, (byte)':', keyIdx + key.Length);
            if (colonIdx < 0 || colonIdx >= end) return 0f;

            int valStart = colonIdx + 1;
            while (valStart < end && json[valStart] == (byte)' ') valStart++;

            int valEnd = valStart;
            while (valEnd < end && json[valEnd] != (byte)',' && json[valEnd] != (byte)'}' && json[valEnd] != (byte)' ')
                valEnd++;

            if (valEnd <= valStart) return 0f;

            if (Utf8Parser.TryParse(json.Slice(valStart, valEnd - valStart), out float result, out _))
                return result;

            return 0f;
        }

        internal static string ParseStringValue(ReadOnlySpan<byte> json, int start, int end, ReadOnlySpan<byte> key)
        {
            int keyIdx = FindKey(json, start, end, key);
            if (keyIdx < 0) return string.Empty;

            int colonIdx = IndexOf(json, (byte)':', keyIdx + key.Length);
            if (colonIdx < 0 || colonIdx >= end) return string.Empty;

            int openQuote = IndexOf(json, (byte)'"', colonIdx + 1);
            if (openQuote < 0 || openQuote >= end) return string.Empty;

            int closeQuote = -1;
            bool hasEscape = false;
            for (int i = openQuote + 1; i < end; i++)
            {
                if (json[i] == (byte)'\\') { hasEscape = true; i++; continue; }
                if (json[i] == (byte)'"') { closeQuote = i; break; }
            }
            if (closeQuote < 0) return string.Empty;

            int valStart = openQuote + 1;
            int valLen = closeQuote - valStart;
            if (valLen == 0) return string.Empty;

            ReadOnlySpan<byte> raw = json.Slice(valStart, valLen);
            return hasEscape ? DecodeEscaped(raw) : Encoding.UTF8.GetString(raw);
        }

        internal static string ParseTextFromJson(ReadOnlySpan<byte> json, bool isFinal)
        {
            ReadOnlySpan<byte> key = (isFinal ? KeyText : KeyPartial).AsSpan();
            return ParseStringValue(json, 0, json.Length, key);
        }

        // Wrapping the IndexOf on a slice keeps the absolute index correct — without
        // this helper we would silently return a relative index inside the slice and
        // every caller would need to add `start` back, which is easy to forget.
        static int FindKey(ReadOnlySpan<byte> json, int start, int end, ReadOnlySpan<byte> key)
        {
            int rel = json.Slice(start, end - start).IndexOf(key);
            return rel < 0 ? -1 : start + rel;
        }

        static int IndexOf(ReadOnlySpan<byte> json, byte b, int start)
        {
            int rel = json.Slice(start).IndexOf(b);
            return rel < 0 ? -1 : start + rel;
        }

        static string DecodeEscaped(ReadOnlySpan<byte> raw)
        {
            byte[] buf = _unescapeBuffer;
            if (buf == null || buf.Length < raw.Length)
                _unescapeBuffer = buf = new byte[raw.Length];

            int dst = 0;
            for (int i = 0; i < raw.Length; i++)
            {
                byte b = raw[i];
                if (b == (byte)'\\' && i + 1 < raw.Length)
                {
                    byte next = raw[i + 1];
                    if (next == (byte)'"' || next == (byte)'\\')
                    {
                        buf[dst++] = next;
                        i++;
                        continue;
                    }
                }
                buf[dst++] = b;
            }

            return Encoding.UTF8.GetString(buf, 0, dst);
        }
    }
}
