using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VoskXR;
using VoskXR.Commands;

namespace VoskXR.Editor
{
    /// <summary>
    /// IMGUI EditorWindow showing the VOSK command recognition pipeline state in real time.
    /// Pull model: polls runtime components every repaint during Play Mode.
    /// </summary>
    public class VoskDebugWindow : EditorWindow
    {
        const int MaxHistoryEntries = 20;
        const float LevelMeterHeight = 16f;
        const float LevelMeterWidth = 200f;

        VoskSpeechRecogniser _speechRecogniser;
        VoskCommandRecogniser _commandRecogniser;

        readonly List<HistoryEntry> _history = new List<HistoryEntry>();
        int _lastDiagFrame = -1;
        bool _paused;
        bool _resumePending;
        string _injectText = "";
        VoskMatchDiagnostics _frozenDiag;

        Vector2 _leftScroll;
        Vector2 _rightScroll;
        Vector2 _historyScroll;

        GUIStyle _boldIntentStyle;
        GUIStyle _rejectStyle;
        GUIStyle _thresholdStyle;
        GUIStyle _historyStyle;

        struct HistoryEntry
        {
            public string Label;
            public bool Accepted;
            public VoskMatchDiagnostics Diagnostics;
        }

        [MenuItem("Window/VOSK XR/Command Debug")]
        static void Open()
        {
            GetWindow<VoskDebugWindow>("VOSK Command Debug");
        }

        void OnEnable()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            FindComponents();
        }

        void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        }

        void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
                FindComponents();
            else if (state == PlayModeStateChange.ExitingPlayMode)
            {
                _speechRecogniser = null;
                _commandRecogniser = null;
            }
        }

        void FindComponents()
        {
            if (!EditorApplication.isPlaying) return;
            _speechRecogniser = FindFirstObjectByType<VoskSpeechRecogniser>();
            _commandRecogniser = FindFirstObjectByType<VoskCommandRecogniser>();
        }

        void OnInspectorUpdate()
        {
            if (EditorApplication.isPlaying)
                Repaint();
        }

        void OnGUI()
        {
            if (!EditorApplication.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play Mode to see live command diagnostics.", MessageType.Info);
                return;
            }

            if (_commandRecogniser == null)
            {
                if (GUILayout.Button("Find Components"))
                    FindComponents();
                EditorGUILayout.HelpBox(
                    "No VoskCommandRecogniser found in the scene. " +
                    "Ensure a GameObject has both VoskSpeechRecogniser and VoskCommandRecogniser.",
                    MessageType.Warning);
                return;
            }

            PollDiagnostics();

            EditorGUILayout.BeginHorizontal();

            // Left panel — Audio & Recognition
            EditorGUILayout.BeginVertical(GUILayout.Width(position.width * 0.45f));
            _leftScroll = EditorGUILayout.BeginScrollView(_leftScroll);
            DrawLeftPanel();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            // Separator
            DrawVerticalSeparator();

            // Right panel — Command Matching
            EditorGUILayout.BeginVertical();
            _rightScroll = EditorGUILayout.BeginScrollView(_rightScroll);
            DrawRightPanel();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();

            DrawBottomBar();
        }

        void PollDiagnostics()
        {
            if (_commandRecogniser == null) return;
            if (_paused) return;

            var diag = _commandRecogniser.LastMatchDiagnostics;

            if (diag.Frame != 0 && diag.Frame != _lastDiagFrame)
            {
                _lastDiagFrame = diag.Frame;
                _frozenDiag = diag;
                _resumePending = false;
                AddToHistory(diag);
            }
        }

        VoskMatchDiagnostics CurrentDiag =>
            (_paused || _resumePending) ? _frozenDiag : (_commandRecogniser != null ? _commandRecogniser.LastMatchDiagnostics : default);

        void AddToHistory(VoskMatchDiagnostics diag)
        {
            if (diag.Attempts == null || diag.Attempts.Length == 0) return;

            foreach (var attempt in diag.Attempts)
            {
                string label;
                if (attempt.IsAccepted)
                    label = $"{attempt.Intent} ({attempt.Score:F2})";
                else if (attempt.Intent != null)
                    label = $"{attempt.Intent}: {attempt.RejectReason}";
                else
                    label = attempt.RejectReason ?? "no match";

                _history.Add(new HistoryEntry
                {
                    Label = label,
                    Accepted = attempt.IsAccepted,
                    Diagnostics = diag,
                });

                while (_history.Count > MaxHistoryEntries)
                    _history.RemoveAt(0);
            }
        }

        // ─── Left Panel ─────────────────────────────────────────────────

        void DrawLeftPanel()
        {
            EditorGUILayout.LabelField("Audio & Recognition", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            DrawAudioLevels();
            EditorGUILayout.Space(8);
            DrawPartialResult();
            EditorGUILayout.Space(4);
            DrawFinalResult();
            EditorGUILayout.Space(4);
            DrawWordConfidence();
            EditorGUILayout.Space(4);
            DrawAlternatives();
        }

        void DrawAudioLevels()
        {
#if UNITY_EDITOR_WIN
            if (_speechRecogniser == null)
            {
                EditorGUILayout.LabelField("Audio levels: no speech recogniser");
                return;
            }

            float preRms = _speechRecogniser.EditorPreAgcRms;
            float postRms = _speechRecogniser.EditorPostAgcRms;
            float gain = _speechRecogniser.EditorAgcGain;

            DrawLevelMeter("Pre-AGC", preRms, Color.cyan);
            DrawLevelMeter("Post-AGC", postRms, Color.green);
            EditorGUILayout.LabelField($"AGC Gain: {gain:F2}x");
#else
            EditorGUILayout.LabelField("Audio levels: Editor-Win only");
#endif
        }

        void DrawLevelMeter(string label, float rms, Color color)
        {
            var rect = EditorGUILayout.GetControlRect(false, LevelMeterHeight);
            float labelWidth = 70f;

            var labelRect = new Rect(rect.x, rect.y, labelWidth, rect.height);
            EditorGUI.LabelField(labelRect, label);

            var meterRect = new Rect(rect.x + labelWidth, rect.y, LevelMeterWidth, rect.height);

            // Background
            EditorGUI.DrawRect(meterRect, new Color(0.15f, 0.15f, 0.15f));

            // RMS bar — scale: 0.0–0.5 RMS maps to full bar (speech rarely exceeds 0.3)
            float fill = Mathf.Clamp01(rms * 3f);
            var fillRect = new Rect(meterRect.x, meterRect.y, meterRect.width * fill, meterRect.height);
            EditorGUI.DrawRect(fillRect, color);

            // Value text
            var valueRect = new Rect(meterRect.xMax + 4, rect.y, 60, rect.height);
            EditorGUI.LabelField(valueRect, $"{rms:F4}");
        }

        void DrawPartialResult()
        {
            string partial = (_paused || _resumePending) ? "" : (_commandRecogniser?.LastPartialResult ?? "");
            EditorGUILayout.LabelField("Partial", EditorStyles.miniLabel);
            EditorGUILayout.SelectableLabel(
                string.IsNullOrEmpty(partial) ? "(listening...)" : partial,
                EditorStyles.wordWrappedLabel, GUILayout.MinHeight(20));
        }

        void DrawFinalResult()
        {
            var diag = CurrentDiag;
            string text = diag.InputText ?? "(none)";

            EditorGUILayout.LabelField("Final Result", EditorStyles.miniLabel);
            EditorGUILayout.SelectableLabel(text, EditorStyles.wordWrappedLabel,
                GUILayout.MinHeight(20));
        }

        void DrawWordConfidence()
        {
            var diag = CurrentDiag;
            if (diag.Words == null || diag.Words.Length == 0) return;

            EditorGUILayout.LabelField("Word Confidence", EditorStyles.miniLabel);

            foreach (var word in diag.Words)
            {
                var rect = EditorGUILayout.GetControlRect(false, 18f);
                float textWidth = 100f;
                float confWidth = 40f;
                float barWidth = rect.width - textWidth - confWidth - 8f;
                if (barWidth < 20f) barWidth = 20f;

                // Word text
                EditorGUI.LabelField(new Rect(rect.x, rect.y, textWidth, rect.height), word.Text);

                if (word.Confidence < 0f)
                {
                    // Confidence unavailable (maxAlternatives > 0 omits per-word conf)
                    EditorGUI.LabelField(
                        new Rect(rect.x + textWidth, rect.y, confWidth + barWidth, rect.height),
                        "[n/a]");
                    continue;
                }

                // Confidence value
                EditorGUI.LabelField(
                    new Rect(rect.x + textWidth, rect.y, confWidth, rect.height),
                    $"[{word.Confidence:F2}]");

                // Confidence bar
                var barRect = new Rect(rect.x + textWidth + confWidth + 4, rect.y + 2,
                    barWidth, rect.height - 4);
                EditorGUI.DrawRect(barRect, new Color(0.15f, 0.15f, 0.15f));

                float fill = Mathf.Clamp01(word.Confidence);
                Color barColor = word.Confidence >= 0.8f ? new Color(0.2f, 0.8f, 0.2f)
                    : word.Confidence >= 0.5f ? new Color(0.9f, 0.8f, 0.1f)
                    : new Color(0.9f, 0.2f, 0.2f);
                EditorGUI.DrawRect(
                    new Rect(barRect.x, barRect.y, barRect.width * fill, barRect.height),
                    barColor);
            }
        }

        void DrawAlternatives()
        {
            if (_speechRecogniser == null) return;

            var lastResult = _speechRecogniser.EditorLastResult;
            if (lastResult.Alternatives == null || lastResult.Alternatives.Length <= 1) return;

            EditorGUILayout.LabelField("Alternatives", EditorStyles.miniLabel);
            for (int i = 0; i < lastResult.Alternatives.Length; i++)
            {
                var alt = lastResult.Alternatives[i];
                EditorGUILayout.LabelField(
                    $"  {i}: \"{alt.Text}\" (conf={alt.Confidence:F1})");
            }
        }

        // ─── Right Panel ────────────────────────────────────────────────

        void DrawRightPanel()
        {
            EditorGUILayout.LabelField("Command Matching", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            DrawActiveSets();
            EditorGUILayout.Space(4);
            DrawPendingCommand();
            EditorGUILayout.Space(4);
            DrawLastMatchBreakdown();
            EditorGUILayout.Space(8);
            DrawHistory();
        }

        void DrawActiveSets()
        {
            var setNames = _commandRecogniser.ActiveSetNames;
            if (setNames.Length == 0)
            {
                EditorGUILayout.LabelField("Active Sets: (all commands / no sets)");
            }
            else
            {
                EditorGUILayout.LabelField($"Active Sets: {string.Join(", ", setNames)}");
            }
        }

        void DrawPendingCommand()
        {
            var pending = _commandRecogniser.EditorPendingCommand;
            if (!pending.HasValue)
                return;

            DrawSectionHeader("Pending Command");

            var p = pending.Value;
            EditorGUILayout.LabelField($"Intent: {p.Command.Intent}", EditorStyles.boldLabel);

            string reason = p.Reason == VoskPendingReason.PartialMatch
                ? "Partial match \u2014 waiting for follow-up"
                : "Awaiting confirmation";
            EditorGUILayout.LabelField($"Reason: {reason}");

            if (p.Command.Slots.Length > 0)
            {
                EditorGUILayout.LabelField("Filled slots:", EditorStyles.miniLabel);
                foreach (var slot in p.Command.Slots)
                    EditorGUILayout.LabelField($"  {slot.Name} = \"{slot.Value}\"");
            }

            if (p.UnfilledSlots != null && p.UnfilledSlots.Length > 0)
                EditorGUILayout.LabelField(
                    $"Unfilled: {string.Join(", ", p.UnfilledSlots)}");

            float elapsed = Time.time - p.CreatedTime;
            EditorGUILayout.LabelField($"Elapsed: {elapsed:F1}s");
        }

        void DrawLastMatchBreakdown()
        {
            var diag = CurrentDiag;
            if (diag.Attempts == null || diag.Attempts.Length == 0)
            {
                EditorGUILayout.LabelField("No match data yet.", EditorStyles.miniLabel);
                return;
            }

            for (int a = 0; a < diag.Attempts.Length; a++)
            {
                var attempt = diag.Attempts[a];

                if (diag.Attempts.Length > 1)
                    DrawSectionHeader($"Match {a + 1}/{diag.Attempts.Length}");
                else
                    DrawSectionHeader("Last Match");

                // Intent
                if (attempt.Intent != null)
                {
                    string icon = attempt.IsAccepted ? " [PASS]" : " [FAIL]";
                    _boldIntentStyle ??= new GUIStyle(EditorStyles.label)
                        { richText = true, fontStyle = FontStyle.Bold };
                    EditorGUILayout.LabelField($"Intent: {attempt.Intent}{icon}", _boldIntentStyle);
                }
                else
                {
                    EditorGUILayout.LabelField("Intent: (no match)", EditorStyles.boldLabel);
                }

                // Pattern
                if (attempt.Pattern != null)
                    EditorGUILayout.LabelField($"Pattern: {attempt.Pattern}");

                // Score
                DrawThresholdLine("Score", attempt.Score, attempt.MinScore);

                // Confidence
                if (attempt.AggregateConfidence >= 0f)
                    DrawThresholdLine("Confidence", attempt.AggregateConfidence, attempt.MinConfidence);
                else
                    EditorGUILayout.LabelField("Confidence: n/a (no word data)");

                // Reject reason
                if (attempt.RejectReason != null)
                {
                    _rejectStyle ??= new GUIStyle(EditorStyles.label)
                        { normal = { textColor = new Color(1f, 0.4f, 0.4f) } };
                    EditorGUILayout.LabelField($"Rejected: {attempt.RejectReason}", _rejectStyle);
                }

                // Slots
                if (attempt.Slots != null && attempt.Slots.Length > 0)
                {
                    EditorGUILayout.LabelField("Slots:", EditorStyles.miniLabel);
                    foreach (var slot in attempt.Slots)
                    {
                        string confStr = slot.Confidence >= 0f ? $" conf={slot.Confidence:F2}" : "";
                        EditorGUILayout.LabelField(
                            $"  {slot.Name} = \"{slot.Value}\"  words[{slot.StartWord}..{slot.EndWord}]{confStr}");
                    }
                }

                EditorGUILayout.Space(4);
            }
        }

        void DrawThresholdLine(string label, float value, float threshold)
        {
            bool pass = value >= threshold;
            string icon = pass ? " [PASS]" : " [FAIL]";
            Color color = pass ? new Color(0.3f, 0.9f, 0.3f) : new Color(1f, 0.4f, 0.4f);

            _thresholdStyle ??= new GUIStyle(EditorStyles.label);
            _thresholdStyle.normal.textColor = color;
            EditorGUILayout.LabelField(
                $"{label}: {value:F2} / {threshold:F2}{icon}", _thresholdStyle);
        }

        void DrawHistory()
        {
            DrawSectionHeader("History");

            if (_history.Count == 0)
            {
                EditorGUILayout.LabelField("(empty)", EditorStyles.miniLabel);
                return;
            }

            _historyScroll = EditorGUILayout.BeginScrollView(_historyScroll,
                GUILayout.MaxHeight(200));

            // Draw newest first
            for (int i = _history.Count - 1; i >= 0; i--)
            {
                var entry = _history[i];
                string icon = entry.Accepted ? "[PASS]" : "[FAIL]";
                Color color = entry.Accepted
                    ? new Color(0.3f, 0.9f, 0.3f)
                    : new Color(0.7f, 0.7f, 0.7f);

                _historyStyle ??= new GUIStyle(EditorStyles.miniLabel);
                _historyStyle.normal.textColor = color;
                EditorGUILayout.LabelField($"{icon} {entry.Label}", _historyStyle);
            }

            EditorGUILayout.EndScrollView();
        }

        // ─── Bottom Bar ─────────────────────────────────────────────────

        void DrawBottomBar()
        {
            DrawHorizontalSeparator();

            EditorGUILayout.BeginHorizontal();

            // Inject text field
            EditorGUILayout.LabelField("Inject:", GUILayout.Width(42));
            GUI.SetNextControlName("VoskInjectField");

            // Check for Enter BEFORE TextField — IMGUI TextField consumes the Return KeyDown event
            bool enterPressed = Event.current.type == EventType.KeyDown
                && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                && GUI.GetNameOfFocusedControl() == "VoskInjectField"
                && !string.IsNullOrWhiteSpace(_injectText);
            if (enterPressed) Event.current.Use();

            _injectText = EditorGUILayout.TextField(_injectText);

            if ((GUILayout.Button("Send", GUILayout.Width(50)) || enterPressed)
                && !string.IsNullOrWhiteSpace(_injectText)
                && _commandRecogniser != null)
            {
                _commandRecogniser.InjectText(_injectText);
                _injectText = "";
                GUI.FocusControl(null);
            }

            // Clear all
            if (GUILayout.Button("Clear", GUILayout.Width(50)))
            {
                _history.Clear();
                _frozenDiag = default;
                // Sync to current frame so PollDiagnostics doesn't re-add the last entry
                if (_commandRecogniser != null)
                    _lastDiagFrame = _commandRecogniser.LastMatchDiagnostics.Frame;
                _resumePending = true;
            }

            // Pause/resume
            string pauseLabel = _paused ? "Resume" : "Pause";
            if (GUILayout.Button(pauseLabel, GUILayout.Width(60)))
            {
                _paused = !_paused;
                // On resume, skip anything that arrived while paused
                // and keep showing frozen display until a genuinely new result arrives
                if (!_paused && _commandRecogniser != null)
                {
                    _lastDiagFrame = _commandRecogniser.LastMatchDiagnostics.Frame;
                    _resumePending = true;
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        // ─── Helpers ────────────────────────────────────────────────────

        static void DrawSectionHeader(string title)
        {
            EditorGUILayout.Space(2);
            var rect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(rect, new Color(0.3f, 0.3f, 0.3f));
            EditorGUILayout.LabelField(title, EditorStyles.miniBoldLabel);
        }

        static void DrawHorizontalSeparator()
        {
            EditorGUILayout.Space(2);
            var rect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(rect, new Color(0.3f, 0.3f, 0.3f));
            EditorGUILayout.Space(2);
        }

        void DrawVerticalSeparator()
        {
            var rect = EditorGUILayout.GetControlRect(false, GUILayout.Width(1));
            rect.height = position.height;
            EditorGUI.DrawRect(rect, new Color(0.3f, 0.3f, 0.3f));
        }

    }
}
