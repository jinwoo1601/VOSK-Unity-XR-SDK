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
    //
    // TheAllocationInstrumentsSeeADeliberateAllocation is the positive control against a
    // repeat of that. Read it before trusting a green run of the other two.
    public class ZeroAllocPollPathTests
    {
        const int Iterations = 100;

        // Not private: a private field only ever assigned is a CS0414 warning. Written by
        // the positive control so its allocations cannot be optimised away as dead.
        internal static byte[] Sink;

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
            //
            // Deliberately an equality, not a ceiling: a count BELOW one-per-call would
            // mean the returned string had stopped being freshly allocated, which is also
            // a change worth failing on.
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
            // zero-or-fail constraint expresses it exactly. Looped rather than called once
            // so that an allocation appearing only on a repeat call — a lazy init that
            // re-fires, a cache that rebuilds — is caught too.
            Assert.That(
                () =>
                {
                    for (int i = 0; i < Iterations; i++)
                        VoxrJsonParser.ParseErrorCode(json);
                },
                Is.Not.AllocatingGCMemory()
            );
        }

        // The failure mode this file exists to prevent is not a parser regression — it is an
        // instrument that silently reads zero. That is what GC.GetAllocatedBytesForCurrentThread
        // did here while both tests above "passed" for four months, and neither test can detect
        // it alone: a zero-or-fail constraint passes when the recorder is dead, and an
        // expected-zero byte delta passes when the counter is dead. So assert the converse —
        // both instruments must SEE a deliberate allocation. If this test goes red, the other
        // two in this file are not measuring anything and their greens mean nothing.
        [Test]
        public void TheAllocationInstrumentsSeeADeliberateAllocation()
        {
            int observed = GCAllocationsDuring(() =>
            {
                Sink = new byte[64];
            });

            Assert.Greater(
                observed,
                0,
                "GCAllocationsDuring saw 0 allocations for a deliberate 64 B array. The GC.Alloc "
                    + "recorder is not measuring on this runtime, so "
                    + "ParsingPartialJson_AllocatesOnlyTheReturnedString proves nothing."
            );

            Assert.That(
                () =>
                {
                    Sink = new byte[64];
                },
                Is.AllocatingGCMemory(),
                "Unity's AllocatingGCMemory constraint did not observe a deliberate 64 B "
                    + "allocation, so ParsingErrorCode_AllocatesNothing proves nothing."
            );
        }

        // Is.Not.AllocatingGCMemory() is zero-or-fail and expresses no budget, so the
        // partial-JSON test — which must permit the one string it returns — reads the same
        // recorder that constraint is itself built on and asserts on the COUNT instead.
        // sampleBlockCount is a count of allocations, which is Unity's own documented
        // reading of it: AllocatingGCMemoryConstraint reports "The provided delegate made
        // {0} GC allocation(s)" from this very number, and only collapses it to a boolean
        // for its own pass/fail.
        //
        // A count is also the stronger guard of the two available: the 20 KB byte budget
        // this test used to carry would have admitted an extra Substring per call
        // (~78 B x 100 = ~7.8 KB) even on a runtime where the byte counter worked.
        static int GCAllocationsDuring(Action action)
        {
            var recorder = Recorder.Get("GC.Alloc");

            // Disabling first flushes the samples Recorder.Get itself produced, so they are
            // not counted against the delegate. The whole sequence mirrors
            // AllocatingGCMemoryConstraint.ApplyTo, WebGL guards included, so that the count
            // read here and the constraint's own verdict can never disagree about what was
            // measured. The delegate is allocated at the call site, outside this region.
            recorder.enabled = false;
#if !UNITY_WEBGL
            recorder.FilterToCurrentThread();
#endif
            recorder.enabled = true;
            try
            {
                action();
            }
            finally
            {
                recorder.enabled = false;
#if !UNITY_WEBGL
                recorder.CollectFromAllThreads();
#endif
            }

            return recorder.sampleBlockCount;
        }
    }
}
