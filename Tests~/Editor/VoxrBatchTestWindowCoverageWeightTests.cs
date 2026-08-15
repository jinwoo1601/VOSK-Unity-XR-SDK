// ============================================================================
// Purpose:  EditMode tests that the batch test window forwards coverageWeight to its runner
// Layer:    Tests.Editor
// Owns:     VoxrBatchTestWindowCoverageWeightTests (public class)
// Depends:  VoxrBatchTestWindow, VoxrBatchTestRunner, VoxrCommandParser, VoxrCommandSetAsset
// ============================================================================
using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using VoXR.Commands;
using VoXR.Editor;
using VoXR.Testing;

namespace VoXR.Tests.Editor
{
    // Issue #80: the window mirrors minScore and minConfidence as serialized fields and hands
    // them to VoxrBatchTestRunner, but coverageWeight had no mirror — so the window's parser ran
    // at the default 1.0 however the project's VoxrCommandRecogniser was tuned, and a mismatched
    // weight does not merely move the reported score. Run accepts the first candidate clearing
    // the thresholds, so a weight that pushes one candidate under minScore promotes the next,
    // and the batch can name an intent the runtime does not fire.
    //
    // The witness is behavioural rather than a field read: "cease fire please now" leaves two
    // trailing tokens no active pattern could begin a match at, so the match is charged for both
    // — 2 / (2 + 2 x coverageWeight). At the default 1.0 that is 0.50 and the default minScore
    // of 0.60 refuses the command; at 0 it is 1.00 and the command fires. Both numbers were
    // measured against the real parser, not derived by hand.
    //
    // Both of CreateRunner's constructor calls are covered — the window picks between them on
    // whether activeSetNames is populated, and each had to be fixed separately.
    //
    // The window's fields and CreateRunner are private, which is the seam this reaches through:
    // testing the forwarding is not a reason to widen the window's own surface.
    public class VoxrBatchTestWindowCoverageWeightTests
    {
        const string TrailingFillerUtterance = "cease fire please now";

        VoxrBatchTestWindow _window;
        VoxrCommandAsset _commandAsset;
        VoxrCommandSetAsset _setAsset;

        [SetUp]
        public void SetUp()
        {
            _commandAsset = ScriptableObject.CreateInstance<VoxrCommandAsset>();
            _commandAsset.intent = "cease_fire";
            _commandAsset.patterns = new[] { "cease fire" };

            _setAsset = ScriptableObject.CreateInstance<VoxrCommandSetAsset>();
            _setAsset.setName = "weapons";
            _setAsset.commands = new[] { _commandAsset };

            _window = ScriptableObject.CreateInstance<VoxrBatchTestWindow>();
            SetField("slotAssets", Array.Empty<VoxrSlotAsset>());
            SetField("commandSetAssets", new[] { _setAsset });
        }

        [TearDown]
        public void TearDown()
        {
            if (_window != null)
                UnityEngine.Object.DestroyImmediate(_window);
            if (_setAsset != null)
                UnityEngine.Object.DestroyImmediate(_setAsset);
            if (_commandAsset != null)
                UnityEngine.Object.DestroyImmediate(_commandAsset);
        }

        static FieldInfo WindowField(string name)
        {
            var field = typeof(VoxrBatchTestWindow).GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic
            );

            Assert.IsNotNull(field, $"the window must carry a serialized '{name}' field");
            return field;
        }

        void SetField(string name, object value) => WindowField(name).SetValue(_window, value);

        // Selects which constructor CreateRunner reaches: an explicit active-set filter, or the
        // all-sets fallback it builds when none is set.
        void UseActiveSetNames(bool explicitFilter) =>
            SetField(
                "activeSetNames",
                explicitFilter ? new[] { _setAsset.setName } : Array.Empty<string>()
            );

        VoxrBatchTestRunner CreateRunner()
        {
            var method = typeof(VoxrBatchTestWindow).GetMethod(
                "CreateRunner",
                BindingFlags.Instance | BindingFlags.NonPublic
            );

            Assert.IsNotNull(method, "the window must build its runner in CreateRunner");

            var runner = (VoxrBatchTestRunner)method.Invoke(_window, null);
            Assert.IsNotNull(runner, "the window failed to build a runner from the test fixture");
            return runner;
        }

        static VoxrTestCase TrailingFillerCase() =>
            new VoxrTestCase { input = TrailingFillerUtterance, expectedIntent = "cease_fire" };

        [TestCase(true)]
        [TestCase(false)]
        public void CreateRunner_ForwardsATunedCoverageWeight(bool explicitFilter)
        {
            UseActiveSetNames(explicitFilter);
            SetField("coverageWeight", 0f);

            var result = CreateRunner().Run(TrailingFillerCase());

            Assert.AreEqual(
                "cease_fire",
                result.ActualIntent,
                "at coverageWeight 0 the trailing filler costs nothing, so the command fires"
            );
            Assert.AreEqual(1.0f, result.Score, 0.005f);
            Assert.IsTrue(result.Passed, result.FailureReason);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void CreateRunner_UntunedWindow_RunsAtTheParserDefault(bool explicitFilter)
        {
            UseActiveSetNames(explicitFilter);

            var result = CreateRunner().Run(TrailingFillerCase());

            Assert.IsNull(
                result.ActualIntent,
                "at the default weight the two trailing tokens hold the score under minScore"
            );
            Assert.AreEqual(0.5f, result.Score, 0.005f);
        }

        [Test]
        public void CoverageWeight_DefaultsToTheParserDefault()
        {
            Assert.AreEqual(
                VoxrCommandParser.DefaultCoverageWeight,
                (float)WindowField("coverageWeight").GetValue(_window),
                0.0001f
            );
        }
    }
}
