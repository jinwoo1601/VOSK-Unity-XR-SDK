// ============================================================================
// Purpose:  EditMode verification of the committed fixture corpus (format, peaks, coverage)
// Layer:    Tests.Editor
// Owns:     VoxrFixtureCorpusTests (public class)
// Depends:  VoxrWavReader, VoxrAudioTestSuiteAsset, VoxrAudioTestCase
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using VoXR;
using VoXR.Testing;

namespace VoXR.Tests.Editor
{
    public class VoxrFixtureCorpusTests
    {
        // The corpus categories the locked design requires (§6.2).
        static readonly string[] RequiredCategories =
        {
            "clean",
            "slot-variant",
            "homophone",
            "filler",
            "split",
            "silence",
        };

        const float UtterancePeakMin = 0.04f; // measured Quest 3 mic range
        const float UtterancePeakMax = 0.4f;
        const float SilencePeakMax = 0.005f;

        static string FixtureRoot
        {
            get
            {
                var pkg = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                    typeof(VoxrSpeechRecogniser).Assembly
                );
                Assert.IsNotNull(pkg, "PackageInfo not found for the VoXR runtime assembly");
                return Path.Combine(pkg.resolvedPath, "Tests", "Fixtures");
            }
        }

        static VoxrAudioTestCase[] LoadManifest()
        {
            string path = Path.Combine(FixtureRoot, "audio", "manifest.json");
            Assert.IsTrue(File.Exists(path), $"manifest not found: {path}");

            var suite = ScriptableObject.CreateInstance<VoxrAudioTestSuiteAsset>();
            try
            {
                suite.FromJson(File.ReadAllText(path));
                return suite.ToArray();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(suite);
            }
        }

        static float Peak(float[] samples)
        {
            float peak = 0f;
            foreach (float s in samples)
            {
                float a = Math.Abs(s);
                if (a > peak)
                    peak = a;
            }
            return peak;
        }

        [Test]
        public void Manifest_ParsesAndIsNonEmpty()
        {
            var cases = LoadManifest();
            Assert.IsNotEmpty(cases);
            foreach (var c in cases)
                Assert.IsFalse(string.IsNullOrEmpty(c.file), "every case must name a fixture file");
        }

        [Test]
        public void Manifest_CoversEveryRequiredCategory()
        {
            var categories = new HashSet<string>(LoadManifest().Select(c => c.category));
            foreach (string required in RequiredCategories)
                Assert.Contains(
                    required,
                    categories.ToList(),
                    $"design §6.2 requires a '{required}' fixture"
                );
        }

        [Test]
        public void EveryFixture_Exists_Parses_AndPeaksInItsRange()
        {
            foreach (var c in LoadManifest())
            {
                string path = Path.Combine(
                    FixtureRoot,
                    c.file.Replace('/', Path.DirectorySeparatorChar)
                );
                Assert.IsTrue(File.Exists(path), $"{c.file}: fixture missing — run generate.sh");

                // Read() enforces 48 kHz mono 16-bit; any format defect throws here.
                float[] samples = VoxrWavReader.ReadFile(path);
                float peak = Peak(samples);

                if (c.category == "silence")
                    Assert.LessOrEqual(
                        peak,
                        SilencePeakMax,
                        $"{c.file}: silence fixture must stay below {SilencePeakMax}"
                    );
                else
                {
                    Assert.GreaterOrEqual(
                        peak,
                        UtterancePeakMin,
                        $"{c.file}: peak {peak:F3} below the Quest mic range"
                    );
                    Assert.LessOrEqual(
                        peak,
                        UtterancePeakMax,
                        $"{c.file}: peak {peak:F3} above the Quest mic range"
                    );
                }
            }
        }

        [Test]
        public void EveryCommittedWav_IsListedInTheManifest()
        {
            var listed = new HashSet<string>(LoadManifest().Select(c => Path.GetFileName(c.file)));

            string ttsDir = Path.Combine(FixtureRoot, "audio", "tts");
            foreach (string wav in Directory.GetFiles(ttsDir, "*.wav"))
                Assert.IsTrue(
                    listed.Contains(Path.GetFileName(wav)),
                    $"{Path.GetFileName(wav)}: committed WAV has no manifest entry"
                );
        }

        [Test]
        public void ExpectationShape_IsConsistent()
        {
            foreach (var c in LoadManifest())
            {
                if (c.category == "silence")
                    Assert.IsTrue(
                        string.IsNullOrEmpty(c.expectedIntent),
                        $"{c.file}: silence fixtures must expect no command"
                    );
                else if (c.category != "homophone")
                    // Homophone traps may legitimately be negative baselines (a trap
                    // the recognizer currently loses); every other utterance category
                    // must state its expected intent.
                    Assert.IsFalse(
                        string.IsNullOrEmpty(c.expectedIntent),
                        $"{c.file}: utterance fixtures must state an expected intent"
                    );
            }
        }
    }
}
