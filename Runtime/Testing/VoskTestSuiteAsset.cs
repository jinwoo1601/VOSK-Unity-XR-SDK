using System;
using System.Collections.Generic;
using UnityEngine;

namespace VoskXR.Testing
{
    /// <summary>
    /// ScriptableObject wrapping a list of <see cref="VoskTestCase"/> entries.
    /// Create via Assets > Create > VOSK XR > Test Suite.
    /// Supports JSON import/export for portability and version control.
    /// </summary>
    [CreateAssetMenu(menuName = "VOSK XR/Test Suite")]
    public class VoskTestSuiteAsset : ScriptableObject
    {
        [Tooltip("Human-readable name for this test suite.")]
        public string suiteName;

        public List<VoskTestCase> cases = new List<VoskTestCase>();

        /// <summary>
        /// Returns the test cases as an array for <see cref="VoskBatchTestRunner.RunAll"/>.
        /// </summary>
        public VoskTestCase[] ToArray() => cases.ToArray();

        /// <summary>
        /// Serialises the test cases to a JSON string.
        /// </summary>
        public string ToJson()
        {
            var wrapper = new JsonWrapper { cases = cases.ToArray() };
            return JsonUtility.ToJson(wrapper, true);
        }

        /// <summary>
        /// Replaces the current test cases with those parsed from a JSON string.
        /// </summary>
        public void FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("JSON string is null or empty.", nameof(json));

            var wrapper = JsonUtility.FromJson<JsonWrapper>(json);
            cases.Clear();
            if (wrapper.cases != null)
                cases.AddRange(wrapper.cases);
        }

        [Serializable]
        struct JsonWrapper
        {
            public VoskTestCase[] cases;
        }
    }
}
