using System;
using NUnit.Framework;
using VoskXR;
using VoskXR.Commands;
using VoskXR.Testing;

namespace VoskXR.Tests.Editor
{
    public class VoskBatchTestRunnerTests
    {
        static VoskSlotDefinition[] MakeSlots() => new[]
        {
            new VoskSlotDefinition("weapon", new[] { "missiles", "torpedoes" }),
            new VoskSlotDefinition("target", new[] { "hotel one", "hotel two", "alpha one" }),
            new VoskSlotDefinition("quantity", new[] { "all", "one", "two" }),
        };

        static VoskCommandDefinition[] MakeCommands() => new[]
        {
            new VoskCommandDefinition("launch_weapon", new[]
            {
                new[] { "launch", "{?quantity}", "{weapon}", "target", "{target}" },
            }),
            new VoskCommandDefinition("cease_fire", new[]
            {
                new[] { "cease", "fire" },
            }),
        };

        VoskBatchTestRunner CreateRunner(float minScore = 0.6f, float minConfidence = 0.4f)
        {
            return new VoskBatchTestRunner(MakeSlots(), MakeCommands(), minScore, minConfidence);
        }

        // ─── Basic pass/fail ────────────────────────────────────────────

        [Test]
        public void Run_MatchingCommand_Passes()
        {
            var runner = CreateRunner();
            var result = runner.Run(new VoskTestCase
            {
                input = "cease fire",
                expectedIntent = "cease_fire",
            });

            Assert.IsTrue(result.Passed);
            Assert.AreEqual("cease_fire", result.ActualIntent);
            Assert.IsNull(result.FailureReason);
        }

        [Test]
        public void Run_MatchingCommandWithSlots_Passes()
        {
            var runner = CreateRunner();
            var result = runner.Run(new VoskTestCase
            {
                input = "launch all missiles target hotel one",
                expectedIntent = "launch_weapon",
                expectedSlots = new[]
                {
                    new ExpectedSlot { name = "weapon", value = "missiles" },
                    new ExpectedSlot { name = "target", value = "hotel one" },
                    new ExpectedSlot { name = "quantity", value = "all" },
                },
            });

            Assert.IsTrue(result.Passed);
            Assert.AreEqual("launch_weapon", result.ActualIntent);
        }

        [Test]
        public void Run_ExpectedRejection_NoMatch_Passes()
        {
            var runner = CreateRunner();
            var result = runner.Run(new VoskTestCase
            {
                input = "hello world",
                expectedIntent = null,
                description = "Out-of-grammar phrase should be rejected",
            });

            Assert.IsTrue(result.Passed);
            Assert.IsNull(result.ActualIntent);
        }

        // ─── Intent mismatch ────────────────────────────────────────────

        [Test]
        public void Run_WrongIntent_Fails()
        {
            var runner = CreateRunner();
            var result = runner.Run(new VoskTestCase
            {
                input = "cease fire",
                expectedIntent = "launch_weapon",
            });

            Assert.IsFalse(result.Passed);
            StringAssert.Contains("expected intent 'launch_weapon'", result.FailureReason);
            StringAssert.Contains("got 'cease_fire'", result.FailureReason);
        }

        [Test]
        public void Run_ExpectedMatch_ButNoMatch_Fails()
        {
            var runner = CreateRunner();
            var result = runner.Run(new VoskTestCase
            {
                input = "hello world",
                expectedIntent = "cease_fire",
            });

            Assert.IsFalse(result.Passed);
            StringAssert.Contains("no pattern matched", result.FailureReason);
        }

        // ─── Slot mismatch ──────────────────────────────────────────────

        [Test]
        public void Run_WrongSlotValue_Fails()
        {
            var runner = CreateRunner();
            var result = runner.Run(new VoskTestCase
            {
                input = "launch all missiles target hotel one",
                expectedIntent = "launch_weapon",
                expectedSlots = new[]
                {
                    new ExpectedSlot { name = "target", value = "hotel two" },
                },
            });

            Assert.IsFalse(result.Passed);
            StringAssert.Contains("slot 'target'", result.FailureReason);
            StringAssert.Contains("expected 'hotel two'", result.FailureReason);
        }

        [Test]
        public void Run_MissingExpectedSlot_Fails()
        {
            var runner = CreateRunner();
            var result = runner.Run(new VoskTestCase
            {
                input = "cease fire",
                expectedIntent = "cease_fire",
                expectedSlots = new[]
                {
                    new ExpectedSlot { name = "weapon", value = "missiles" },
                },
            });

            Assert.IsFalse(result.Passed);
            StringAssert.Contains("expected slot 'weapon' not found", result.FailureReason);
        }

        // ─── Threshold filtering ────────────────────────────────────────

        [Test]
        public void Run_BelowMinConfidence_RejectedCorrectly()
        {
            var runner = CreateRunner(minConfidence: 0.4f);
            var result = runner.Run(new VoskTestCase
            {
                input = "cease fire",
                expectedIntent = null,
                wordConfidence = 0.2f,
                description = "Low confidence should be rejected",
            });

            Assert.IsTrue(result.Passed, "Expected rejection due to low confidence");
        }

        [Test]
        public void Run_AboveMinConfidence_AcceptedCorrectly()
        {
            var runner = CreateRunner(minConfidence: 0.4f);
            var result = runner.Run(new VoskTestCase
            {
                input = "cease fire",
                expectedIntent = "cease_fire",
                wordConfidence = 0.85f,
            });

            Assert.IsTrue(result.Passed);
            Assert.AreEqual(0.85f, result.Confidence, 1e-5f);
        }

        [Test]
        public void Run_BelowMinScore_RejectedCorrectly()
        {
            // "cease xyz" against pattern "cease fire" — one hit, one miss.
            // Normalized score = (1.0 + -0.5) / 2 = 0.25, which is below default 0.6.
            var runner = CreateRunner();
            var result = runner.Run(new VoskTestCase
            {
                input = "cease xyz",
                expectedIntent = null,
                description = "Garbled phrase should be rejected by score threshold",
            });

            Assert.IsTrue(result.Passed, result.FailureReason);
        }

        [Test]
        public void Run_ExpectedAcceptance_ButConfidenceRejects_Fails()
        {
            var runner = CreateRunner(minConfidence: 0.4f);
            var result = runner.Run(new VoskTestCase
            {
                input = "cease fire",
                expectedIntent = "cease_fire",
                wordConfidence = 0.2f,
            });

            Assert.IsFalse(result.Passed);
            StringAssert.Contains("rejected", result.FailureReason);
            StringAssert.Contains("confidence", result.FailureReason);
        }

        [Test]
        public void Run_ExpectedRejection_ButCommandAccepted_Fails()
        {
            var runner = CreateRunner();
            var result = runner.Run(new VoskTestCase
            {
                input = "cease fire",
                expectedIntent = null,
                description = "Incorrectly expecting rejection for a valid command",
            });

            Assert.IsFalse(result.Passed);
            StringAssert.Contains("expected rejection", result.FailureReason);
            StringAssert.Contains("cease_fire", result.FailureReason);
        }

        // ─── RunAll + batch results ─────────────────────────────────────

        [Test]
        public void RunAll_AllPass_AllPassedIsTrue()
        {
            var runner = CreateRunner();
            var results = runner.RunAll(new[]
            {
                new VoskTestCase { input = "cease fire", expectedIntent = "cease_fire" },
                new VoskTestCase { input = "hello world", expectedIntent = null },
            });

            Assert.IsTrue(results.AllPassed, results.FailureSummary);
            Assert.AreEqual(2, results.PassCount);
            Assert.AreEqual(0, results.FailCount);
        }

        [Test]
        public void RunAll_OneFails_AllPassedIsFalse()
        {
            var runner = CreateRunner();
            var results = runner.RunAll(new[]
            {
                new VoskTestCase { input = "cease fire", expectedIntent = "cease_fire" },
                new VoskTestCase { input = "cease fire", expectedIntent = "wrong_intent" },
            });

            Assert.IsFalse(results.AllPassed);
            Assert.AreEqual(1, results.PassCount);
            Assert.AreEqual(1, results.FailCount);
            Assert.IsTrue(results.FailureSummary.Length > 0);
        }

        [Test]
        public void RunAll_EmptyArray_AllPassedIsTrue()
        {
            var runner = CreateRunner();
            var results = runner.RunAll(Array.Empty<VoskTestCase>());

            Assert.IsTrue(results.AllPassed);
            Assert.AreEqual(0, results.Results.Length);
        }

        // ─── Command sets constructor ───────────────────────────────────

        [Test]
        public void CommandSetConstructor_ActiveSetOnly()
        {
            var sets = new[]
            {
                new VoskCommandSet("combat", MakeCommands()),
                new VoskCommandSet("navigation", new[]
                {
                    new VoskCommandDefinition("heading", new[]
                    {
                        new[] { "heading", "{target}" },
                    }),
                }),
            };

            // Only activate "combat" — heading should not be available
            var runner = new VoskBatchTestRunner(MakeSlots(), sets,
                new[] { "combat" });

            var result = runner.Run(new VoskTestCase
            {
                input = "cease fire",
                expectedIntent = "cease_fire",
            });

            Assert.IsTrue(result.Passed);
        }

        [Test]
        public void CommandSetConstructor_UnknownSet_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
            {
                new VoskBatchTestRunner(MakeSlots(),
                    new[] { new VoskCommandSet("combat", MakeCommands()) },
                    new[] { "nonexistent" });
            });
        }

        // ─── CSV export ─────────────────────────────────────────────────

        [Test]
        public void ToCsv_ContainsHeaderAndRows()
        {
            var runner = CreateRunner();
            var results = runner.RunAll(new[]
            {
                new VoskTestCase { input = "cease fire", expectedIntent = "cease_fire" },
                new VoskTestCase { input = "hello world", expectedIntent = null },
            });

            string csv = VoskBatchTestRunner.ToCsv(results);

            StringAssert.Contains("Input,Expected,Actual,Score,Confidence,Status,Reason", csv);
            StringAssert.Contains("cease fire", csv);
            StringAssert.Contains("PASS", csv);
        }

        // ─── Diagnostics ────────────────────────────────────────────────

        [Test]
        public void Run_PopulatesDiagnostics()
        {
            var runner = CreateRunner();
            var result = runner.Run(new VoskTestCase
            {
                input = "cease fire",
                expectedIntent = "cease_fire",
            });

            Assert.IsTrue(result.Passed);
            Assert.IsNotNull(result.Diagnostics.Attempts);
            Assert.IsTrue(result.Diagnostics.Attempts.Length > 0);
            Assert.AreEqual("cease fire", result.Diagnostics.InputText);
        }

        // ─── Edge cases ─────────────────────────────────────────────────

        [Test]
        public void Run_NullInput_NoMatch()
        {
            var runner = CreateRunner();
            var result = runner.Run(new VoskTestCase
            {
                input = null,
                expectedIntent = null,
            });

            Assert.IsTrue(result.Passed);
        }

        [Test]
        public void Run_EmptyInput_NoMatch()
        {
            var runner = CreateRunner();
            var result = runner.Run(new VoskTestCase
            {
                input = "",
                expectedIntent = null,
            });

            Assert.IsTrue(result.Passed);
        }

        [Test]
        public void Run_NoExpectedSlots_IgnoresActualSlots()
        {
            var runner = CreateRunner();
            // Match with slots but don't assert on them
            var result = runner.Run(new VoskTestCase
            {
                input = "launch all missiles target hotel one",
                expectedIntent = "launch_weapon",
                expectedSlots = null,
            });

            Assert.IsTrue(result.Passed);
            Assert.IsTrue(result.ActualSlots.Length > 0, "Should still populate actual slots");
        }

        [Test]
        public void RunAll_NullCases_Throws()
        {
            var runner = CreateRunner();
            Assert.Throws<ArgumentNullException>(() => runner.RunAll(null));
        }

        [Test]
        public void FailureSummary_ContainsIndex_And_Description()
        {
            var runner = CreateRunner();
            var results = runner.RunAll(new[]
            {
                new VoskTestCase
                {
                    input = "cease fire",
                    expectedIntent = "wrong",
                    description = "Testing wrong intent",
                },
            });

            StringAssert.Contains("[0]", results.FailureSummary);
            StringAssert.Contains("Testing wrong intent", results.FailureSummary);
        }
    }
}
