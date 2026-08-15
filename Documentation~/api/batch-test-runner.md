# VoxrBatchTestRunner

`public class VoxrBatchTestRunner` -- Namespace: `VoXR.Testing`

Pure C# runner for regression-testing command definitions. No MonoBehaviour dependency and no audio hardware required. Instantiates a `VoxrCommandParser` directly (the same path that `InjectText` uses internally).

## Constructors

| Constructor | Description |
|-------------|-------------|
| `VoxrBatchTestRunner(slots, commands, minScore, minConfidence, coverageWeight)` | All commands active as a flat list. |
| `VoxrBatchTestRunner(slots, sets, activeSetNames, minScore, minConfidence, coverageWeight)` | Named command sets with explicit active set selection. |

All three thresholds are optional and default to the recogniser's own (`minScore` `0.6`, `minConfidence` `0.4`, `coverageWeight` `1.0`). Pass the values your `VoxrCommandRecogniser` uses if you have tuned them, so batch results track runtime behaviour. `coverageWeight` was named `skippedWordPenalty` before #65 — a **named**-argument caller must update; positional callers are unaffected.

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

Result of running a single `VoxrTestCase` through the batch test runner.

| Field | Type | Description |
|-------|------|-------------|
| `TestCase` | `VoxrTestCase` | The test case that produced this result. |
| `ActualIntent` | `string` | The intent that was accepted, or null if no command passed thresholds. |
| `ActualSlots` | `VoxrSlotMatch[]` | Slot matches from the accepted command. |
| `Score` | `float` | Best match score from the parser (0 if no match). |
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

Use `VoxrTestSuiteAsset.ToJson()` and `VoxrTestSuiteAsset.FromJson()` to import/export.

## See Also

- [Editor Testing](../editor-testing.md) -- visual Batch Test Runner UI guide
- [ScriptableObject Assets](scriptable-objects.md) -- `VoxrTestSuiteAsset`, `VoxrTestCase`
- [VoxrCommandRecogniser](command-recogniser.md) -- runtime command pipeline
- [Command Definitions](command-definitions.md) -- defining commands and slots under test
