# VoxrBatchTestRunner

`public class VoxrBatchTestRunner` -- Namespace: `VoXR.Testing`

Pure C# runner for regression-testing command definitions. No MonoBehaviour dependency and no audio hardware required. Drives an internal parser instance directly -- the same path that `InjectText` uses internally.

## Constructors

| Constructor | Description |
|-------------|-------------|
| `VoxrBatchTestRunner(slots, commands, minScore, minConfidence, coverageWeight)` | All commands active as a flat list. |
| `VoxrBatchTestRunner(slots, sets, activeSetNames, minScore, minConfidence, coverageWeight)` | Named command sets with explicit active set selection. |

All three tuning parameters are optional. Two of them are gates -- `minScore` and `minConfidence` -- while `coverageWeight` is not a gate but the weight charged for recognised words a match leaves unexplained. They default to `0.6`, `0.4`, and `1.0`, which are the recogniser's defaults today; but the runner's `minScore` and `minConfidence` defaults are its own literals rather than the recogniser's, so the two can drift apart. Only `coverageWeight` defaults from the parser's shared constant. Pass the values your `VoxrCommandRecogniser` uses rather than relying on the defaults matching, so batch results track runtime behaviour. Since #140 `minScore` also reaches the parser the runner builds, so the Editor-only authoring warnings a batch run logs at construction — the sibling-tie warning and the cancel-collision report — are judged against the same threshold the run gates on, rather than against `0.6` regardless. `coverageWeight` was named `skippedWordPenalty` before #65 — a **named**-argument caller must update; positional callers are unaffected.

> **Batch scores are a lower bound, not the runtime score.** [Coverage](../scoring.md#2-coverage) exempts the literal `[unk]` token, which is what a grammar-constrained decoder returns for a word outside its vocabulary. This runner receives real text instead, so a trailing word the decoder would have hidden arrives verbatim and is charged: "cease fire please" scores `2 / (2 + 1)` = `0.67` here against `1.00` through the live decoder. Expect batch scores at or below what the same utterance gets at runtime, and treat a batch regression on a grammar whose users add natural filler with that in mind.

## Methods

| Method | Description |
|--------|-------------|
| `RunAll(VoxrTestCase[])` | Returns `VoxrBatchResults` with per-case pass/fail. |
| `Run(VoxrTestCase)` | Returns a single `VoxrTestResult`. |
| `ToCsv(VoxrBatchResults)` | Static. Exports results as a CSV string for diffing across runs. |

## VoxrBatchResults

`public class VoxrBatchResults` -- Namespace: `VoXR.Testing`

Aggregated results from `RunAll`.

| Property | Type | Description |
|----------|------|-------------|
| `Results` | `VoxrTestResult[]` | Individual results for each test case, in input order. |
| `AllPassed` | `bool` | True when every test case passed. |
| `FailureSummary` | `string` | Multi-line summary of all failures for NUnit assertion messages. Empty when all passed. |
| `PassCount` | `int` | Number of passing test cases. |
| `FailCount` | `int` | Number of failing test cases. |

## VoxrTestResult

`public class VoxrTestResult` -- Namespace: `VoXR.Testing`

Result of running a single `VoxrTestCase` through the batch test runner. One result covers the whole utterance even when the parser extracted several commands from it: the fields describe the round that was accepted, or the strongest rejected round when none was — so a case cannot assert a second extracted intent.

**A [barred](../scoring.md#the-leading-required-miss-bar) round never reaches the runner at all.** Where a round's winner missed its first required element the parser emits nothing for that round, so the runner has no round to describe for it — but any *other* round that emitted is still reported as usual. For `scoring.md` §7 D's `"cease fire target hotel one"` the result is `cease_fire` at `Score` `1.00`, not a rejection.

Only when **every** round was barred does the case come back with `ActualIntent` null, `Score` `0` and `Confidence` `-1`, indistinguishable from an utterance nothing matched — even though a pattern matched every element but its first and scored well above `minScore` (up to `0.86` on a seven-element pattern). A case that expected an intent surfaces it as `FailureReason` `expected intent '…' but no pattern matched`; a case that expects rejection simply passes, with `FailureReason` null.

| Field | Type | Description |
|-------|------|-------------|
| `TestCase` | `VoxrTestCase` | The test case that produced this result. |
| `ActualIntent` | `string` | The intent that was accepted, or null when no command passed the thresholds -- or when the round the runner reports was refused for a missing required slot (`required slot unfilled`), or when **every** round was barred for missing its first required element so no round was left to report. Both are separate checks from the thresholds. |
| `ActualSlots` | `VoxrSlotMatch[]` | Slot matches from the accepted command. Never null — an empty array when nothing was accepted or the accepted command took no slots. |
| `Score` | `float` | The score of the round that was **accepted** — the runner stops at the first round clearing the gates, so this is not the highest score seen. When nothing was accepted it is the highest score among the rejected rounds, and `0` when no round was left to report — which covers both "nothing matched" and "every round that matched was barred". |
| `Confidence` | `float` | Minimum word confidence across matched tokens (-1 if unavailable). |
| `Passed` | `bool` | True if the actual result matches expectations. |
| `FailureReason` | `string` | Human-readable failure reason, or null if passed. |

## Example

```csharp
using VoXR.Commands;
using VoXR.Testing;

var runner = new VoxrBatchTestRunner(slots, commands, minScore: 0.6f, minConfidence: 0.4f);
var results = runner.RunAll(testCases);
Assert.IsTrue(results.AllPassed, results.FailureSummary);
```

## JSON Test Case Format

Test cases can be authored in JSON for portability and version control:

```json
{
    "cases": [
        {
            "input": "launch all missiles target hotel one",
            "expectedIntent": "launch_weapon",
            "expectedSlots": [{"name": "target", "value": "hotel one"}],
            "wordConfidence": -1,
            "description": "Full launch command with target"
        },
        {
            "input": "hello world",
            "expectedIntent": "",
            "description": "Out-of-grammar phrase should be rejected"
        },
        {
            "input": "cease fire",
            "expectedIntent": "",
            "wordConfidence": 0.3,
            "description": "Low confidence should be rejected by threshold"
        }
    ]
}
```

Use `VoxrTestSuiteAsset.ToJson()` and `VoxrTestSuiteAsset.FromJson()` to import/export from code. The [Batch Test Runner window](../editor-testing.md#batch-test-runner) exposes the same two calls as its **Import JSON** / **Export JSON** buttons, so a suite can be moved in and out without writing any.

## See Also

- [Editor Testing](../editor-testing.md) -- visual Batch Test Runner UI guide
- [ScriptableObject Assets](scriptable-objects.md) -- `VoxrTestSuiteAsset`, `VoxrTestCase`
- [VoxrCommandRecogniser](command-recogniser.md) -- runtime command pipeline
- [Command Definitions](command-definitions.md) -- defining commands and slots under test
