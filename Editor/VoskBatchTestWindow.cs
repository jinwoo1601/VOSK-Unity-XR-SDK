// ============================================================================
// Purpose:  IMGUI EditorWindow for running batch test suites with results table and CSV export
// Layer:    Editor
// Owns:     VoskBatchTestWindow (public EditorWindow)
// Depends:  VoskBatchTestRunner, VoskTestSuiteAsset, VoskSlotAsset, VoskCommandSetAsset, VoskTestResult
// ============================================================================
using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using VoskXR.Commands;
using VoskXR.Testing;

namespace VoskXR.Editor
{
    /// <summary>
    /// EditorWindow for visually running batch test suites against command definitions.
    /// Displays a results table with pass/fail, score, per-row diagnostics expansion,
    /// and CSV export.
    /// </summary>
    public class VoskBatchTestWindow : EditorWindow
    {
        [SerializeField] VoskTestSuiteAsset testSuite;
        [SerializeField] VoskSlotAsset[] slotAssets;
        [SerializeField] VoskCommandSetAsset[] commandSetAssets;
        [SerializeField] string[] activeSetNames;

        [SerializeField] float minScore = 0.6f;
        [SerializeField] float minConfidence = 0.4f;

        VoskBatchResults _results;
        bool[] _expanded;
        Vector2 _scroll;

        GUIStyle _passStyle;
        GUIStyle _failStyle;
        GUIStyle _headerStyle;

        SerializedObject _serializedSelf;
        SerializedProperty _propTestSuite;
        SerializedProperty _propSlotAssets;
        SerializedProperty _propCommandSetAssets;
        SerializedProperty _propActiveSetNames;

        [MenuItem("Window/VOSK XR/Batch Test Runner")]
        static void Open()
        {
            GetWindow<VoskBatchTestWindow>("VOSK Batch Tests");
        }

        void OnEnable()
        {
            _serializedSelf = new SerializedObject(this);
            _propTestSuite = _serializedSelf.FindProperty(nameof(testSuite));
            _propSlotAssets = _serializedSelf.FindProperty(nameof(slotAssets));
            _propCommandSetAssets = _serializedSelf.FindProperty(nameof(commandSetAssets));
            _propActiveSetNames = _serializedSelf.FindProperty(nameof(activeSetNames));
        }

        void OnDisable()
        {
            _passStyle = null;
            _failStyle = null;
            _headerStyle = null;
        }

        void OnGUI()
        {
            _serializedSelf.Update();

            DrawConfiguration();
            EditorGUILayout.Space(4);
            DrawToolbar();
            EditorGUILayout.Space(4);
            DrawResults();

            _serializedSelf.ApplyModifiedProperties();
        }

        // ─── Configuration ──────────────────────────────────────────────

        void DrawConfiguration()
        {
            EditorGUILayout.LabelField("Configuration", EditorStyles.boldLabel);

            EditorGUILayout.PropertyField(_propTestSuite, new GUIContent("Test Suite"));

            EditorGUILayout.PropertyField(_propSlotAssets,
                new GUIContent("Slot Definitions"), true);

            EditorGUILayout.PropertyField(_propCommandSetAssets,
                new GUIContent("Command Sets"), true);

            EditorGUILayout.PropertyField(_propActiveSetNames,
                new GUIContent("Active Sets"), true);

            EditorGUILayout.Space(2);
            minScore = EditorGUILayout.FloatField("Min Score", minScore);
            minConfidence = EditorGUILayout.FloatField("Min Confidence", minConfidence);
        }

        // ─── Toolbar ────────────────────────────────────────────────────

        void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal();

            bool canRun = testSuite != null && testSuite.cases.Count > 0
                && slotAssets != null && slotAssets.Length > 0
                && commandSetAssets != null && commandSetAssets.Length > 0;

            GUI.enabled = canRun;
            if (GUILayout.Button("Run All", GUILayout.Width(80)))
                RunAll();

            GUI.enabled = canRun && _results != null && _results.FailCount > 0;
            if (GUILayout.Button("Re-run Failed", GUILayout.Width(100)))
                RerunFailed();

            GUI.enabled = _results != null;
            if (GUILayout.Button("Export CSV", GUILayout.Width(80)))
                ExportCsv();

            GUI.enabled = true;

            GUILayout.FlexibleSpace();

            if (testSuite != null)
            {
                if (GUILayout.Button("Import JSON", GUILayout.Width(90)))
                    ImportJson();
                if (GUILayout.Button("Export JSON", GUILayout.Width(90)))
                    ExportJson();
            }

            EditorGUILayout.EndHorizontal();

            if (!canRun && testSuite != null)
            {
                string missing = "";
                if (slotAssets == null || slotAssets.Length == 0) missing += "slot definitions, ";
                if (commandSetAssets == null || commandSetAssets.Length == 0) missing += "command sets, ";
                if (testSuite.cases.Count == 0) missing += "test cases, ";
                if (missing.Length > 0) missing = missing.Substring(0, missing.Length - 2);
                EditorGUILayout.HelpBox($"Missing: {missing}", MessageType.Info);
            }

            // Summary bar
            if (_results != null)
            {
                string summary = $"{_results.PassCount} passed, {_results.FailCount} failed " +
                    $"/ {_results.Results.Length} total";
                MessageType msgType = _results.AllPassed ? MessageType.Info : MessageType.Warning;
                EditorGUILayout.HelpBox(summary, msgType);
            }
        }

        // ─── Results Table ──────────────────────────────────────────────

        void DrawResults()
        {
            if (_results == null) return;

            _headerStyle ??= new GUIStyle(EditorStyles.miniLabel)
                { fontStyle = FontStyle.Bold };
            _passStyle ??= new GUIStyle(EditorStyles.label)
                { normal = { textColor = new Color(0.2f, 0.8f, 0.2f) } };
            _failStyle ??= new GUIStyle(EditorStyles.label)
                { normal = { textColor = new Color(1f, 0.4f, 0.4f) } };

            // Header row
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("", GUILayout.Width(20));
            EditorGUILayout.LabelField("Input", _headerStyle, GUILayout.MinWidth(150));
            EditorGUILayout.LabelField("Expected", _headerStyle, GUILayout.Width(120));
            EditorGUILayout.LabelField("Result", _headerStyle, GUILayout.Width(120));
            EditorGUILayout.LabelField("Score", _headerStyle, GUILayout.Width(50));
            EditorGUILayout.LabelField("Status", _headerStyle, GUILayout.Width(50));
            EditorGUILayout.EndHorizontal();

            DrawHorizontalSeparator();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            for (int i = 0; i < _results.Results.Length; i++)
                DrawResultRow(i);

            EditorGUILayout.EndScrollView();
        }

        void DrawResultRow(int index)
        {
            var r = _results.Results[index];
            var style = r.Passed ? _passStyle : _failStyle;

            EditorGUILayout.BeginHorizontal();

            // Expand toggle
            bool wasExpanded = _expanded != null && index < _expanded.Length && _expanded[index];
            bool nowExpanded = EditorGUILayout.Toggle(wasExpanded, GUILayout.Width(20));
            if (_expanded != null && index < _expanded.Length)
                _expanded[index] = nowExpanded;

            // Input
            EditorGUILayout.LabelField(Truncate(r.TestCase.input, 60),
                EditorStyles.label, GUILayout.MinWidth(150));

            // Expected
            string expected = r.TestCase.ExpectsRejection ? "(none)" : r.TestCase.expectedIntent;
            EditorGUILayout.LabelField(expected, GUILayout.Width(120));

            // Actual result
            string actual = FormatActual(r);
            EditorGUILayout.LabelField(actual, GUILayout.Width(120));

            // Score
            EditorGUILayout.LabelField(r.Score > 0f ? $"{r.Score:F2}" : "-",
                GUILayout.Width(50));

            // Status
            EditorGUILayout.LabelField(r.Passed ? "PASS" : "FAIL", style,
                GUILayout.Width(50));

            EditorGUILayout.EndHorizontal();

            // Expanded detail
            if (nowExpanded)
                DrawExpandedDetail(r);
        }

        void DrawExpandedDetail(VoskTestResult r)
        {
            EditorGUI.indentLevel += 2;

            if (!string.IsNullOrEmpty(r.TestCase.description))
                EditorGUILayout.LabelField($"Description: {r.TestCase.description}",
                    EditorStyles.wordWrappedLabel);

            if (r.FailureReason != null)
                EditorGUILayout.LabelField($"Failure: {r.FailureReason}",
                    _failStyle ?? EditorStyles.label);

            if (r.Confidence >= 0f)
                EditorGUILayout.LabelField($"Confidence: {r.Confidence:F2}");

            if (r.TestCase.wordConfidence >= 0f)
                EditorGUILayout.LabelField(
                    $"Simulated word confidence: {r.TestCase.wordConfidence:F2}");

            // Slots
            if (r.ActualSlots != null && r.ActualSlots.Length > 0)
            {
                EditorGUILayout.LabelField("Slots:");
                EditorGUI.indentLevel++;
                foreach (var slot in r.ActualSlots)
                    EditorGUILayout.LabelField($"{slot.Name} = \"{slot.Value}\"");
                EditorGUI.indentLevel--;
            }

#if UNITY_EDITOR
            // Diagnostic attempts
            if (r.Diagnostics.Attempts != null && r.Diagnostics.Attempts.Length > 0)
            {
                EditorGUILayout.LabelField("Diagnostics:");
                EditorGUI.indentLevel++;
                foreach (var attempt in r.Diagnostics.Attempts)
                {
                    string status = attempt.IsAccepted ? "[PASS]" : "[FAIL]";
                    string intent = attempt.Intent ?? "(no match)";
                    EditorGUILayout.LabelField(
                        $"{status} {intent} — score {attempt.Score:F2}/{attempt.MinScore:F2}");

                    if (attempt.Pattern != null)
                        EditorGUILayout.LabelField($"  Pattern: {attempt.Pattern}");

                    if (attempt.RejectReason != null)
                        EditorGUILayout.LabelField($"  Rejected: {attempt.RejectReason}");
                }
                EditorGUI.indentLevel--;
            }
#endif

            EditorGUI.indentLevel -= 2;
            EditorGUILayout.Space(4);
        }

        // ─── Actions ────────────────────────────────────────────────────

        void RunAll()
        {
            var runner = CreateRunner();
            if (runner == null) return;

            _results = runner.RunAll(testSuite.ToArray());
            _expanded = new bool[_results.Results.Length];
        }

        void RerunFailed()
        {
            if (_results == null) return;

            var runner = CreateRunner();
            if (runner == null) return;

            for (int i = 0; i < _results.Results.Length; i++)
            {
                if (!_results.Results[i].Passed)
                    _results.Results[i] = runner.Run(_results.Results[i].TestCase);
            }

            _results.Recount();
        }

        VoskBatchTestRunner CreateRunner()
        {
            VoskSlotDefinition[] slots;
            try
            {
                slots = BuildSlots();
            }
            catch (Exception e)
            {
                Debug.LogError($"[VoskBatchTest] Failed to build slots: {e.Message}");
                return null;
            }

            try
            {
                var sets = BuildSets();

                if (activeSetNames != null && activeSetNames.Length > 0)
                    return new VoskBatchTestRunner(slots, sets, activeSetNames,
                        minScore, minConfidence);

                // No active set filter — use all commands from all sets
                var allNames = new string[sets.Length];
                for (int i = 0; i < sets.Length; i++)
                    allNames[i] = sets[i].Name;

                return new VoskBatchTestRunner(slots, sets, allNames,
                    minScore, minConfidence);
            }
            catch (Exception e)
            {
                Debug.LogError($"[VoskBatchTest] Failed to create runner: {e.Message}");
                return null;
            }
        }

        VoskSlotDefinition[] BuildSlots()
        {
            var list = new VoskSlotDefinition[slotAssets.Length];
            for (int i = 0; i < slotAssets.Length; i++)
            {
                if (slotAssets[i] == null)
                    throw new InvalidOperationException($"slotAssets[{i}] is null.");
                list[i] = slotAssets[i].ToDefinition();
            }
            return list;
        }

        VoskCommandSet[] BuildSets()
        {
            var list = new VoskCommandSet[commandSetAssets.Length];
            for (int i = 0; i < commandSetAssets.Length; i++)
            {
                if (commandSetAssets[i] == null)
                    throw new InvalidOperationException($"commandSetAssets[{i}] is null.");
                list[i] = commandSetAssets[i].ToSet();
            }
            return list;
        }

        void ExportCsv()
        {
            if (_results == null) return;

            string path = EditorUtility.SaveFilePanel("Export CSV", "", "test-results.csv", "csv");
            if (string.IsNullOrEmpty(path)) return;

            File.WriteAllText(path, VoskBatchTestRunner.ToCsv(_results));
            Debug.Log($"[VoskBatchTest] Exported CSV to {path}");
        }

        void ImportJson()
        {
            if (testSuite == null) return;

            string path = EditorUtility.OpenFilePanel("Import Test Cases", "", "json");
            if (string.IsNullOrEmpty(path)) return;

            string json = File.ReadAllText(path);
            Undo.RecordObject(testSuite, "Import Test Cases");
            testSuite.FromJson(json);
            EditorUtility.SetDirty(testSuite);
            _results = null;
            Debug.Log($"[VoskBatchTest] Imported {testSuite.cases.Count} test cases from {path}");
        }

        void ExportJson()
        {
            if (testSuite == null) return;

            string path = EditorUtility.SaveFilePanel("Export Test Cases", "",
                testSuite.suiteName + ".json", "json");
            if (string.IsNullOrEmpty(path)) return;

            File.WriteAllText(path, testSuite.ToJson());
            Debug.Log($"[VoskBatchTest] Exported {testSuite.cases.Count} test cases to {path}");
        }

        // ─── Helpers ────────────────────────────────────────────────────

        static string FormatActual(VoskTestResult r)
        {
            if (r.ActualIntent == null)
                return "(none)";

            if (r.ActualSlots != null && r.ActualSlots.Length > 0)
            {
                var sb = new System.Text.StringBuilder();
                sb.Append(r.ActualIntent);
                sb.Append('(');
                for (int i = 0; i < r.ActualSlots.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(r.ActualSlots[i].Name);
                    sb.Append(':');
                    sb.Append(r.ActualSlots[i].Value);
                }
                sb.Append(')');
                return sb.ToString();
            }

            return r.ActualIntent;
        }

        static string Truncate(string value, int maxLen)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Length <= maxLen ? value : value.Substring(0, maxLen - 3) + "...";
        }

        static void DrawHorizontalSeparator() => VoskEditorGUI.DrawHorizontalSeparator();
    }
}
