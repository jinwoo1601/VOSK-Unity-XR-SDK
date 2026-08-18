# Editor Testing

The SDK provides multiple tools for iterating on speech commands without deploying to Quest. You can inspect the live pipeline visually, speak into your PC microphone, inject text programmatically, or regression-test command definitions in batch.

These tools are stages of one tuning loop. Reproduce the utterance in the [Command Debug Window](#command-debug-window) to see which pattern won and why it scored what it did; confirm the pattern is not a one-off by reading the whole playtest back from the [session debug log](#session-debug-log); then pin the case in the [Batch Test Runner](#batch-test-runner) so a later threshold, alias, or grammar edit cannot quietly undo the fix. [Matching and Scoring](scoring.md) is the reference for the numbers all three surfaces report.

---

## Command Debug Window

Open **Window > VoXR > Command Debug** during Play Mode to inspect the full command pipeline in real time. The window looks for the scene's `VoxrSpeechRecogniser` and `VoxrCommandRecogniser` when it opens and again when Play Mode is entered; if the pair is spawned later than that, click **Find Components** on the warning it shows to search again.

### Left Panel (recognition state)

- **Audio level meters** -- pre-AGC RMS, post-AGC RMS, and current AGC gain. Useful for verifying microphone input and AGC behaviour. Windows Editor only, like the [live-mic backend](#live-microphone-windows-editor) they measure -- macOS and Linux Editors show *Audio levels: Editor-Win only* in their place.
- **Partial result** -- the live VOSK partial transcript, updated continuously as you speak.
- **Final result** -- the completed transcript text at utterance boundaries.
- **Per-word confidence bars** -- each word with a colour-coded confidence bar (green > yellow > red).

### Right Panel (command matching)

- **Active command sets** -- which sets are currently loaded in the parser.
- **Pending command** -- when a command is in pending state, shows the intent, reason (partial match, awaiting confirmation, or awaiting disambiguation, which also lists the words that answer it), filled slots, unfilled slots, and elapsed time since entering pending state.
- **Last match breakdown** -- for each command extracted from the utterance, the pattern that won selection: intent, score, confidence, threshold pass/fail, and reject reason (if any). Losing candidates are not shown. Accepted commands are highlighted in green. A **tie line** appears when the winner beat an equally-good rival on registration order alone -- naming the rival, and saying whether it was a *sibling* (one dropped word apart, which is what `disambiguateSiblingTies` answers) or *not a sibling*, which means duplicate or overlapping patterns in your grammar. Nothing else in the panel shows a tie: the winner is correct by every rule the parser has, so a coin flip and a clean victory look identical without it.
- **Slot details** -- matched slot word positions with per-slot confidence. The positions are a half-open `[start, end)` range of token indices into the whitespace-split transcript -- the same convention the [session log](#what-is-recorded) records.
- **Match history** -- scrolling list of the last 20 match results. The cap counts *attempts*, not utterances, so one breath carrying two commands fills two of the twenty slots. History entries carry no timestamps; for a timestamped record, read the [session debug log](#session-debug-log).

### Bottom Toolbar

- **Inject field** -- type a phrase and press Enter (or click Send) to push it through the full command pipeline without a microphone. Useful for testing specific phrases, edge cases, or reproducing issues.
- **Clear** -- clears match history and resets the display.
- **Pause / Resume** -- freezes the display so you can inspect a result without it being overwritten by the next utterance. On resume, stale results are skipped so the display jumps to the next genuinely new result.

The debug window is Editor-only and has zero cost in builds: it lives in an Editor-only assembly, so no player build compiles it at all. The underlying diagnostic structs (`VoxrMatchDiagnostics`, `VoxrMatchAttempt`, `VoxrDiagnosticSlotMatch`) do sit in the runtime assembly, and they are the part carrying an `#if UNITY_EDITOR` guard -- so they too are compiled out of non-Editor builds.

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
- `attempts` -- one entry per decision the recogniser logged. On the ordinary parse path that is one per extraction round: the pattern that **won** selection that round, with `intent`, `pattern`, `score` vs `minScore`, `aggregateConfidence` vs `minConfidence`, extracted `slots`, `accepted`, `rejectReason` when it did not fire, and the two tie fields described below. Losing candidates are not recorded, so a pattern's absence means it lost selection, not that it was never tried. Six pipeline events (`no match`, confirm/cancel, an answer to a disambiguation prompt, follow-up slot-fill, a follow-up fill refused for re-scoring at or below zero, pending timeout) publish a single synthetic attempt with an empty `pattern` instead. [Matching and Scoring](scoring.md#reading-a-session-log) maps each field to the rule that produced it and tabulates those events.

Each slot carries `startWord` and `endWord` -- a half-open `[startWord, endWord)` range of **token indices into the whitespace-split `inputText`**, not into the `words` array. So a slot filled by a single token spans `[n, n+1)`, and the range stays meaningful even when `words` is empty.

Tie detection is in the log. Each attempt carries `tiedRival` -- the equally-good rival it beat on registration order alone, as `intent (pattern N)`, empty when nothing tied it -- and `tiedRivalIsSibling`, true when that rival was one dropped word apart -- the shape `disambiguateSiblingTies` answers, whether or not it could be phrased as a question. `false` covers everything else that can tie, and the two cases differ: compare `tiedRival`'s intent against the attempt's own `intent`. A *differing* intent is a grammar defect -- duplicate or overlapping patterns, one of which can never fire. The *same* intent is the winner's own second phrasing tying it, which is harmless and routine. That is the same finding the `Tied with:` line reports in the [debug window's last-match panel](#right-panel-command-matching) and the batch runner's per-row diagnostics, so a coin-flip win stays distinguishable from a clean one across a whole playtest and not just while you are watching. Recording them took `schemaVersion` to `2`; both fields are additive, so tooling written against `1` still reads the file.

The file carries a `schemaVersion`, package and Unity versions, session start/end timestamps, an `entryCount`, and a `readme` field describing the format, so external tooling can consume it without reading the SDK source. A confidence of `-1` means no per-word confidence data was available for the matched **span** -- usually because the utterance carried no word data at all, as with injected text, but it can also appear with `words` populated when the matched span came from a segment that carried none. One caveat for anything parsing `rejectReason`: the numbers inside it are formatted with the Editor's current culture, so the decimal separator may be `,` -- match on the surrounding words, not on the whole literal.

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

### WAV Replay (Repository Tests)

The same backend carries an internal playback mode used by the repository's acoustic regression suite: committed WAV fixtures (48 kHz mono 16-bit, amplitude-scaled to the measured Quest 3 microphone range) replay through the identical downsampler -> AGC -> VOSK pipeline as live microphone audio, and PlayMode tests assert the recognized command per fixture. The test-data containers ([`VoxrAudioTestSuiteAsset` / `VoxrAudioTestCase`](api/scriptable-objects.md#voxraudiotestsuiteasset)) ship with the package, though nothing in the shipped package runs them -- the only consumer is this repository's WAV-replay suite, and the Batch Test Window accepts a `VoxrTestSuiteAsset` only; the fixture corpus, its reproducible TTS generation script, and the replay tests live under the repository's `Tests~/` folder and are stripped from the published package. Replay results are a regression baseline against committed audio -- TTS speech is cleaner than human speech, so the suite detects changes in behaviour, not absolute recognition quality.

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

Open **Window > VoXR > Batch Test Runner** and fill the four configuration fields:

- **Test Suite** -- the `VoxrTestSuiteAsset` holding the cases to run.
- **Slot Definitions** -- the `VoxrSlotAsset`s the patterns under test refer to.
- **Command Sets** -- the `VoxrCommandSetAsset`s to load.
- **Active Sets** -- which set names to activate. Leave it empty and every set you assigned is activated.

**Min Score**, **Min Confidence**, and **Coverage Weight** mirror the fields of the same name on `VoxrCommandRecogniser` and are passed to the runner as they stand. Set them to the values your recogniser uses, or batch results will not track what the runtime does.

Then click **Run All**. Results appear in a table of `Input`, `Expected`, `Result`, `Score`, and `Status`, where the `Result` cell prints the accepted intent with its slots as `intent(slot:value,...)`, or `(none)` when nothing was accepted. The toolbar carries three more actions:

- **Re-run Failed** -- re-runs only the failing rows, rebuilding the runner from the configuration fields as they stand, so a threshold edit can be retried without discarding the passing rows. Enabled once a run has produced at least one failure.
- **Export CSV** -- writes the run to disk for diffing across runs. Its columns are not the screen's: `Input,Expected,Actual,Score,Confidence,Status,Reason` -- confidence and the failure reason are CSV-only.
- **Import JSON** / **Export JSON** -- shown once a Test Suite is assigned, and read or write that asset's case list in the [JSON format](#test-case-authoring) below. Import **replaces** the asset's cases rather than appending to them and clears the current results; it is recorded as an undo step, so a mistaken import can be undone.

Ticking a row's checkbox expands its diagnostics: the case description, the failure reason, the measured and simulated word confidences, the extracted slots, and one line per extraction round with that round's intent, score against `minScore`, pattern, and reject reason. Those lines carry the same **tie line** the debug window shows ([above](#right-panel-command-matching)) -- and a corpus run is where a tie is most likely to be caught, because the pass/fail column cannot show one: the case passes whenever registration order happens to land on the expected intent.

### Programmatic API

```csharp
using VoXR.Commands;
using VoXR.Testing;

var runner = new VoxrBatchTestRunner(slots, commands, minScore: 0.6f, minConfidence: 0.4f);
var results = runner.RunAll(testCases);
Assert.IsTrue(results.AllPassed, results.FailureSummary);
```

`VoxrBatchTestRunner` is pure C# -- no MonoBehaviour dependency and no audio hardware required. It drives an internal parser instance directly, the same code path that `InjectText` uses internally.

Both constructors also take an optional `coverageWeight` (default `1.0`, matching the recogniser; named `skippedWordPenalty` before #65). Pass the value your `VoxrCommandRecogniser` uses if you have tuned it, so batch results track runtime behaviour. One caveat since #65, on this path and in the window alike: the batch runner receives real text rather than the decoder's `[unk]` for out-of-grammar words, so trailing filler is charged here where the grammar-constrained decoder would have made it free -- see [Coverage](command-recognition.md#coverage).

For command-set-aware testing:

```csharp
var runner = new VoxrBatchTestRunner(slots, sets, activeSetNames, minScore: 0.6f, minConfidence: 0.4f);
```

### Test Case Authoring

Create a `VoxrTestSuiteAsset` via **Assets > Create > VoXR > Test Suite** and author test cases in the Inspector. Or move them in and out as JSON for portability and version control -- with the window's **Import JSON** / **Export JSON** buttons, or from code via `VoxrTestSuiteAsset.ToJson()` and `FromJson()`:

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
| `expectedIntent` | Expected intent name. Empty or null means expect rejection -- no match, below a threshold, or a required slot left unfilled. |
| `expectedSlots` | Array of `{name, value}` pairs. Omit to skip slot verification. |
| `wordConfidence` | Simulated uniform word confidence (0--1). Set to `-1` to omit word data. |
| `description` | Human-readable description for the results table. |

That third rejection cause is the completeness check: a pattern can clear both thresholds and still be refused because a required slot was never filled, reported as `required slot unfilled`. The runner applies the same [completeness gate](scoring.md#completeness-independent-of-score) the recogniser does, so a case cannot pass here on behaviour the runtime refuses.

### API Reference

Two constructors -- a flat command list, or named sets with an explicit active-set selection -- plus `RunAll` for a suite, `Run` for a single case, and the static `ToCsv`. `VoxrBatchResults` carries `Results`, `AllPassed`, `FailureSummary`, `PassCount`, and `FailCount`; each `VoxrTestResult` carries the accepted intent and slots, the score, the confidence, and the failure reason. Full signatures, types, and per-field caveats: [VoxrBatchTestRunner](api/batch-test-runner.md).

---

## See Also

- [Command Recognition](command-recognition.md) -- The full parsing pipeline that these tools exercise
- [Matching and Scoring](scoring.md) -- The scoring rules behind every number these tools report, and how to read a session log
- [VoxrBatchTestRunner](api/batch-test-runner.md) -- API reference for the batch runner, its results, and per-case results
- [Push-to-Talk and Error Handling](push-to-talk.md) -- Push-to-talk pattern and error code reference
- [Troubleshooting](troubleshooting.md) -- Common issues when testing in the Editor
