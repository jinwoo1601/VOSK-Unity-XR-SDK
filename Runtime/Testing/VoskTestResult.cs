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
    public class VoskTestResult
    {
        public readonly VoskTestCase TestCase;

        public readonly string ActualIntent;

        public readonly VoskSlotMatch[] ActualSlots;

        public readonly float Score;

        public readonly float Confidence;

        public readonly bool Passed;

        public readonly string FailureReason;

#if UNITY_EDITOR
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

    public class VoskBatchResults
    {
        public readonly VoskTestResult[] Results;

        public bool AllPassed { get; private set; }

        public int PassCount { get; private set; }

        public int FailCount { get; private set; }

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
