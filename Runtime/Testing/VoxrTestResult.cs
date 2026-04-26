// ============================================================================
// Purpose:  Single test result and aggregated batch results with pass/fail summary
// Layer:    Runtime.Testing
// Owns:     VoxrTestResult (public class), VoxrBatchResults (public class)
// Depends:  VoxrTestCase, VoxrSlotMatch, VoxrMatchDiagnostics
// ============================================================================
using System;
using System.Text;
using VoXR.Commands;

namespace VoXR.Testing
{
    public class VoxrTestResult
    {
        public readonly VoxrTestCase TestCase;

        public readonly string ActualIntent;

        public readonly VoxrSlotMatch[] ActualSlots;

        public readonly float Score;

        public readonly float Confidence;

        public readonly bool Passed;

        public readonly string FailureReason;

#if UNITY_EDITOR
        internal readonly VoxrMatchDiagnostics Diagnostics;
#endif

        public VoxrTestResult(VoxrTestCase testCase, string actualIntent,
            VoxrSlotMatch[] actualSlots, float score, float confidence,
            bool passed, string failureReason)
        {
            TestCase = testCase;
            ActualIntent = actualIntent;
            ActualSlots = actualSlots ?? Array.Empty<VoxrSlotMatch>();
            Score = score;
            Confidence = confidence;
            Passed = passed;
            FailureReason = failureReason;
        }

#if UNITY_EDITOR
        internal VoxrTestResult(VoxrTestCase testCase, string actualIntent,
            VoxrSlotMatch[] actualSlots, float score, float confidence,
            bool passed, string failureReason, VoxrMatchDiagnostics diagnostics)
            : this(testCase, actualIntent, actualSlots, score, confidence, passed, failureReason)
        {
            Diagnostics = diagnostics;
        }
#endif
    }

    public class VoxrBatchResults
    {
        public readonly VoxrTestResult[] Results;

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

        public VoxrBatchResults(VoxrTestResult[] results)
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
