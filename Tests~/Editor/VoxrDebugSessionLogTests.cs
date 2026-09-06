using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using VoXR;
using VoXR.Commands;
using VoXR.Editor;

namespace VoXR.Tests.Editor
{
    public class VoxrDebugSessionLogTests
    {
        static VoxrMatchDiagnostics MakeDiagnostics(
            VoxrWord[] words,
            VoxrMatchAttempt[] attempts,
            string inputText = "launch missiles"
        ) => new VoxrMatchDiagnostics(inputText, words, attempts, 42);

        [Test]
        public void BuildEntry_CopiesWordsAttemptsAndSlots()
        {
            var words = new[]
            {
                new VoxrWord("launch", 0.9f, 0.0f, 0.3f),
                new VoxrWord("missiles", 0.8f, 0.3f, 0.7f),
            };
            var slots = new[] { new VoxrDiagnosticSlotMatch("weapon", "missiles", 1, 1, 0.8f) };
            var attempts = new[]
            {
                new VoxrMatchAttempt(
                    "launch_weapon",
                    "launch {weapon}",
                    0.95f,
                    0.6f,
                    0.85f,
                    0.4f,
                    slots,
                    null,
                    true
                ),
            };

            var entry = VoxrDebugSessionLog.BuildEntry(null, MakeDiagnostics(words, attempts));

            Assert.AreEqual("launch missiles", entry.inputText);
            Assert.AreEqual(42, entry.frame);

            Assert.AreEqual(2, entry.words.Length);
            Assert.AreEqual("missiles", entry.words[1].text);
            Assert.AreEqual(0.8f, entry.words[1].confidence);
            Assert.AreEqual(0.3f, entry.words[1].startTime);
            Assert.AreEqual(0.7f, entry.words[1].endTime);

            Assert.AreEqual(1, entry.attempts.Length);
            var a = entry.attempts[0];
            Assert.AreEqual("launch_weapon", a.intent);
            Assert.AreEqual("launch {weapon}", a.pattern);
            Assert.AreEqual(0.95f, a.score);
            Assert.AreEqual(0.6f, a.minScore);
            Assert.AreEqual(0.85f, a.aggregateConfidence);
            Assert.AreEqual(0.4f, a.minConfidence);
            Assert.IsTrue(a.accepted);

            Assert.AreEqual(1, a.slots.Length);
            Assert.AreEqual("weapon", a.slots[0].name);
            Assert.AreEqual("missiles", a.slots[0].value);
            Assert.AreEqual(1, a.slots[0].startWord);
            Assert.AreEqual(1, a.slots[0].endWord);
            Assert.AreEqual(0.8f, a.slots[0].confidence);
        }

        [Test]
        public void BuildEntry_NullStringsBecomeEmpty()
        {
            var attempts = new[]
            {
                new VoxrMatchAttempt(null, null, 0f, 0.6f, 0f, 0.4f, null, "no match", false),
            };

            var entry = VoxrDebugSessionLog.BuildEntry(
                null,
                MakeDiagnostics(Array.Empty<VoxrWord>(), attempts, null)
            );

            Assert.AreEqual("", entry.inputText);
            Assert.AreEqual("", entry.attempts[0].intent);
            Assert.AreEqual("", entry.attempts[0].pattern);
            Assert.AreEqual("no match", entry.attempts[0].rejectReason);
            Assert.AreEqual("", entry.attempts[0].tiedRival);
            Assert.IsFalse(entry.attempts[0].tiedRivalIsSibling);
            Assert.IsFalse(entry.attempts[0].accepted);
            Assert.AreEqual(0, entry.attempts[0].slots.Length);
            Assert.AreEqual(0, entry.words.Length);
        }

        /// <summary>
        /// A registration-order coin flip and a clean win are identical in every other field,
        /// so the export has to carry the rival — and which kind of tie it was — or whole-session
        /// analysis cannot tell them apart. JsonUtility only serialises public fields, hence the
        /// assertions on the serialised JSON rather than DTO reads alone.
        /// </summary>
        [Test]
        public void BuildEntry_RecordsTiedRivalAndWhetherItWasASibling()
        {
            var attempts = new[]
            {
                new VoxrMatchAttempt(
                    "set_mode",
                    "weapons mode",
                    1f,
                    0.6f,
                    0.9f,
                    0.4f,
                    null,
                    null,
                    true,
                    "set_nav_mode (pattern 0)",
                    true
                ),
                new VoxrMatchAttempt(
                    "raise_shields",
                    "shields up",
                    1f,
                    0.6f,
                    0.9f,
                    0.4f,
                    null,
                    null,
                    true,
                    "activate_defence (pattern 1)",
                    false
                ),
                new VoxrMatchAttempt(
                    "cease_fire",
                    "cease fire",
                    1f,
                    0.6f,
                    0.9f,
                    0.4f,
                    null,
                    null,
                    true
                ),
            };

            var entry = VoxrDebugSessionLog.BuildEntry(
                null,
                MakeDiagnostics(Array.Empty<VoxrWord>(), attempts)
            );

            Assert.AreEqual("set_nav_mode (pattern 0)", entry.attempts[0].tiedRival);
            Assert.IsTrue(entry.attempts[0].tiedRivalIsSibling);
            Assert.AreEqual("activate_defence (pattern 1)", entry.attempts[1].tiedRival);
            Assert.IsFalse(entry.attempts[1].tiedRivalIsSibling);
            Assert.AreEqual("", entry.attempts[2].tiedRival);
            Assert.IsFalse(entry.attempts[2].tiedRivalIsSibling);

            string json = JsonUtility.ToJson(entry);

            StringAssert.Contains("\"tiedRival\":\"set_nav_mode (pattern 0)\"", json);
            StringAssert.Contains("\"tiedRivalIsSibling\":true", json);
            StringAssert.Contains("\"tiedRival\":\"activate_defence (pattern 1)\"", json);
            StringAssert.Contains("\"tiedRival\":\"\"", json);
            StringAssert.Contains("\"tiedRivalIsSibling\":false", json);
        }

        [Test]
        public void BuildEntry_NullSenderYieldsEmptyActiveSets()
        {
            var entry = VoxrDebugSessionLog.BuildEntry(
                null,
                MakeDiagnostics(Array.Empty<VoxrWord>(), Array.Empty<VoxrMatchAttempt>())
            );

            Assert.IsNotNull(entry.activeSets);
            Assert.AreEqual(0, entry.activeSets.Length);
        }

        [Test]
        public void BuildEntry_RecordsSenderActiveSets()
        {
            var go = new GameObject("SessionLogTestRecogniser");
            try
            {
                var recogniser = go.AddComponent<VoxrCommandRecogniser>();
                recogniser.Configure(
                    Array.Empty<VoxrSlotDefinition>(),
                    new[]
                    {
                        new VoxrCommandSet(
                            "combat",
                            new[]
                            {
                                new VoxrCommandDefinition(
                                    "cease_fire",
                                    new[] { new[] { "cease", "fire" } }
                                ),
                            }
                        ),
                    }
                );
                recogniser.SetActiveSets("combat");

                var entry = VoxrDebugSessionLog.BuildEntry(
                    recogniser,
                    MakeDiagnostics(Array.Empty<VoxrWord>(), Array.Empty<VoxrMatchAttempt>())
                );

                CollectionAssert.AreEqual(new[] { "combat" }, entry.activeSets);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void BuildEntry_SerialisesToJson()
        {
            var attempts = new[]
            {
                new VoxrMatchAttempt(
                    "cease_fire",
                    "cease fire",
                    1f,
                    0.6f,
                    0.9f,
                    0.4f,
                    null,
                    null,
                    true
                ),
            };
            var entry = VoxrDebugSessionLog.BuildEntry(
                null,
                MakeDiagnostics(Array.Empty<VoxrWord>(), attempts, "cease fire")
            );

            string json = JsonUtility.ToJson(entry);

            StringAssert.Contains("\"inputText\":\"cease fire\"", json);
            StringAssert.Contains("\"intent\":\"cease_fire\"", json);
            StringAssert.Contains("\"accepted\":true", json);
        }

        /// <summary>
        /// This test is itself running under the Test Runner, so the hook's RunStarted
        /// callback must already have fired. If the hook assembly failed to compile or its
        /// callbacks were never registered, this is the assertion that catches it.
        /// </summary>
        [Test]
        public void TestRunActive_IsSetWhileTheTestRunnerDrivesThisRun()
        {
            Assert.IsTrue(
                VoxrDebugSessionLog.TestRunActive,
                "Test Runner hook did not flag the run — the session log would export "
                    + "on exit from an in-editor test run and evict real playtest logs."
            );
        }

        [Test]
        public void TestRunActive_SurvivesAsSessionState()
        {
            // Round-trips through SessionState rather than a static field, so the flag
            // outlives the domain reload that entering Play Mode triggers.
            bool original = VoxrDebugSessionLog.TestRunActive;
            try
            {
                VoxrDebugSessionLog.TestRunActive = false;
                Assert.IsFalse(VoxrDebugSessionLog.TestRunActive);

                VoxrDebugSessionLog.TestRunActive = true;
                Assert.IsTrue(VoxrDebugSessionLog.TestRunActive);
            }
            finally
            {
                VoxrDebugSessionLog.TestRunActive = original;
            }
        }

        /// <summary>
        /// A barred round and a round's second choice are what issue #144 added to the export,
        /// and an attempt that carries neither is indistinguishable from one whose fields were
        /// dropped on the way into the DTO. JsonUtility serialises public fields only, so the
        /// assertions run against the serialised form as well as the DTO — the same shape
        /// BuildEntry_RecordsTiedRivalAndWhetherItWasASibling uses, and for the same reason.
        /// </summary>
        [Test]
        public void BuildEntry_RecordsBarredAndRunnerUp()
        {
            var attempts = new[]
            {
                // A refused round: no slots, not accepted, and a runner-up behind it.
                new VoxrMatchAttempt(
                    "approach_target",
                    "approach target {target}",
                    0.75f,
                    0.6f,
                    0.9f,
                    0.4f,
                    null,
                    "barred",
                    false,
                    null,
                    false,
                    true,
                    "set_level",
                    0.5f
                ),
                // An ordinary accepted round that had a second choice.
                new VoxrMatchAttempt(
                    "cease_fire",
                    "cease fire",
                    1f,
                    0.6f,
                    0.9f,
                    0.4f,
                    null,
                    null,
                    true,
                    null,
                    false,
                    false,
                    "set_mode",
                    0.75f
                ),
                // The null-runner-up case, which must land as an empty string rather than a
                // JSON null — the same treatment tiedRival gets, so a log reader has one rule.
                new VoxrMatchAttempt("engage", "engage", 1f, 0.6f, 0.9f, 0.4f, null, null, true),
            };

            var entry = VoxrDebugSessionLog.BuildEntry(
                null,
                MakeDiagnostics(Array.Empty<VoxrWord>(), attempts)
            );

            Assert.IsTrue(entry.attempts[0].barred);
            Assert.IsFalse(entry.attempts[0].accepted);
            Assert.AreEqual("barred", entry.attempts[0].rejectReason);
            Assert.AreEqual("set_level", entry.attempts[0].runnerUpIntent);
            Assert.AreEqual(0.5f, entry.attempts[0].runnerUpScore);

            Assert.IsFalse(entry.attempts[1].barred, "an ordinary round is not a barred one");
            Assert.AreEqual("set_mode", entry.attempts[1].runnerUpIntent);
            Assert.AreEqual(0.75f, entry.attempts[1].runnerUpScore);

            Assert.IsFalse(entry.attempts[2].barred);
            Assert.AreEqual("", entry.attempts[2].runnerUpIntent, "null becomes empty, not null");
            Assert.AreEqual(
                -1f,
                entry.attempts[2].runnerUpScore,
                "and -1 is the no-runner-up score"
            );

            string json = JsonUtility.ToJson(entry);

            StringAssert.Contains("\"barred\":true", json);
            StringAssert.Contains("\"barred\":false", json);
            StringAssert.Contains("\"runnerUpIntent\":\"set_level\"", json);
            StringAssert.Contains("\"runnerUpIntent\":\"set_mode\"", json);
            StringAssert.Contains("\"runnerUpIntent\":\"\"", json);

            // Round-tripped rather than string-matched: JsonUtility's float formatting is its
            // own business, and pinning its spelling here would fail for a reason that has
            // nothing to do with whether the score survived the export.
            var roundTripped = JsonUtility.FromJson<VoxrDebugSessionLog.Entry>(json);
            Assert.AreEqual(0.5f, roundTripped.attempts[0].runnerUpScore, 1e-6f);
            Assert.AreEqual(0.75f, roundTripped.attempts[1].runnerUpScore, 1e-6f);
            Assert.AreEqual(-1f, roundTripped.attempts[2].runnerUpScore, 1e-6f);
            Assert.IsTrue(roundTripped.attempts[0].barred);
            Assert.IsFalse(roundTripped.attempts[1].barred);
        }

        /// <summary>
        /// The exported log is read by tooling and by whoever receives a field report, and the
        /// schema version is the only thing telling them which shape they have. Issue #144 added
        /// three attempt fields and changed what a "no match" attempt means, which is a version
        /// bump; nothing pinned the number before, so a silent bump — or a silent failure to
        /// bump — was invisible. Reflected because the constant is private and deliberately
        /// stays that way.
        /// </summary>
        [Test]
        public void SchemaVersion_IsThree()
        {
            var field = typeof(VoxrDebugSessionLog).GetField(
                "SchemaVersion",
                BindingFlags.NonPublic | BindingFlags.Static
            );

            Assert.IsNotNull(
                field,
                "VoxrDebugSessionLog.SchemaVersion is gone or renamed — the exported log's "
                    + "schemaVersion is no longer pinned by anything"
            );
            Assert.AreEqual(
                3,
                (int)field.GetRawConstantValue(),
                "the log's shape changed with issue #144, so consumers need the bump"
            );
        }
    }
}
