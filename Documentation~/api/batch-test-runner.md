# VoskBatchTestRunner

`public class VoskBatchTestRunner` -- Namespace: `VoskXR.Testing`

Pure C# runner for regression-testing command definitions. No MonoBehaviour dependency -- works in Edit Mode without Play Mode or audio hardware. Instantiates a `VoskCommandParser` directly (the same path that `InjectText` uses internally).

## Constructors

| Constructor | Description |
|-------------|-------------|
| `VoskBatchTestRunner(slots, commands, minScore, minConfidence)` | All commands active as a flat list. |
| `VoskBatchTestRunner(slots, sets, activeSetNames, minScore, minConfidence)` | Named command sets with explicit active set selection. |

## Methods

| Method | Description |
|--------|-------------|
| `RunAll(VoskTestCase[])` | Returns `VoskBatchResults` with per-case pass/fail. |
| `Run(VoskTestCase)` | Returns a single `VoskTestResult`. |
| `ToCsv(VoskBatchResults)` | Static. Exports results as a CSV string for diffing across runs. |

## VoskBatchResults

`public class VoskBatchResults` -- Namespace: `VoskXR.Testing`

Aggregated results from `RunAll`.

| Property | Type | Description |
|----------|------|-------------|
| `Results` | `VoskTestResult[]` | Individual results for each test case, in input order. |
| `AllPassed` | `bool` | True when every test case passed. |
| `FailureSummary` | `string` | Multi-line summary of all failures for NUnit assertion messages. Empty when all passed. |
| `PassCount` | `int` | Number of passing test cases. |
| `FailCount` | `int` | Number of failing test cases. |

## VoskTestResult

`public class VoskTestResult` -- Namespace: `VoskXR.Testing`

Result of running a single `VoskTestCase` through the batch test runner.

| Field | Type | Description |
|-------|------|-------------|
| `TestCase` | `VoskTestCase` | The test case that produced this result. |
| `ActualIntent` | `string` | The intent that was accepted, or null if no command passed thresholds. |
| `ActualSlots` | `VoskSlotMatch[]` | Slot matches from the accepted command. |
| `Score` | `float` | Best match score from the parser (0 if no match). |
| `Confidence` | `float` | Minimum word confidence across matched tokens (-1 if unavailable). |
| `Passed` | `bool` | True if the actual result matches expectations. |
| `FailureReason` | `string` | Human-readable failure reason, or null if passed. |

## Example

```csharp
using VoskXR.Commands;
using VoskXR.Testing;

var runner = new VoskBatchTestRunner(slots, commands, minScore: 0.6f, minConfidence: 0.4f);
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

Use `VoskTestSuiteAsset.ToJson()` and `VoskTestSuiteAsset.FromJson()` to import/export.

## See Also

- [Editor Testing](../editor-testing.md) -- visual Batch Test Runner UI guide
- [ScriptableObject Assets](scriptable-objects.md) -- `VoskTestSuiteAsset`, `VoskTestCase`
- [VoskCommandRecogniser](command-recogniser.md) -- runtime command pipeline
- [Command Definitions](command-definitions.md) -- defining commands and slots under test
