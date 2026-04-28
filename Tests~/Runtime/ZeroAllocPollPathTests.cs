using System;
using System.Text;
using NUnit.Framework;
using VoXR;

namespace VoXR.Tests.Runtime
{
    // Regression guard for the zero-alloc parsing hot path. The poll loop should
    // allocate only the leaf string returned to consumers — anything else
    // (Substring, boxing, freshly allocated key arrays) is a regression.
    public class ZeroAllocPollPathTests
    {
        // Sized for the fixed test input below ("hello world" = 11 chars). On Mono
        // a managed string costs ~56 B header + 2*length bytes ≈ 78 B → ~7.8 KB / 100
        // calls. 20 KB budget gives headroom for slightly longer inputs without
        // becoming flaky from per-test JIT or static-init noise.
        const long PartialBudget = 20_000;

        [Test]
        public void ParsingPartialJson_AllocatesOnlyTheReturnedString()
        {
            byte[] json = Encoding.UTF8.GetBytes("{\"partial\":\"hello world\"}");

            // Warm up — first call may JIT/touch statics.
            VoxrJsonParser.ParseTextFromJson(json, isFinal: false);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
                VoxrJsonParser.ParseTextFromJson(json, isFinal: false);
            long delta = GC.GetAllocatedBytesForCurrentThread() - before;

            // Each call returns one managed string (the partial text). Anything beyond
            // that is a regression — e.g. an inadvertent Substring or boxed value type.
            Assert.Less(delta, PartialBudget,
                $"Allocated {delta} B over 100 calls; expected < {PartialBudget} B.");
        }

        [Test]
        public void ParsingErrorCode_AllocatesNothing()
        {
            byte[] json = Encoding.UTF8.GetBytes("{\"error\":\"x\",\"code\":3}");

            VoxrJsonParser.ParseErrorCode(json);  // warm up

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
                VoxrJsonParser.ParseErrorCode(json);
            long delta = GC.GetAllocatedBytesForCurrentThread() - before;

            // Returns an enum (value type), no allocations expected at all.
            Assert.AreEqual(0, delta, $"Allocated {delta} B; expected exactly 0.");
        }
    }
}
