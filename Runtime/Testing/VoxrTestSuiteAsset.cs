// ============================================================================
// Purpose:  ScriptableObject wrapping a list of test cases with JSON import/export
// Layer:    Runtime.Testing
// Owns:     VoxrTestSuiteAsset (public ScriptableObject)
// Depends:  VoxrTestCase
// ============================================================================
using System;
using System.Collections.Generic;
using UnityEngine;

namespace VoXR.Testing
{
    [CreateAssetMenu(menuName = "VoXR/Test Suite")]
    public class VoxrTestSuiteAsset : ScriptableObject
    {
        [Tooltip("Human-readable name for this test suite.")]
        public string suiteName;

        public List<VoxrTestCase> cases = new List<VoxrTestCase>();

        public VoxrTestCase[] ToArray() => cases.ToArray();

        public string ToJson()
        {
            var wrapper = new JsonWrapper { cases = cases.ToArray() };
            return JsonUtility.ToJson(wrapper, true);
        }

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
            public VoxrTestCase[] cases;
        }
    }
}
