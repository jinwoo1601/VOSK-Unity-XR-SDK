// ============================================================================
// Purpose:  Feeds test cases through VoxrCommandParser with threshold filtering, produces results matrix
// Layer:    Runtime.Testing
// Owns:     VoxrBatchTestRunner (public class)
// Depends:  VoxrCommandParser, VoxrSlotDefinition, VoxrCommandDefinition, VoxrCommandSet, VoxrTestCase, VoxrTestResult, VoxrSpeechRecogniser
// ============================================================================
using System;
using System.Collections.Generic;
using System.Text;
using VoXR.Commands;

namespace VoXR.Testing
{
    public class VoxrBatchTestRunner
    {
        readonly VoxrCommandParser _parser;
        readonly float _minScore;
        readonly float _minConfidence;

        public VoxrBatchTestRunner(VoxrSlotDefinition[] slots, VoxrCommandDefinition[] commands,
            float minScore = 0.6f, float minConfidence = 0.4f,
            float skippedWordPenalty = VoxrCommandParser.DefaultSkippedWordPenalty)
        {
            if (slots == null) throw new ArgumentNullException(nameof(slots));
            if (commands == null) throw new ArgumentNullException(nameof(commands));

            _parser = new VoxrCommandParser(slots, commands, skippedWordPenalty);
            _minScore = minScore;
            _minConfidence = minConfidence;
        }

        public VoxrBatchTestRunner(VoxrSlotDefinition[] slots, VoxrCommandSet[] sets,
            string[] activeSetNames, float minScore = 0.6f, float minConfidence = 0.4f,
            float skippedWordPenalty = VoxrCommandParser.DefaultSkippedWordPenalty)
        {
            if (slots == null) throw new ArgumentNullException(nameof(slots));
            if (sets == null) throw new ArgumentNullException(nameof(sets));
            if (activeSetNames == null) throw new ArgumentNullException(nameof(activeSetNames));

            var setLookup = new Dictionary<string, VoxrCommandSet>(sets.Length, StringComparer.Ordinal);
            foreach (var set in sets)
                setLookup[set.Name] = set;

            int total = 0;
            foreach (var name in activeSetNames)
            {
                if (!setLookup.ContainsKey(name))
                    throw new ArgumentException($"Unknown command set name: '{name}'.", nameof(activeSetNames));
                total += setLookup[name].Commands.Length;
            }

            var commands = new VoxrCommandDefinition[total];
            int offset = 0;
            foreach (var name in activeSetNames)
            {
                var c = setLookup[name].Commands;
                Array.Copy(c, 0, commands, offset, c.Length);
                offset += c.Length;
            }

            _parser = new VoxrCommandParser(slots, commands, skippedWordPenalty);
            _minScore = minScore;
            _minConfidence = minConfidence;
        }

        public VoxrBatchResults RunAll(VoxrTestCase[] testCases)
        {
            if (testCases == null) throw new ArgumentNullException(nameof(testCases));

            var results = new VoxrTestResult[testCases.Length];
            for (int i = 0; i < testCases.Length; i++)
                results[i] = Run(testCases[i]);

            return new VoxrBatchResults(results);
        }

        public VoxrTestResult Run(VoxrTestCase testCase)
        {
            if (testCase == null) throw new ArgumentNullException(nameof(testCase));

            VoxrWord[] words = null;
            if (testCase.wordConfidence >= 0f)
                words = VoxrSpeechRecogniser.CreateSimulatedWords(
                    testCase.input, testCase.wordConfidence);

            var parseResults = words != null
                ? _parser.Parse(testCase.input, words)
                : _parser.Parse(testCase.input);

            string acceptedIntent = null;
            VoxrSlotMatch[] acceptedSlots = null;
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

        bool PassesThresholds(VoxrCommand cmd, out string rejectReason)
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

        static string CheckSlots(ExpectedSlot[] expected, VoxrSlotMatch[] actual)
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

        VoxrTestResult MakeResult(VoxrTestCase testCase, string actualIntent,
            VoxrSlotMatch[] actualSlots, float score, float confidence,
            bool passed, string failureReason,
            VoxrCommandResult[] parseResults, VoxrWord[] words)
        {
#if UNITY_EDITOR
            // Build per-command diagnostic attempts from parser diagnostics
            var parseDiag = _parser.LastParseDiagnostics;
            VoxrMatchAttempt[] attempts;

            if (parseResults.Length > 0 && parseDiag != null && parseDiag.Length > 0)
            {
                int count = Math.Min(parseResults.Length, parseDiag.Length);
                attempts = new VoxrMatchAttempt[count];
                for (int i = 0; i < count; i++)
                {
                    var cmd = parseResults[i].Command;
                    bool accepted = PassesThresholds(cmd, out string reason);

                    attempts[i] = new VoxrMatchAttempt(
                        cmd.Intent, parseDiag[i].PatternString,
                        cmd.Score, _minScore, cmd.Confidence, _minConfidence,
                        null, reason, accepted);
                }
            }
            else
            {
                attempts = new[] { new VoxrMatchAttempt(
                    actualIntent, null, score, _minScore,
                    confidence, _minConfidence, null,
                    failureReason ?? "no match", actualIntent != null) };
            }

            var diagWords = words ?? Array.Empty<VoxrWord>();
            var diagnostics = new VoxrMatchDiagnostics(testCase.input, diagWords, attempts, 0);

            return new VoxrTestResult(testCase, actualIntent, actualSlots,
                score, confidence, passed, failureReason, diagnostics);
#else
            return new VoxrTestResult(testCase, actualIntent, actualSlots,
                score, confidence, passed, failureReason);
#endif
        }

        public static string ToCsv(VoxrBatchResults results)
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
