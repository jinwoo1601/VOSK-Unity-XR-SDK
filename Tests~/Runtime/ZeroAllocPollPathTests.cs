using System;
using System.Text;
using NUnit.Framework;
using UnityEngine.Profiling;
using UnityEngine.TestTools.Constraints;
using VoXR;
// UnityEngine.TestTools.Constraints.Is derives from NUnit's and adds AllocatingGCMemory().
// Aliased because both namespaces export the name.
using Is = UnityEngine.TestTools.Constraints.Is;

namespace VoXR.Tests.Runtime
{
    // Regression guard for the zero-alloc parsing hot path. The poll loop should
    // allocate only the leaf string returned to consumers — anything else
    // (Substring, boxing, freshly allocated key arrays) is a regression.
    //
    // Both tests below measure through Unity's GC.Alloc profiler recorder, NOT through
    // GC.GetAllocatedBytesForCurrentThread. That counter is INERT on this runtime —
    // measured at 0 B moved after a deliberate 1 MB allocation — so the byte-delta
    // assertions this file was written with read zero whatever the code does, and both
    // passed unconditionally from the day they were written (issue #105).
    public class ZeroAllocPollPathTests
    {
        const int Iterations = 100;

        [Test]
        public void ParsingPartialJson_AllocatesOnlyTheReturnedString()
        {
            byte[] json = Encoding.UTF8.GetBytes("{\"partial\":\"hello world\"}");

            // Warm up — first call may JIT/touch statics.
            VoxrJsonParser.ParseTextFromJson(json, isFinal: false);

            int allocations = GCAllocationsDuring(() =>
            {
                for (int i = 0; i < Iterations; i++)
                    VoxrJsonParser.ParseTextFromJson(json, isFinal: false);
            });

            // Each call returns one managed string (the partial text) and is allowed
            // exactly that one allocation. Anything beyond it — an inadvertent Substring,
            // a boxed value type, a freshly built key array — shows up as a second
            // allocation per call and fails here.
            Assert.AreEqual(
                Iterations,
                allocations,
                $"{allocations} GC allocation(s) over {Iterations} calls; expected exactly "
                    + $"{Iterations} — one returned string per call and nothing else."
            );
        }

        [Test]
        public void ParsingErrorCode_AllocatesNothing()
        {
            byte[] json = Encoding.UTF8.GetBytes("{\"error\":\"x\",\"code\":3}");

            VoxrJsonParser.ParseErrorCode(json);  // warm up

            // Returns an enum (value type), so nothing at all is permitted and Unity's
            // zero-or-fail constraint expresses it exactly.
            Assert.That(
                () =>
                {
                    for (int i = 0; i < Iterations; i++)
                        VoxrJsonParser.ParseErrorCode(json);
                },
                Is.Not.AllocatingGCMemory()
            );
        }

        // Is.Not.AllocatingGCMemory() is zero-or-fail and expresses no budget, so the
        // partial-JSON test — which must permit the one string it returns — reads the same
        // recorder that constraint is itself built on (AllocatingGCMemoryConstraint uses
        // Recorder.Get("GC.Alloc") and sampleBlockCount) and asserts on the COUNT instead.
        // A count is the stronger guard of the two available: the 20 KB byte budget this
        // test used to carry would have admitted an extra Substring per call (~78 B × 100
        // = ~7.8 KB) even on a runtime where the byte counter worked.
        static int GCAllocationsDuring(Action action)
        {
            var recorder = Recorder.Get("GC.Alloc");

            // Disabling first flushes the samples Recorder.Get itself produced, so they are
            // not counted against the delegate. Unity's constraint does the same, for the
            // same reason. The delegate is allocated at the call site, outside this region.
            recorder.enabled = false;
            recorder.FilterToCurrentThread();
            recorder.enabled = true;
            try
            {
                action();
            }
            finally
            {
                recorder.enabled = false;
                recorder.CollectFromAllThreads();
            }

            return recorder.sampleBlockCount;
        }
    }
}
