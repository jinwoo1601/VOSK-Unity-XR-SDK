// ============================================================================
// Purpose:  Marks the session debug log inactive while the Test Runner drives a run
// Layer:    Editor.TestRunnerHook (compiled only when com.unity.test-framework is installed)
// Owns:     VoxrSessionLogTestRunnerHook (internal static class), Callbacks (private nested class)
// Depends:  VoxrDebugSessionLog
// ============================================================================
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace VoXR.Editor
{
    /// <summary>
    /// In-editor Test Runner runs enter Play Mode without being batch mode, so they would
    /// otherwise export a session log per run and evict real playtests from the retention
    /// pool. This flags the collector for the duration of a run.
    /// </summary>
    /// <remarks>
    /// Lives in its own assembly, constrained to <c>VOXR_TEST_FRAMEWORK</c>, which the
    /// asmdef defines only when com.unity.test-framework is installed. When the package is
    /// absent the assembly is not compiled at all, so its UnityEditor.TestRunner reference
    /// never has to resolve and consumers take no dependency on the test framework.
    /// </remarks>
    [InitializeOnLoad]
    internal static class VoxrSessionLogTestRunnerHook
    {
        static VoxrSessionLogTestRunnerHook()
        {
            // Callback registration is per-domain, so this re-runs after every reload —
            // including the one that entering Play Mode for PlayMode tests triggers.
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.RegisterCallbacks(new Callbacks());
        }

        class Callbacks : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun) =>
                VoxrDebugSessionLog.TestRunActive = true;

            public void RunFinished(ITestResultAdaptor result) =>
                VoxrDebugSessionLog.TestRunActive = false;

            public void TestStarted(ITestAdaptor test) { }

            public void TestFinished(ITestResultAdaptor result) { }
        }
    }
}
