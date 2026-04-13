// ============================================================================
// Purpose:  Single test result and aggregated batch results with pass/fail summary
// Layer:    Runtime.Testing
// Owns:     VoskTestResult (public class), VoskBatchResults (public class)
// Depends:  VoskTestCase, VoskSlotMatch, VoskMatchDiagnostics
// ============================================================================
using System;
using System.Text;
using VoskXR.Commands;

namespace VoskXR.Testing
{
    /// <summary>
    /// Result of running a single <see cref="VoskTestCase"/> through the batch test runner.
    /// </summary>
    public class VoskTestResult
    {
        /// <summary>The test case that produced this result.</summary>
        public readonly VoskTestCase TestCase;

        /// <summary>The intent that was accepted, or null if no command passed thresholds.</summary>
        public readonly string ActualIntent;

        /// <summary>Slot matches from the accepted command.</summary>
        public readonly VoskSlotMatch[] ActualSlots;

        /// <summary>Best match score from the parser (0 if no match).</summary>
        public readonly float Score;

        /// <summary>Minimum word confidence across matched tokens (-1 if unavailable).</summary>
        public readonly float Confidence;

        /// <summary>True if the actual result matches expectations.</summary>
        public readonly bool Passed;

        /// <summary>Human-readable failure reason, or null if passed.</summary>
        public readonly string FailureReason;

#if UNITY_EDITOR
        /// <summary>Full diagnostic data from the parse cycle (Editor only).</summary>
        internal readonly VoskMatchDiagnostics Diagnostics;
#endif

        public VoskTestResult(VoskTestCase testCase, string actualIntent,
            VoskSlotMatch[] actualSlots, float score, float confidence,
            bool passed, string failureReason)
        {
            TestCase = testCase;
            ActualIntent = actualIntent;
            ActualSlots = actualSlots ?? Array.Empty<VoskSlotMatch>();
            Score = score;
            Confidence = confidence;
            Passed = passed;
            FailureReason = failureReason;
        }

#if UNITY_EDITOR
        internal VoskTestResult(VoskTestCase testCase, string actualIntent,
            VoskSlotMatch[] actualSlots, float score, float confidence,
            bool passed, string failureReason, VoskMatchDiagnostics diagnostics)
            : this(testCase, actualIntent, actualSlots, score, confidence, passed, failureReason)
        {
            Diagnostics = diagnostics;
        }
#endif
    }

    /// <summary>
    /// Aggregated results from <see cref="VoskBatchTestRunner.RunAll"/>.
    /// Provides <see cref="AllPassed"/> and <see cref="FailureSummary"/> for
    /// convenient use in NUnit assertions.
    /// </summary>
    public class VoskBatchResults
    {
        /// <summary>Individual results for each test case, in input order.</summary>
        public readonly VoskTestResult[] Results;

        /// <summary>True when every test case passed.</summary>
        public bool AllPassed { get; private set; }

        /// <summary>Number of test cases that passed.</summary>
        public int PassCount { get; private set; }

        /// <summary>Number of test cases that failed.</summary>
        public int FailCount { get; private set; }

        /// <summary>
        /// Multi-line summary of all failures. Empty string when all passed.
        /// Suitable for passing as the message argument to <c>Assert.IsTrue</c>.
        /// </summary>
        public string FailureSummary
        {
            get
            {
                var sb = new StringBuilder();
                for (int i = 0; i < Results.Length; i++)
                {
                    if (Results[i].Passed) continue;
                    if (sb.Length > 0) sb.AppendLine();

                    var r = Results[i];
                    string desc = string.IsNullOrEmpty(r.TestCase.description)
                        ? r.TestCase.input
                        : r.TestCase.description;
                    sb.Append($"[{i}] {desc}: {r.FailureReason}");
                }
                return sb.ToString();
            }
        }

        public VoskBatchResults(VoskTestResult[] results)
        {
            Results = results ?? throw new ArgumentNullException(nameof(results));
            Recount();
        }

        /// <summary>
        /// Recalculates PassCount, FailCount, and AllPassed after in-place result updates.
        /// </summary>
        internal void Recount()
        {
            int pass = 0;
            for (int i = 0; i < Results.Length; i++)
                if (Results[i].Passed) pass++;
            PassCount = pass;
            FailCount = Results.Length - pass;
            AllPassed = pass == Results.Length;
        }
    }
}
