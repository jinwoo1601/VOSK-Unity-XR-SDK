using System;
using System.Collections.Generic;
using System.Text;
using VoskXR.Commands;

namespace VoskXR.Testing
{
    /// <summary>
    /// Feeds a list of test cases through the command parser and produces a results matrix.
    /// Pure C# — no MonoBehaviour dependency, works in Edit Mode and CI.
    /// Reuses <see cref="VoskCommandParser"/> directly (the same path that
    /// <see cref="VoskCommandRecogniser.InjectText"/> uses internally).
    /// </summary>
    public class VoskBatchTestRunner
    {
        readonly VoskCommandParser _parser;
        readonly float _minScore;
        readonly float _minConfidence;

        /// <summary>
        /// Creates a batch test runner with the given slot and command definitions.
        /// All commands are active (no command sets).
        /// </summary>
        /// <param name="slots">Slot definitions used by the commands.</param>
        /// <param name="commands">Command definitions to test against.</param>
        /// <param name="minScore">Minimum match score threshold (default 0.6).</param>
        /// <param name="minConfidence">Minimum word confidence threshold (default 0.4).</param>
        public VoskBatchTestRunner(VoskSlotDefinition[] slots, VoskCommandDefinition[] commands,
            float minScore = 0.6f, float minConfidence = 0.4f)
        {
            if (slots == null) throw new ArgumentNullException(nameof(slots));
            if (commands == null) throw new ArgumentNullException(nameof(commands));

            _parser = new VoskCommandParser(slots, commands);
            _minScore = minScore;
            _minConfidence = minConfidence;
        }

        /// <summary>
        /// Creates a batch test runner with named command sets. Only commands from
        /// the specified active sets are tested.
        /// </summary>
        /// <param name="slots">Slot definitions used by the commands.</param>
        /// <param name="sets">Named command sets.</param>
        /// <param name="activeSetNames">Which sets to activate.</param>
        /// <param name="minScore">Minimum match score threshold (default 0.6).</param>
        /// <param name="minConfidence">Minimum word confidence threshold (default 0.4).</param>
        public VoskBatchTestRunner(VoskSlotDefinition[] slots, VoskCommandSet[] sets,
            string[] activeSetNames, float minScore = 0.6f, float minConfidence = 0.4f)
        {
            if (slots == null) throw new ArgumentNullException(nameof(slots));
            if (sets == null) throw new ArgumentNullException(nameof(sets));
            if (activeSetNames == null) throw new ArgumentNullException(nameof(activeSetNames));

            var setLookup = new Dictionary<string, VoskCommandSet>(sets.Length, StringComparer.Ordinal);
            foreach (var set in sets)
                setLookup[set.Name] = set;

            int total = 0;
            foreach (var name in activeSetNames)
            {
                if (!setLookup.ContainsKey(name))
                    throw new ArgumentException($"Unknown command set name: '{name}'.", nameof(activeSetNames));
                total += setLookup[name].Commands.Length;
            }

            var commands = new VoskCommandDefinition[total];
            int offset = 0;
            foreach (var name in activeSetNames)
            {
                var c = setLookup[name].Commands;
                Array.Copy(c, 0, commands, offset, c.Length);
                offset += c.Length;
            }

            _parser = new VoskCommandParser(slots, commands);
            _minScore = minScore;
            _minConfidence = minConfidence;
        }

        /// <summary>
        /// Runs all test cases and returns aggregated results.
        /// </summary>
        public VoskBatchResults RunAll(VoskTestCase[] testCases)
        {
            if (testCases == null) throw new ArgumentNullException(nameof(testCases));

            var results = new VoskTestResult[testCases.Length];
            for (int i = 0; i < testCases.Length; i++)
                results[i] = Run(testCases[i]);

            return new VoskBatchResults(results);
        }

        /// <summary>
        /// Runs a single test case through the parser and threshold filter.
        /// </summary>
        public VoskTestResult Run(VoskTestCase testCase)
        {
            if (testCase == null) throw new ArgumentNullException(nameof(testCase));

            VoskWord[] words = null;
            if (testCase.wordConfidence >= 0f)
                words = VoskSpeechRecogniser.CreateSimulatedWords(
                    testCase.input, testCase.wordConfidence);

            var parseResults = words != null
                ? _parser.Parse(testCase.input, words)
                : _parser.Parse(testCase.input);

            string acceptedIntent = null;
            VoskSlotMatch[] acceptedSlots = null;
            float bestScore = 0f;
            float bestConfidence = -1f;
            string rejectReason = null;

            if (parseResults.Length == 0)
            {
                if (testCase.ExpectsRejection)
                    return MakeResult(testCase, null, null, 0f, -1f, true, null,
                        parseResults, words);

                return MakeResult(testCase, null, null, 0f, -1f, false,
                    $"expected intent '{testCase.expectedIntent}' but no pattern matched",
                    parseResults, words);
            }

            for (int i = 0; i < parseResults.Length; i++)
            {
                var cmd = parseResults[i].Command;

                if (!PassesThresholds(cmd, out string reason))
                {
                    if (cmd.Score > bestScore)
                    {
                        bestScore = cmd.Score;
                        bestConfidence = cmd.Confidence;
                        rejectReason = reason;
                    }
                    continue;
                }

                acceptedIntent = cmd.Intent;
                acceptedSlots = cmd.Slots;
                bestScore = cmd.Score;
                bestConfidence = cmd.Confidence;
                rejectReason = null;
                break;
            }

            // Compare against expectations
            bool passed;
            string failureReason;

            if (testCase.ExpectsRejection)
            {
                if (acceptedIntent == null)
                {
                    passed = true;
                    failureReason = null;
                }
                else
                {
                    passed = false;
                    failureReason = $"expected rejection but got intent '{acceptedIntent}'";
                }
            }
            else if (acceptedIntent == null)
            {
                passed = false;
                failureReason = rejectReason != null
                    ? $"expected intent '{testCase.expectedIntent}' but rejected: {rejectReason}"
                    : $"expected intent '{testCase.expectedIntent}' but no command accepted";
            }
            else if (!string.Equals(acceptedIntent, testCase.expectedIntent, StringComparison.Ordinal))
            {
                passed = false;
                failureReason = $"expected intent '{testCase.expectedIntent}' but got '{acceptedIntent}'";
            }
            else
            {
                string slotFailure = CheckSlots(testCase.expectedSlots, acceptedSlots);
                passed = slotFailure == null;
                failureReason = slotFailure;
            }

            return MakeResult(testCase, acceptedIntent, acceptedSlots, bestScore, bestConfidence,
                passed, failureReason, parseResults, words);
        }

        bool PassesThresholds(VoskCommand cmd, out string rejectReason)
        {
            if (cmd.Score < _minScore)
            {
                rejectReason = $"score {cmd.Score:F2} < minScore {_minScore:F2}";
                return false;
            }
            if (cmd.Confidence >= 0f && cmd.Confidence < _minConfidence)
            {
                rejectReason = $"confidence {cmd.Confidence:F2} < minConfidence {_minConfidence:F2}";
                return false;
            }
            rejectReason = null;
            return true;
        }

        static string CheckSlots(ExpectedSlot[] expected, VoskSlotMatch[] actual)
        {
            if (expected == null || expected.Length == 0)
                return null;

            foreach (var exp in expected)
            {
                bool found = false;
                for (int i = 0; i < actual.Length; i++)
                {
                    if (string.Equals(actual[i].Name, exp.name, StringComparison.Ordinal))
                    {
                        found = true;
                        if (!string.Equals(actual[i].Value, exp.value, StringComparison.Ordinal))
                        {
                            return $"slot '{exp.name}': expected '{exp.value}' but got '{actual[i].Value}'";
                        }
                        break;
                    }
                }

                if (!found)
                    return $"expected slot '{exp.name}' not found in result";
            }

            return null;
        }

        VoskTestResult MakeResult(VoskTestCase testCase, string actualIntent,
            VoskSlotMatch[] actualSlots, float score, float confidence,
            bool passed, string failureReason,
            VoskCommandResult[] parseResults, VoskWord[] words)
        {
#if UNITY_EDITOR
            // Build per-command diagnostic attempts from parser diagnostics
            var parseDiag = _parser.LastParseDiagnostics;
            VoskMatchAttempt[] attempts;

            if (parseResults.Length > 0 && parseDiag != null && parseDiag.Length > 0)
            {
                int count = Math.Min(parseResults.Length, parseDiag.Length);
                attempts = new VoskMatchAttempt[count];
                for (int i = 0; i < count; i++)
                {
                    var cmd = parseResults[i].Command;
                    bool accepted = PassesThresholds(cmd, out string reason);

                    attempts[i] = new VoskMatchAttempt(
                        cmd.Intent, parseDiag[i].PatternString,
                        cmd.Score, _minScore, cmd.Confidence, _minConfidence,
                        null, reason, accepted);
                }
            }
            else
            {
                attempts = new[] { new VoskMatchAttempt(
                    actualIntent, null, score, _minScore,
                    confidence, _minConfidence, null,
                    failureReason ?? "no match", actualIntent != null) };
            }

            var diagWords = words ?? Array.Empty<VoskWord>();
            var diagnostics = new VoskMatchDiagnostics(testCase.input, diagWords, attempts, 0);

            return new VoskTestResult(testCase, actualIntent, actualSlots,
                score, confidence, passed, failureReason, diagnostics);
#else
            return new VoskTestResult(testCase, actualIntent, actualSlots,
                score, confidence, passed, failureReason);
#endif
        }

        /// <summary>
        /// Exports batch results as a CSV string for diffing across runs.
        /// </summary>
        public static string ToCsv(VoskBatchResults results)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Input,Expected,Actual,Score,Confidence,Status,Reason");

            foreach (var r in results.Results)
            {
                string expected = r.TestCase.ExpectsRejection ? "(none)" : r.TestCase.expectedIntent;
                string actual = r.ActualIntent ?? "(none)";
                string status = r.Passed ? "PASS" : "FAIL";
                string reason = r.FailureReason != null ? CsvEscape(r.FailureReason) : "";

                sb.AppendLine($"{CsvEscape(r.TestCase.input)},{CsvEscape(expected)}," +
                    $"{CsvEscape(actual)},{r.Score:F2},{r.Confidence:F2},{status},{reason}");
            }

            return sb.ToString();
        }

        static string CsvEscape(string value)
        {
            if (value == null) return "";
            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }
    }
}
