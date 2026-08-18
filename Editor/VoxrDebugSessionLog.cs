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
    /// behaviour can be analysed after the fact by tooling. Always on in the interactive
    /// editor; batch-mode runs are skipped, and a session that produced no diagnostics
    /// writes no file.
    /// </summary>
    [InitializeOnLoad]
    internal static class VoxrDebugSessionLog
    {
        const int SchemaVersion = 2;
        const int MaxRetainedSessions = 10;
        const string LogDirName = "VoxrDebugLogs";

        const string Readme =
            "Auto-exported VoXR command recognition diagnostics for one Unity Play Mode session. "
            + "Each entry is one utterance. On the ordinary parse path each attempt within it is "
            + "one extraction round, reporting the command pattern that won selection that round "
            + "— losing candidates are not recorded, so a pattern's absence means it lost "
            + "selection, not that it was never tried. Six pipeline events instead publish a "
            + "single synthetic attempt with an empty pattern: rejectReason 'no match' (nothing "
            + "was extracted; intent is empty and aggregateConfidence is 0, not -1), a "
            + "confirm/cancel resolution of a pending command, an answer to a disambiguation "
            + "prompt whose chosen command still needs confirming ('chosen via vocabulary, now "
            + "awaiting confirmation'), a follow-up slot-fill resolution of a pending command, "
            + "a follow-up fill refused because the completed command re-scored at or below "
            + "zero ('follow-up re-score <n> <= 0'), and a pending timeout (whose inputText is "
            + "the original command's transcript, not "
            + "new speech). An attempt fired when accepted=true, otherwise rejectReason says why "
            + "it did not. score is compared against minScore and aggregateConfidence against "
            + "minConfidence. aggregateConfidence is the MINIMUM per-word confidence over the "
            + "matched span, never an average; -1 means no per-word confidence was available for "
            + "that span, which usually means the utterance carried no word data at all (as with "
            + "injected text, where words is empty) but can also occur with words populated when "
            + "the matched span came from a segment that carried none. Slot startWord/endWord "
            + "are half-open [startWord, endWord) indices into the whitespace-split inputText, "
            + "not into the words array; they stay valid even when words is empty. tiedRival "
            + "names the equally-good rival the attempt beat on registration order alone, as "
            + "'intent (pattern N)'; it is empty when nothing tied it, so a coin-flip win is "
            + "distinguishable from a clean one. tiedRivalIsSibling is true when that rival was "
            + "one dropped word apart from the winner — the shape disambiguateSiblingTies exists "
            + "to answer, whether or not it could actually be phrased as a question. False covers "
            + "everything else that can tie, and the two cases differ: compare tiedRival's intent "
            + "against this attempt's intent. A differing intent is a grammar defect — duplicate "
            + "or overlapping patterns, one of which can never fire. The SAME intent is the "
            + "winner's own second phrasing tying it, which is harmless and routine. "
            + "tiedRivalIsSibling is meaningless when tiedRival is empty. Note the "
            + "numbers embedded in rejectReason text are formatted with the Editor's current "
            + "culture, so the decimal separator may be ',' — match on the surrounding words, "
            + "not the whole literal. The scoring model behind these numbers — the score "
            + "formula, the coverage weight charged for in-grammar words a match leaves "
            + "unexplained before AND after it, selection order, the two gates, and worked "
            + "examples — is documented in Documentation~/scoring.md in the com.jinwoo1601.voxr "
            + "package.";

        const string TestRunKey = "VoXR.DebugSessionLog.TestRunActive";

        static readonly List<Entry> Entries = new List<Entry>();
        static string _sessionStart;

        /// <summary>
        /// Set by the Test Runner hook for the duration of a run. Backed by
        /// <see cref="SessionState"/> rather than a static field because entering Play Mode
        /// reloads the domain, which would wipe the flag exactly when it is needed. It is
        /// also cleared when the editor closes, so an interrupted run cannot suppress
        /// exports indefinitely.
        /// </summary>
        internal static bool TestRunActive
        {
            get => SessionState.GetBool(TestRunKey, false);
            set => SessionState.SetBool(TestRunKey, value);
        }

        static VoxrDebugSessionLog()
        {
            // Batch-mode runs (CI, -runTests) are not playtests, but they drive the same
            // Play Mode diagnostics. Exporting there would churn through the retention
            // pool and evict real playtest sessions, so stay unsubscribed entirely.
            if (Application.isBatchMode)
                return;

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
            if (TestRunActive)
                return;
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
                    tiedRival = a.TiedRival ?? "",
                    tiedRivalIsSibling = a.TiedRivalIsSibling,
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
            public string tiedRival;
            public bool tiedRivalIsSibling;
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
