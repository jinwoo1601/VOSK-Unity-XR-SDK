# Editor Testing

The SDK provides multiple tools for iterating on speech commands without deploying to Quest. You can inspect the live pipeline visually, speak into your PC microphone, inject text programmatically, or regression-test command definitions in batch.

---

## Command Debug Window

Open **Window > VoXR > Command Debug** during Play Mode to inspect the full command pipeline in real time.

### Left Panel (recognition state)

- **Audio level meters** -- pre-AGC RMS, post-AGC RMS, and current AGC gain. Useful for verifying microphone input and AGC behaviour.
- **Partial result** -- the live VOSK partial transcript, updated continuously as you speak.
- **Final result** -- the completed transcript text at utterance boundaries.
- **Per-word confidence bars** -- each word with a colour-coded confidence bar (green > yellow > red).

### Right Panel (command matching)

- **Active command sets** -- which sets are currently loaded in the parser.
- **Pending command** -- when a command is in pending state, shows the intent, reason (partial match or awaiting confirmation), filled slots, unfilled slots, and elapsed time since entering pending state.
- **Last match breakdown** -- for each command definition attempted: intent, score, confidence, threshold pass/fail, and reject reason (if any). Accepted commands are highlighted in green.
- **Slot details** -- matched slot word positions (start/end indices) with per-slot confidence.
- **Match history** -- scrolling list of the last 20 match results with timestamps.

### Bottom Toolbar

- **Inject field** -- type a phrase and press Enter (or click Send) to push it through the full command pipeline without a microphone. Useful for testing specific phrases, edge cases, or reproducing issues.
- **Clear** -- clears match history and resets the display.
- **Pause / Resume** -- freezes the display so you can inspect a result without it being overwritten by the next utterance. On resume, stale results are skipped so the display jumps to the next genuinely new result.

The debug window is Editor-only (`#if UNITY_EDITOR`) and has zero cost in builds. The underlying diagnostic structs (`VoxrMatchDiagnostics`, `VoxrMatchAttempt`, `VoxrDiagnosticSlotMatch`) are compiled out of non-Editor builds.

---

## Session Debug Log

The debug window's history is live-only: it holds the last 20 matches and is discarded when Play Mode exits. For post-session analysis, every match the recogniser produces during a Play Mode session is also written to disk automatically.

When Play Mode ends, the session is exported to:

```
<project>/Library/VoxrDebugLogs/session-<yyyy-MM-dd_HH-mm-ss>.json
```

The Console logs the exact path on export. `Library/` is not version-controlled, so the logs never enter your repository. The ten most recent sessions are retained; older ones are pruned automatically.

Export is always on, requires no setup, and needs no debug window open -- the collector runs headless. A session that produced no matches writes no file.

Test runs are skipped. Both a `-runTests`/CI invocation and an in-editor Test Runner run drive the same Play Mode diagnostics, but exporting from them would churn through the ten retained slots and evict real playtest sessions. The collector stays inactive when `Application.isBatchMode` is true, and a Test Runner callback flags it for the duration of any in-editor run.

That callback lives in its own assembly, compiled only when `com.unity.test-framework` is installed, so the package takes no dependency on the test framework. Without it you still get the batch-mode guard.

### What Is Recorded

The whole session, not just the window's 20-entry history. Each entry is one utterance:

- `timestamp` (ISO 8601) and `frame` -- when the utterance was matched.
- `activeSets` -- the command sets active at that moment.
- `inputText` -- the transcript that was parsed.
- `words` -- each recognised word with its confidence and start/end times. Empty when no per-word data was available, as with injected text.
- `attempts` -- every command definition evaluated against the utterance: `intent`, `pattern`, `score` vs `minScore`, `aggregateConfidence` vs `minConfidence`, extracted `slots`, `accepted`, and `rejectReason` when it did not fire.

Each slot carries `startWord` and `endWord` -- a half-open `[startWord, endWord)` range of **token indices into the whitespace-split `inputText`**, not into the `words` array. So a slot filled by a single token spans `[n, n+1)`, and the range stays meaningful even when `words` is empty.

The file carries a `schemaVersion`, package and Unity versions, session start/end timestamps, and a `readme` field describing the format, so external tooling can consume it without reading the SDK source. A confidence of `-1` means no per-word confidence data was available for that utterance.

This makes a session readable by scripts or LLM tooling for questions the live window cannot answer -- which commands were rejected just under threshold across a whole playtest, which words consistently come back low-confidence, or which slots repeatedly fail to extract.

Like the rest of the diagnostics, the collector is Editor-only and compiled out of builds.

---

## Live Microphone (Windows Editor)

On Windows, `VoxrSpeechRecogniser.StartRecognition()` transparently auto-routes audio through `UnityEngine.Microphone` and a desktop build of `libvosk.dll` via P/Invoke. Existing scenes and user code work with zero changes -- speak into your PC microphone and watch commands fire in the Console.

The required Windows DLLs (`libvosk.dll` plus three MinGW runtime dependencies) ship inside the package under `Runtime/Plugins/x86_64/`. No manual download or extra setup is needed -- press Play in the Editor and the live-mic backend takes over.

### How It Works

The Editor backend (`EditorMicBackend`) captures audio via `UnityEngine.Microphone` at the system's default sample rate, applies C# ports of the native bridge's DSP (48 kHz -> 16 kHz FIR downsampler and AGC with soft saturation), and feeds the processed audio to `libvosk.dll` via P/Invoke. Model loading is offloaded to a background thread to avoid main-thread hitches.

### Scope and Limitations

- **Editor-only.** The live mic backend is excluded from Android, standalone Windows, Linux, and macOS builds via `#if UNITY_EDITOR_WIN` guards. Android runtime behaviour is unchanged.
- **Windows only.** macOS and Linux Editors do not have a live mic backend -- use text injection on those platforms.
- **Default microphone.** The backend uses the Windows default input device. Ensure your microphone is connected and set as the default.

---

## Text Injection API

For unit tests, CI, replay, and threshold tuning without audio hardware. All injection methods are main-thread only and fire the same events as real recognition, so existing handlers work unchanged.

### Inject Through the Command Pipeline

```csharp
// Full pipeline: parser -> threshold -> buffer -> debounce
commandRecogniser.InjectText("launch all missiles target hotel one");
commandRecogniser.FlushPendingBuffer(); // Force immediate parse
```

### Inject with Simulated Confidence

```csharp
// Test threshold behaviour with specific confidence values
var words = VoxrSpeechRecogniser.CreateSimulatedWords("cease fire", confidence: 0.85f);
commandRecogniser.InjectText("cease fire", words);
```

### Inject Raw Recogniser Events

```csharp
// Bypass the command pipeline entirely -- fires speech recogniser events directly
recogniser.InjectResult("hello world");
recogniser.InjectPartialResult("hel");
```

### Notes

- `InjectText` feeds text through the full command pipeline (parser, threshold, buffer, debounce). Call `FlushPendingBuffer()` after injection if you need synchronous results (e.g. in tests).
- `InjectResult` and `InjectPartialResult` fire events on `VoxrSpeechRecogniser` directly, bypassing the command recogniser. Use these to test speech-level event handling.
- `CreateSimulatedWords` generates `VoxrWord[]` with uniform confidence and sequential timing. Useful for testing `minConfidence` threshold behaviour.

---

## Batch Test Runner

Regression-test command definitions after changing thresholds, aliases, or slot values. The batch runner feeds a list of test cases through the command parser, applies threshold filtering, and compares against expected intents and slots.

### Visual UI

Open **Window > VoXR > Batch Test Runner**. Assign slot/command assets and a `VoxrTestSuiteAsset`, then click **Run All**. Results appear in a table with per-row expansion for diagnostics. Export results as CSV for diffing across runs.

### Programmatic API

```csharp
using VoXR.Commands;
using VoXR.Testing;

var runner = new VoxrBatchTestRunner(slots, commands, minScore: 0.6f, minConfidence: 0.4f);
var results = runner.RunAll(testCases);
Assert.IsTrue(results.AllPassed, results.FailureSummary);
```

`VoxrBatchTestRunner` is pure C# -- no MonoBehaviour dependency and no audio hardware required. It instantiates a `VoxrCommandParser` directly (the same code path that `InjectText` uses internally).

Both constructors also take an optional `skippedWordPenalty` (default `1.0`, matching the recogniser). Pass the value your `VoxrCommandRecogniser` uses if you have tuned it, so batch results predict runtime behaviour -- see [Skipped-word penalty](command-recognition.md#skipped-word-penalty).

For command-set-aware testing:

```csharp
var runner = new VoxrBatchTestRunner(slots, sets, activeSetNames, minScore: 0.6f, minConfidence: 0.4f);
```

### Test Case Authoring

Create a `VoxrTestSuiteAsset` via **Assets > Create > VoXR > Test Suite** and author test cases in the Inspector. Or import/export as JSON for portability and version control:

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

| Field | Description |
|-------|-------------|
| `input` | Text to feed through the command parser. |
| `expectedIntent` | Expected intent name. Empty or null means expect rejection (no match or below threshold). |
| `expectedSlots` | Array of `{name, value}` pairs. Omit to skip slot verification. |
| `wordConfidence` | Simulated uniform word confidence (0--1). Set to `-1` to omit word data. |
| `description` | Human-readable description for the results table. |

### API Reference

| Method | Description |
|--------|-------------|
| `VoxrBatchTestRunner(slots, commands, minScore, minConfidence, skippedWordPenalty)` | Constructor. All commands active. |
| `VoxrBatchTestRunner(slots, sets, activeSetNames, minScore, minConfidence, skippedWordPenalty)` | Constructor with named command sets. |
| `RunAll(VoxrTestCase[])` | Returns `VoxrBatchResults` with per-case pass/fail. |
| `Run(VoxrTestCase)` | Returns a single `VoxrTestResult`. |
| `ToCsv(VoxrBatchResults)` | Static. Exports results as a CSV string. |

| Property | Type | Description |
|----------|------|-------------|
| `VoxrBatchResults.AllPassed` | `bool` | True when every test case passed. |
| `VoxrBatchResults.FailureSummary` | `string` | Multi-line summary of all failures for NUnit assertion messages. |
| `VoxrBatchResults.PassCount` | `int` | Number of passing test cases. |
| `VoxrBatchResults.FailCount` | `int` | Number of failing test cases. |

---

## See Also

- [Command Recognition](command-recognition.md) -- The full parsing pipeline that these tools exercise
- [Push-to-Talk and Error Handling](push-to-talk.md) -- Push-to-talk pattern and error code reference
- [Troubleshooting](troubleshooting.md) -- Common issues when testing in the Editor
