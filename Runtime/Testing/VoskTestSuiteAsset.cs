// ============================================================================
// Purpose:  ScriptableObject wrapping a list of test cases with JSON import/export
// Layer:    Runtime.Testing
// Owns:     VoskTestSuiteAsset (public ScriptableObject)
// Depends:  VoskTestCase
// ============================================================================
using System;
using System.Collections.Generic;
using UnityEngine;

namespace VoskXR.Testing
{
    [CreateAssetMenu(menuName = "VOSK XR/Test Suite")]
    public class VoskTestSuiteAsset : ScriptableObject
    {
        [Tooltip("Human-readable name for this test suite.")]
        public string suiteName;

        public List<VoskTestCase> cases = new List<VoskTestCase>();

        public VoskTestCase[] ToArray() => cases.ToArray();

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
            public VoskTestCase[] cases;
        }
    }
}
