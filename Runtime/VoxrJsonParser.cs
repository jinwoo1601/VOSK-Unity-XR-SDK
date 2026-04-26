// ============================================================================
// Purpose:  Zero-allocation hand-rolled JSON parser for VOSK result/alternative/error JSON
// Layer:    Runtime
// Owns:     VoxrJsonParser (internal static class)
// Depends:  VoxrWord, VoxrAlternative, VoxrBridgeErrorCode
// ============================================================================
using System;
using System.Globalization;

namespace VoXR
{
    internal static class VoxrJsonParser
    {
        internal static VoxrBridgeErrorCode ParseErrorCode(string json)
        {
            const string key = "\"code\":";
            int idx = json.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0)
                return VoxrBridgeErrorCode.RingBufferOverflow;

            idx += key.Length;
            while (idx < json.Length && json[idx] == ' ') idx++;

            int code = 0;
            while (idx < json.Length && json[idx] >= '0' && json[idx] <= '9')
            {
                code = code * 10 + (json[idx] - '0');
                idx++;
            }

            return (VoxrBridgeErrorCode)code;
        }

        // VOSK returns JSON with word confidence when vosk_recognizer_set_words(1) is set:
        // {"result": [{"conf":0.95,"end":0.6,"start":0.1,"word":"hello"}, ...], "text":"hello"}
        // When there is no speech the "result" key is absent and "text" is empty.
        internal static VoxrWord[] ParseWordsFromJson(string json)
            => ParseWordsInRange(json, 0, json.Length);

        internal static VoxrWord[] ParseWordsInRange(string json, int rangeStart, int rangeEnd)
        {
            const string key = "\"result\"";
            int keyIdx = json.IndexOf(key, rangeStart, rangeEnd - rangeStart, StringComparison.Ordinal);
            if (keyIdx < 0)
                return Array.Empty<VoxrWord>();

            int arrayStart = json.IndexOf('[', keyIdx + key.Length);
            if (arrayStart < 0 || arrayStart >= rangeEnd)
                return Array.Empty<VoxrWord>();

            int arrayEnd = json.IndexOf(']', arrayStart);
            if (arrayEnd < 0 || arrayEnd > rangeEnd)
                return Array.Empty<VoxrWord>();

            // Count word objects
            int count = 0;
            for (int i = arrayStart; i < arrayEnd; i++)
                if (json[i] == '{') count++;

            if (count == 0)
                return Array.Empty<VoxrWord>();

            var words = new VoxrWord[count];
            int wordIdx = 0;
            int pos = arrayStart + 1;

            while (wordIdx < count && pos < arrayEnd)
            {
                int objStart = json.IndexOf('{', pos);
                if (objStart < 0 || objStart >= arrayEnd) break;

                int objEnd = json.IndexOf('}', objStart);
                if (objEnd < 0 || objEnd > arrayEnd) break;

                // "conf" is absent when maxAlternatives > 0; use -1 sentinel.
                bool hasConf = json.IndexOf("\"conf\"", objStart, objEnd - objStart,
                    StringComparison.Ordinal) >= 0;
                float conf = hasConf ? ParseFloatValue(json, objStart, objEnd, "\"conf\"") : -1f;
                float start = ParseFloatValue(json, objStart, objEnd, "\"start\"");
                float end = ParseFloatValue(json, objStart, objEnd, "\"end\"");
                string word = ParseStringValue(json, objStart, objEnd, "\"word\"");

                words[wordIdx++] = new VoxrWord(word, conf, start, end);
                pos = objEnd + 1;
            }

            return words;
        }

        // When max_alternatives > 0, VOSK wraps results in:
        // {"alternatives": [{"confidence":123.4,"result":[...],"text":"hello"}, ...]}
        internal static VoxrAlternative[] ParseAlternativesFromJson(string json)
        {
            const string key = "\"alternatives\"";
            int keyIdx = json.IndexOf(key, StringComparison.Ordinal);
            if (keyIdx < 0)
                return Array.Empty<VoxrAlternative>();

            int arrayStart = json.IndexOf('[', keyIdx + key.Length);
            if (arrayStart < 0)
                return Array.Empty<VoxrAlternative>();

            // Find matching ']' — must handle nested arrays ("result":[...])
            int arrayEnd = FindMatchingDelimiter(json, arrayStart, '[', ']');
            if (arrayEnd < 0)
                return Array.Empty<VoxrAlternative>();

            // Count depth-1 objects to allocate exact-size array upfront.
            int count = 0;
            {
                int depth = 0;
                for (int i = arrayStart; i <= arrayEnd; i++)
                {
                    if (json[i] == '{')
                    {
                        depth++;
                        if (depth == 1) count++;
                    }
                    else if (json[i] == '}') depth--;
                }
            }

            if (count == 0)
                return Array.Empty<VoxrAlternative>();

            var alternatives = new VoxrAlternative[count];
            int altIdx = 0;
            int pos = arrayStart + 1;

            while (altIdx < count && pos < arrayEnd)
            {
                int objStart = json.IndexOf('{', pos);
                if (objStart < 0 || objStart >= arrayEnd) break;

                int objEnd = FindMatchingDelimiter(json, objStart, '{', '}');
                if (objEnd < 0 || objEnd > arrayEnd) break;

                string text = ParseStringValue(json, objStart, objEnd, "\"text\"");
                float confidence = ParseFloatValue(json, objStart, objEnd, "\"confidence\"");
                var words = ParseWordsInRange(json, objStart, objEnd);

                alternatives[altIdx++] = new VoxrAlternative(text, confidence, words);
                pos = objEnd + 1;
            }

            return altIdx > 0 ? alternatives : Array.Empty<VoxrAlternative>();
        }

        internal static int FindMatchingDelimiter(string json, int openPos, char open, char close)
        {
            int depth = 1;
            for (int i = openPos + 1; i < json.Length; i++)
            {
                if (json[i] == open) depth++;
                else if (json[i] == close) { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        internal static float ParseFloatValue(string json, int start, int end, string key)
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

        internal static string ParseStringValue(string json, int start, int end, string key)
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

        internal static string ParseTextFromJson(string json, bool isFinal)
        {
            string key = isFinal ? "\"text\"" : "\"partial\"";
            string raw = ParseStringValue(json, 0, json.Length, key);
            if (raw.Length == 0 || raw.IndexOf('\\') < 0) return raw;
            return raw.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
    }
}
