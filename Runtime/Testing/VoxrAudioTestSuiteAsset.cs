// ============================================================================
// Purpose:  ScriptableObject wrapping a list of audio test cases with JSON import/export
// Layer:    Runtime.Testing
// Owns:     VoxrAudioTestSuiteAsset (public ScriptableObject)
// Depends:  VoxrAudioTestCase
// ============================================================================
using System;
using System.Collections.Generic;
using UnityEngine;

namespace VoXR.Testing
{
    [CreateAssetMenu(menuName = "VoXR/Audio Test Suite")]
    public class VoxrAudioTestSuiteAsset : ScriptableObject
    {
        [Tooltip("Human-readable name for this audio test suite.")]
        public string suiteName;

        public List<VoxrAudioTestCase> cases = new List<VoxrAudioTestCase>();

        public VoxrAudioTestCase[] ToArray() => cases.ToArray();

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
            public VoxrAudioTestCase[] cases;
        }
    }
}
