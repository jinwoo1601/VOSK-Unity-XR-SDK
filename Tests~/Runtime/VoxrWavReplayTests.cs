// ============================================================================
// Purpose:  PlayMode acoustic regression tests replaying WAV fixtures end-to-end
// Layer:    Tests.Runtime
// Owns:     VoxrWavReplayTests (public class)
// Depends:  VoxrSpeechRecogniser, VoxrCommandRecogniser, DemoGrammar, VoxrWavReader, EditorMicBackend
// ============================================================================
#if UNITY_EDITOR_WIN
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VoXR.Commands;
using VoXR.Testing;

namespace VoXR.Tests.Runtime
{
    public class VoxrWavReplayTests
    {
        GameObject _go;
        VoxrSpeechRecogniser _speech;
        VoxrCommandRecogniser _command;
        List<string> _errors;

        // Fixture root inside the package working tree. During host-project test
        // runs Tests~ is renamed to Tests (verification bindings), so the folder
        // resolves as <package>/Tests/Fixtures.
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

        static string FixturePath(string relative) =>
            Path.Combine(FixtureRoot, relative.Replace('/', Path.DirectorySeparatorChar));

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _go = new GameObject("WavReplayTest");
            _speech = _go.AddComponent<VoxrSpeechRecogniser>();
            _command = _go.AddComponent<VoxrCommandRecogniser>();
            _command.SpeechRecogniser = _speech;
            // Re-run OnEnable so the production subscription path connects with
            // the now-set reference (same pattern as the injection tests).
            _command.enabled = false;
            _command.enabled = true;

            _errors = new List<string>();
            _speech.OnError += (code, msg) => _errors.Add($"{code}: {msg}");

            var init = _speech.InitialiseAsync();
            while (!init.IsCompleted)
                yield return null;
            Assert.IsTrue(
                _speech.IsInitialised,
                "Recogniser failed to initialise — is the VOSK model available to the "
                    + $"host project? Errors: [{string.Join("; ", _errors)}]"
            );

            DemoGrammar.Configure(_command);
            Assert.IsEmpty(_errors, $"Errors during grammar setup: [{string.Join("; ", _errors)}]");

            _command.BufferWindow = 1.5f;
            _command.CommandCooldown = 0f;
            _command.EagerFlushOnCompleteMatch = true;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _speech.EditorBackend?.StopPlayback();
            Object.Destroy(_go);
            yield return null;
        }

        // Replays a fixture through the full pipeline and pumps until a command
        // fires or the post-playback grace window expires.
        IEnumerator Replay(
            float[] samples,
            List<string> finals,
            List<string> unrecognised,
            System.Func<bool> done,
            float graceSeconds = 5f
        )
        {
            var backend = _speech.EditorBackend;
            Assert.IsTrue(
                backend.StartPlayback(samples, 48000, (c, m) => _errors.Add(m)),
                $"StartPlayback refused: [{string.Join("; ", _errors)}]"
            );

            int guard = 0;
            while (backend.TickPlayback(_speech.EditorDispatcher))
            {
                Assert.Less(++guard, 10000, "playback did not complete");
                yield return null;
            }

            float deadline = Time.realtimeSinceStartup + graceSeconds;
            while (!done() && Time.realtimeSinceStartup < deadline)
                yield return null;
        }

        static VoxrAudioTestCase[] ManifestCases()
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
                Object.DestroyImmediate(suite);
            }
        }

        static IEnumerable<string> ManifestFileNames()
        {
            foreach (var c in ManifestCases())
                yield return c.file;
        }

        // The corpus-wide acoustic regression suite (F5/F8): one test per manifest
        // entry, expectations asserted against the full pipeline's output.
        [UnityTest]
        public IEnumerator Replay_ManifestCase([ValueSource(nameof(ManifestFileNames))] string file)
        {
            var testCase = System.Array.Find(ManifestCases(), c => c.file == file);
            Assert.IsNotNull(testCase, $"manifest entry vanished for {file}");

            float[] samples = VoxrWavReader.ReadFile(FixturePath(testCase.file));

            var finals = new List<string>();
            var unrecognised = new List<string>();
            var commands = new List<VoxrCommand>();
            _speech.OnFinalResult += t => finals.Add(t);
            _command.OnUnrecognisedSpeech += t => unrecognised.Add(t);
            _command.OnCommandRecognised += cmd => commands.Add(cmd);

            bool expectsCommand = !testCase.ExpectsNoCommand;
            yield return Replay(
                samples,
                finals,
                unrecognised,
                () => expectsCommand && commands.Count > 0,
                graceSeconds: expectsCommand ? 5f : 2f
            );

            string context =
                $"{testCase.file} ({testCase.description}) — "
                + $"finals: [{string.Join(" | ", finals)}]; "
                + $"unrecognised: [{string.Join(" | ", unrecognised)}]; "
                + $"commands: [{string.Join(" | ", commands)}]; "
                + $"errors: [{string.Join("; ", _errors)}]";

            if (!expectsCommand)
            {
                Assert.IsEmpty(commands, $"expected no command; {context}");
                // Only silence is transcript-free; a negative homophone trap
                // still produces a (mis-heard) transcript that matches nothing.
                if (testCase.category == "silence")
                    Assert.IsTrue(
                        finals.TrueForAll(string.IsNullOrEmpty),
                        $"silence must not produce transcript text; {context}"
                    );
            }
            else
            {
                Assert.IsNotEmpty(commands, $"no command recognised; {context}");
                var cmd = commands[0];
                Assert.AreEqual(testCase.expectedIntent, cmd.Intent, context);
                if (testCase.expectedSlots != null)
                {
                    foreach (var slot in testCase.expectedSlots)
                        Assert.AreEqual(
                            slot.value,
                            cmd.GetSlot(slot.name),
                            $"slot '{slot.name}'; {context}"
                        );
                }
                if (!string.IsNullOrEmpty(testCase.expectedTranscript))
                    Assert.Contains(
                        testCase.expectedTranscript,
                        finals,
                        $"expected transcript missing; {context}"
                    );
            }
        }

        // Phase 3a acoustic go/no-go probe (architecture.md build plan): one clean
        // command through TTS fixture → AGC → real Vosk → grammar → command layer.
        [UnityTest]
        public IEnumerator Probe_CleanLaunchCommand_RecognisedEndToEnd()
        {
            float[] samples = VoxrWavReader.ReadFile(
                FixturePath("audio/tts/launch_one_missiles_target_hotel_one.wav")
            );

            var finals = new List<string>();
            var unrecognised = new List<string>();
            VoxrCommand? received = null;
            _speech.OnFinalResult += t => finals.Add(t);
            _command.OnUnrecognisedSpeech += t => unrecognised.Add(t);
            _command.OnCommandRecognised += cmd =>
            {
                if (received == null)
                    received = cmd;
            };

            yield return Replay(samples, finals, unrecognised, () => received.HasValue);

            Assert.IsTrue(
                received.HasValue,
                "No command recognised from the probe fixture. "
                    + $"Finals: [{string.Join(" | ", finals)}]; "
                    + $"unrecognised: [{string.Join(" | ", unrecognised)}]; "
                    + $"errors: [{string.Join("; ", _errors)}]"
            );
            Assert.AreEqual("launch_weapon", received.Value.Intent);
            Assert.AreEqual("one", received.Value.GetSlot("quantity"));
            Assert.AreEqual("missiles", received.Value.GetSlot("weapon"));
            Assert.AreEqual("hotel one", received.Value.GetSlot("target"));
        }
    }
}
#endif
