// ============================================================================
// Purpose:  Headless collector that auto-exports a Play Mode session's command diagnostics to JSON
// Layer:    Editor
// Owns:     VoxrDebugSessionLog (internal static class), Session, Entry, WordDto, AttemptDto, SlotDto (serializable DTOs)
// Depends:  VoxrCommandRecogniser, VoxrMatchDiagnostics, VoxrMatchAttempt, VoxrDiagnosticSlotMatch, VoxrWord
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using VoXR.Commands;

namespace VoXR.Editor
{
    /// <summary>
    /// Records every <see cref="VoxrMatchDiagnostics"/> published during Play Mode and writes
    /// the whole session to <c>Library/VoxrDebugLogs/</c> when Play Mode ends, so recognition
    /// behaviour can be analysed after the fact by tooling. Always on; a session that produced
    /// no diagnostics writes no file.
    /// </summary>
    [InitializeOnLoad]
    internal static class VoxrDebugSessionLog
    {
        const int SchemaVersion = 1;
        const int MaxRetainedSessions = 10;
        const string LogDirName = "VoxrDebugLogs";

        const string Readme =
            "Auto-exported VoXR command recognition diagnostics for one Unity Play Mode session. "
            + "Each entry is one utterance; each attempt within it is one command pattern evaluated "
            + "against that utterance. An attempt fired when accepted=true, otherwise rejectReason "
            + "says why it did not. score is compared against minScore and aggregateConfidence "
            + "against minConfidence; a confidence of -1 means VOSK supplied no per-word confidence "
            + "data for that utterance. Word/slot indices refer to positions in the words array.";

        static readonly List<Entry> Entries = new List<Entry>();
        static string _sessionStart;

        static VoxrDebugSessionLog()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            VoxrCommandRecogniser.DiagnosticsPublished -= OnDiagnosticsPublished;
            VoxrCommandRecogniser.DiagnosticsPublished += OnDiagnosticsPublished;
        }

        static string LogDirectory =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", LogDirName));

        static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                Entries.Clear();
                _sessionStart = DateTime.Now.ToString("o");
            }
            // Flush on EnteredEditMode rather than ExitingPlayMode: scene teardown runs in
            // between, and VoxrCommandRecogniser.OnDisable flushes its utterance buffer there,
            // which can publish one last diagnostic. Statics survive play-mode exit, so the
            // collected session is still intact by the time edit mode is entered.
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                Flush();
            }
        }

        static void OnDiagnosticsPublished(VoxrCommandRecogniser sender, VoxrMatchDiagnostics diag)
        {
            if (!EditorApplication.isPlaying)
                return;
            Entries.Add(BuildEntry(sender, diag));
        }

        static void Flush()
        {
            if (Entries.Count == 0)
            {
                _sessionStart = null;
                return;
            }

            var session = new Session
            {
                schemaVersion = SchemaVersion,
                readme = Readme,
                package = "com.jinwoo1601.voxr",
                packageVersion = PackageVersion(),
                unityVersion = Application.unityVersion,
                sessionStart = _sessionStart ?? "",
                sessionEnd = DateTime.Now.ToString("o"),
                entryCount = Entries.Count,
                entries = Entries.ToArray(),
            };

            try
            {
                string dir = LogDirectory;
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, $"session-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.json");
                File.WriteAllText(path, JsonUtility.ToJson(session, true));
                Prune(dir);
                Debug.Log(
                    $"[VoXR] Command debug session log written ({session.entryCount} "
                        + $"utterances): {path}"
                );
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[VoXR] Failed to write command debug session log: {e.Message}");
            }
            finally
            {
                Entries.Clear();
                _sessionStart = null;
            }
        }

        static void Prune(string dir)
        {
            var files = Directory.GetFiles(dir, "session-*.json");
            if (files.Length <= MaxRetainedSessions)
                return;

            // Timestamped names sort chronologically under ordinal comparison.
            Array.Sort(files, StringComparer.Ordinal);
            for (int i = 0; i < files.Length - MaxRetainedSessions; i++)
                File.Delete(files[i]);
        }

        static string PackageVersion()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                typeof(VoxrCommandRecogniser).Assembly
            );
            return info != null ? info.version : "";
        }

        // ─── DTO construction ───────────────────────────────────────────

        internal static Entry BuildEntry(VoxrCommandRecogniser sender, VoxrMatchDiagnostics diag)
        {
            var words = diag.Words ?? Array.Empty<VoxrWord>();
            var attempts = diag.Attempts ?? Array.Empty<VoxrMatchAttempt>();

            var entry = new Entry
            {
                timestamp = DateTime.Now.ToString("o"),
                frame = diag.Frame,
                activeSets =
                    sender != null
                        ? sender.ActiveSetNames ?? Array.Empty<string>()
                        : Array.Empty<string>(),
                inputText = diag.InputText ?? "",
                words = new WordDto[words.Length],
                attempts = new AttemptDto[attempts.Length],
            };

            for (int i = 0; i < words.Length; i++)
            {
                entry.words[i] = new WordDto
                {
                    text = words[i].Text ?? "",
                    confidence = words[i].Confidence,
                    startTime = words[i].StartTime,
                    endTime = words[i].EndTime,
                };
            }

            for (int i = 0; i < attempts.Length; i++)
            {
                var a = attempts[i];
                var slots = a.Slots ?? Array.Empty<VoxrDiagnosticSlotMatch>();

                var dto = new AttemptDto
                {
                    intent = a.Intent ?? "",
                    pattern = a.Pattern ?? "",
                    score = a.Score,
                    minScore = a.MinScore,
                    aggregateConfidence = a.AggregateConfidence,
                    minConfidence = a.MinConfidence,
                    accepted = a.IsAccepted,
                    rejectReason = a.RejectReason ?? "",
                    slots = new SlotDto[slots.Length],
                };

                for (int s = 0; s < slots.Length; s++)
                {
                    dto.slots[s] = new SlotDto
                    {
                        name = slots[s].Name ?? "",
                        value = slots[s].Value ?? "",
                        startWord = slots[s].StartWord,
                        endWord = slots[s].EndWord,
                        confidence = slots[s].Confidence,
                    };
                }

                entry.attempts[i] = dto;
            }

            return entry;
        }

        // ─── Serialized shape ───────────────────────────────────────────

        [Serializable]
        internal class Session
        {
            public int schemaVersion;
            public string readme;
            public string package;
            public string packageVersion;
            public string unityVersion;
            public string sessionStart;
            public string sessionEnd;
            public int entryCount;
            public Entry[] entries;
        }

        [Serializable]
        internal class Entry
        {
            public string timestamp;
            public int frame;
            public string[] activeSets;
            public string inputText;
            public WordDto[] words;
            public AttemptDto[] attempts;
        }

        [Serializable]
        internal class WordDto
        {
            public string text;
            public float confidence;
            public float startTime;
            public float endTime;
        }

        [Serializable]
        internal class AttemptDto
        {
            public string intent;
            public string pattern;
            public float score;
            public float minScore;
            public float aggregateConfidence;
            public float minConfidence;
            public bool accepted;
            public string rejectReason;
            public SlotDto[] slots;
        }

        [Serializable]
        internal class SlotDto
        {
            public string name;
            public string value;
            public int startWord;
            public int endWord;
            public float confidence;
        }
    }
}
