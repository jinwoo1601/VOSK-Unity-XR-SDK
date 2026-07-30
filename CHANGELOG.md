# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- WAV-replay test seam and acoustic regression corpus (Tier B of the automated-verification plan). The Windows editor backend's processing pipeline (48 kHz -> 16 kHz downsampler -> AGC -> int16 -> VOSK -> result dispatch) is now extracted into a single `ProcessChunk` shared by microphone capture and a new internal playback mode, so committed WAV fixtures replay through byte-for-byte the same path live audio takes — pre-DSP, so DSP and AGC changes are exercised by replay. Playback is armed with `StartPlayback` (48 kHz mono 16-bit only; anything else is rejected with an error naming actual and required format) and pumped caller-side in fixed 100 ms chunks, so replays are deterministic and faster than real time, and the live microphone path pays zero additional per-tick cost when playback is unused. On top of the seam: two new public data containers, `VoxrAudioTestCase` and `VoxrAudioTestSuiteAsset` (**Assets > Create > VoXR > Audio Test Suite**), pairing a fixture WAV with an expected intent, slots, and optional transcript; a byte-level 48 kHz-gated WAV reader; and, in the repository (stripped from the published package with the rest of `Tests~`), a 16-fixture TTS corpus at the measured Quest 3 microphone amplitude (peaks 0.04–0.4) covering all 11 demo-grammar intents plus homophone, filler, split-command, and silence cases, a reproducible `generate.sh` + phrase-manifest pipeline, and a PlayMode suite that replays every fixture through a real VOSK model and asserts the recognized command. The suite is a regression detector against committed audio, not an absolute recognition-quality benchmark — TTS speech is cleaner than human speech.

### Fixed

- When two patterns tie on match score, the parser now prefers the one that consumes more of the utterance, instead of keeping whichever the command asset happened to list first. Selection ran earliest start → highest score → most matched literals, with no term for the consumed span, so a command carrying both a bare pattern and a tailed sibling (`intercept track {track}` and `intercept track {track} {burn_level}`) tied at 1.0 with equal literal counts on an utterance that carried the tail. The tie fell through to registration order: if the bare pattern was listed first it won, and sequential extraction then matched the orphaned tail as a *separate* command — one spoken order silently delivered as two, with no warning and both marked successful, which defeats any policy the game applies at the command level. Array order in the asset was effectively load-bearing for correctness, and the workaround (listing patterns longest-first) collided with content keyed on `MatchedPatternIndex`. **Check your grammars for this:** the span term sits above the literal-count tie-break, so it decides more than the order-dependent ties it was added for — any two candidates that start at the same token and score equally now resolve by span, *including* pairs that literal count previously decided on its own, deterministically, whichever order they were declared in. A pattern with fewer literals but wider coverage now wins where it used to lose (`fire at {target}` over `fire at hotel` on "fire at hotel one"), and because the comparison runs across the whole command list this can change which **intent** fires, not just which pattern index (`go {place}` over `go {dir}` on "go north pole"). Grammars whose equal-scoring candidates always agree on span and literal count are unaffected. The span is measured over tokens a pattern actually matched, so a trailing `[unk]` cannot win a tie by being absorbed; literal count remains the next tie-break and registration order the final deterministic fallback. The same ordering is applied in the eager-flush scan, so the eager verdict still names the command a subsequent flush will fire — a tailed utterance that used to stop short of the buffer end now commits early instead of paying the full `bufferWindow`, and where the newly-preferred pattern is itself a prefix of a longer sibling the buffer now reports a complete-but-extendable match, which arms the shortened `prefixHoldSeconds` hold rather than the full window. ([#41](https://github.com/jinwoo1601/VoXR-Speech-Recognition/issues/41))

## [1.4.0] - 2026-07-28

### Added

- `prefixHoldSeconds` on `VoxrCommandRecogniser` — a separate, shorter hold for the "complete match, but still extendable" state under `eagerFlushOnCompleteMatch`. Eager flush deliberately refuses to commit a command that is a prefix of a longer one (`orient to heading {heading}` prefixes `orient to heading {heading} mark {mark_sign} {mark}`) or whose trailing slot could still grow, so the common plain form paid the *entire* buffer window — 2.0s of dead air on Quest 3 — for an ambiguity a continuing speaker resolves almost immediately. The refusal to commit early is correct and unchanged; what changes is how long the buffer then waits for that continuation. When `prefixHoldSeconds` is above 0, a held complete match flushes after that much silence instead of the full `bufferWindow`, so the plain form fires in ~0.5–0.8s while the extended form stays speakable. The hold only ever shortens the wait — values above `bufferWindow` are ignored — and it applies solely to a buffer that already parses as one complete, confident, whole-buffer command: partial speech mid-split-command, unmatched speech, and grammars too complex for the eager precompute to analyse all keep the full window. The verdict is re-derived on every VOSK result, so a continuation that arrives restores the full window for the rest of the utterance. Default `0` keeps the previous behaviour exactly. ([#32](https://github.com/jinwoo1601/VoXR-Speech-Recognition/issues/32))

### Changed

- The eager-flush eligibility analysis now consults slot vocabularies when deciding whether a pattern could be the opening of a longer one, instead of treating every slot position as compatible with anything. The old rule made a bare single-slot pattern (`{burn_level}`) a potential prefix of *every* longer pattern in the grammar — slot-vs-`close`, slot-vs-`intercept`, slot-vs-`decelerate` all counted as compatible — so a lone "coast" or "hard burn" paid the entire `bufferWindow` for an ambiguity that never existed. A slot facing a literal is now compatible only when some surface form of the slot actually begins with that word (a number sequence, only when the word is a digit word), so those patterns become eager-committable with no grammar change. Genuine holds are untouched: `decelerate` still waits for `decelerate {burn_level}`, and a value that really does start a longer pattern — `hard burn` against `hard burn now` — still holds. The vocabulary test is applied only where both patterns have provably consumed the same words (through their shared leading literals and the first element past them); past an earlier slot, which may have absorbed a different number of words on each side, the analysis stays conservative as before. Eager flush is still opt-in via `eagerFlushOnCompleteMatch`, and nothing changes when it is off. ([#33](https://github.com/jinwoo1601/VoXR-Speech-Recognition/issues/33))
- Words the parser's sliding start skips before a match now count against the match score, so a stray utterance whose tail resembles a short pattern can no longer execute it. Previously the skipped words were free: an out-of-vocabulary word snapped by VOSK onto an in-grammar one ("thrusters port" heard as "thrusters report") let the parser skip the unmatched leading word and match a lone one-word pattern at a full 1.0, and a mid-order stumble ("half closure three clicks on track") could fire the command buried in the middle of it. Both cases were observed in-headset. Each skipped in-grammar word is now added to the score denominator, weighted by the new `skippedWordPenalty` field on `VoxrCommandRecogniser` (default `1.0`), which makes the score the fraction of the utterance the pattern actually covers — a one-element pattern reached past one skipped word scores 0.5 and is rejected by the default `minScore` of 0.6, while a five-element pattern reached past the same skipped word scores 0.83 and still fires. The penalty is proportional, so it only bites patterns short enough to be swallowed whole by a stray sentence; longer commands still absorb false starts. `[unk]` tokens are never charged — tolerating out-of-grammar preamble and hesitation is what the sliding start is for — and counting restarts after each extracted command, so chained commands in one utterance do not penalise each other. Set `skippedWordPenalty` to `0` for the previous behaviour. `VoxrBatchTestRunner` takes the same value as an optional constructor argument so batch results keep predicting runtime behaviour. ([#31](https://github.com/jinwoo1601/VoXR-Speech-Recognition/issues/31))

### Fixed

- The `bufferWindow` Inspector tooltip recommended 1.5s on Quest 3 where every other source — `KNOWN_LIMITATIONS.md`, the README, the command-recognition and troubleshooting guides, and the command-recognition sample — recommends 2.0s. 1.5s sits below the ~1.9–2.1s inter-result gap measured on Quest 3, so the tooltip was recommending a window the project's own test matrices found insufficient; it appears to be a fossil of the v2.3 default rather than a Quest figure. The tooltip now says 2.0s and carries the measured gap plus the ~2.5s ceiling past which unrelated utterances start merging. Tooltip text only — no behaviour change, and the default remains 0.5s. ([#34](https://github.com/jinwoo1601/VoXR-Speech-Recognition/issues/34))

## [1.3.0] - 2026-07-25

### Added

- Command debug results are now auto-exported to disk when Play Mode ends. Every `VoxrMatchDiagnostics` entry produced during a session — the full session, not the debug window's rolling 20-entry history — is written to `<project>/Library/VoxrDebugLogs/session-<timestamp>.json`, with the path logged to the Console. Each entry records the transcript, per-word confidences, active command sets, and every match attempt (intent, pattern, score vs `minScore`, aggregate confidence vs `minConfidence`, extracted slots, reject reason, accepted flag). The file is self-describing — schema version, package/Unity versions, and a `readme` field — so scripts or LLM tooling can analyse recognition behaviour after a playtest without reading the SDK source. Always on, no setup, and the debug window does not need to be open; the ten most recent sessions are retained and a session with no matches writes nothing. Test runs are skipped so they cannot evict real playtest sessions from the retention pool — batch mode (`-runTests`, CI) via `Application.isBatchMode`, and in-editor Test Runner runs via a `TestRunnerApi` callback that lives in its own assembly, compiled only when `com.unity.test-framework` is installed so the package takes no dependency on it. Editor-only and compiled out of builds, like the diagnostics it records. ([#28](https://github.com/jinwoo1601/VoXR-Speech-Recognition/issues/28))

### Fixed

- Documentation stated a `bufferWindow` default of 1.5s, which had been lowered to 0.5s in code without the docs following. The API reference, command-recognition guide, troubleshooting guide, and `KNOWN_LIMITATIONS.md` now state the actual 0.5s default, preserving the v2.3 history and the empirical Quest 3 tuning notes. Note the Quest 3 *recommendation* still differs between the code tooltip (1.5s) and the docs/test data (2.0s); that remains open.

## [1.2.2] - 2026-05-29

### Added

- Opt-in eager flush for the command recogniser. When `eagerFlushOnCompleteMatch` is enabled, the utterance buffer fires a recognised command the moment the buffered speech forms a complete match that cannot be extended or completed by further speech, instead of always waiting out `bufferWindow`. This removes the buffer latency (default 0.5s; 1.5–2.0s on Quest 3) for clean single-breath commands while preserving split-command recovery: a command that is a prefix of a longer one — or whose trailing slot could still grow (multi-word enumerated values, or a variable-length number sequence) — keeps waiting the full window. Split commands also fire as soon as their second half completes. Off by default, so existing timing is unchanged. One behavioural note when enabled: a terminal command marked `RequiresConfirmation` now enters its pending/confirmation state with zero added latency (it still does not fire until confirmed). ([#25](https://github.com/jinwoo1601/VoXR-Speech-Recognition/issues/25))

## [1.2.1] - 2026-05-28

### Fixed

- Command match scores no longer penalize valid utterances that take advantage of optional tokens. `VoxrCommandParser` previously normalized the raw score by the static pattern length, so an omitted `?word`/`{?slot}` optional still counted toward the denominator (dropping the score even though optionality permits omission), and a spoken optional literal could never reach a perfect 1.0. The denominator is now dynamic — required elements always count, optional elements only when actually matched — so a perfect match scores 1.0 whether or not its optionals were uttered. The same fix is applied to follow-up scoring (`ScoreFollowUp`) so initial and pending-command scores stay consistent. Short patterns with optionals (e.g. `["go", "?now"]` said as just "go") are no longer pushed below the default `minScore` of 0.6. ([#21](https://github.com/jinwoo1601/VoXR-Speech-Recognition/issues/21))

## [1.2.0] - 2026-04-29

### Changed

- Recognition result polling now parses VOSK JSON directly from the native UTF-8 buffer via `ReadOnlySpan<byte>`, eliminating the per-poll `Marshal.PtrToStringUTF8` allocation and downstream `Substring` allocations inside `VoxrJsonParser`. On a typical partial flow (5–20 polls/sec) only the leaf string returned to consumers is allocated; the rest of the parse path is zero-alloc. Reduces frame-time variance during recognition on Quest, where main-thread GC was the dominant source of recognition-related hitches.
- `EditorMicBackend` no longer queues result strings — libvosk's null-terminated result buffer is dispatched inline through a delegate that takes `ReadOnlySpan<byte>`, matching the Android path. Partial dedupe switched from `string` equality to byte-buffer `SequenceEqual`.
- Runtime asmdef now sets `allowUnsafeCode: true` so the new span helpers (`BridgeNative.SpanFromPtr` / `SpanFromNullTerminated`) can wrap raw native pointers.

### Breaking Changes

- **`vosk_bridge_get_result` native ABI changed** — added a trailing `int* out_length` so the C# side can build a span without a separate `strlen` scan. The bundled `Runtime/Plugins/Android/arm64-v8a/libvosk-bridge.so` is rebuilt against the new signature; out-of-tree consumers of the native bridge must rebuild.

## [1.1.0] - 2026-04-28

### Removed

- `VoxrAlternative` struct (`VoXR` namespace).
- `VoxrResult.Alternatives` field.
- `VoxrSpeechRecogniser.maxAlternatives` Inspector field.
- `vosk_recognizer_set_max_alternatives` P/Invoke binding from `VoxrNative`.
- `VoxrJsonParser.ParseAlternativesFromJson` (and its `FindMatchingDelimiter` helper).
- N-best alternatives panel and the `[n/a]` per-word confidence fallback in `VoxrDebugWindow`.
- `alternativesText` field, `UpdateAlternativesPanel`, and the alt-logging branch from the `BasicTranscription` sample (and matching scene UI: `AlternativesLabel`/`AlternativesText` GameObjects).
- `Tests~/Runtime/ParseAlternativesFromJsonTests.cs`; alternatives-specific test in `VoxrSpeechRecogniserInjectionTests.cs`.

### Breaking Changes

- **`VoxrSpeechRecogniser.InjectResult` signature shrunk** from `(string text, VoxrWord[] words, VoxrAlternative[] alternatives)` to `(string text, VoxrWord[] words)`. Positional 3-arg callers break at compile time; `InjectResult(text)` and `InjectResult(text, words)` callers are unaffected. Migration: drop the third argument.
- **`VoxrResult` constructor signature shrunk** from `(string text, VoxrWord[] words, VoxrAlternative[] alternatives)` to `(string text, VoxrWord[] words)`. Direct callers (uncommon — most consumers receive `VoxrResult` via `OnResult`) must drop the third argument.
- **`vosk_bridge_init` native ABI changed** — the trailing `int max_alternatives` parameter is gone. The bundled `Runtime/Plugins/Android/arm64-v8a/libvosk-bridge.so` is rebuilt against the new signature; out-of-tree consumers of the native bridge must rebuild.
- Reading `result.Alternatives` no longer compiles. Migration: delete those accesses; use `result.Words` for per-word data instead.

## [1.0.0] - 2026-04-26

### Added

- Bundled prebuilt Windows VOSK DLLs (`libvosk.dll` plus the three MinGW runtime deps `libgcc_s_seh-1.dll`, `libstdc++-6.dll`, `libwinpthread-1.dll`) under `Runtime/Plugins/x86_64/`. The Editor live-mic backend on Windows now works out of the box — no manual download from alphacep/vosk-api required.

### Changed

- **First stable release.** The public API surface (`VoxrSpeechRecogniser`, `VoxrCommandRecogniser`, `VoxrPushToTalkController`, command/slot definitions, asset types, error codes) is now committed for the v1.x series. Breaking changes will require a v2.x major bump.
- Excluded internal test sources from package import by renaming `Tests/` to `Tests~/`. Tests remain in the repository for development but no longer load in consumer projects.
- Stripped `NativeBridge~/` (C++ bridge sources) and `Tests~/` from the published `v1.0.0` tag. Sources remain on `main` for ongoing development; consumer-facing distributions ship from a `release/1.0.0` branch.

## [0.17.0] - 2026-04-25

### Added

- `VoxrPushToTalkController` MonoBehaviour and `VoxrListeningMode` enum — hold-to-talk gating with runtime-switchable continuous mode, optional command-recogniser integration, `UnityEvent` hooks, and a guard for the Android mic-permission race. See [Push-to-Talk guide](Documentation~/push-to-talk.md).
- Push-to-talk sample at `Samples~/PushToTalk/` and `VoxrPushToTalkControllerTests` (16 Play Mode tests).
- `VoxrSlotDefinition.OneOf(name, params values)` factory method for concise enumerated-slot construction.

## [0.16.0] - 2026-04-13

### Changed

- Extracted `VoxrCommandRecogniser` responsibilities into single-purpose internal classes: `CommandDebouncer`, `CommandSetManager`, `DynamicSlotManager`, `GrammarManager`, `PendingCommandHandler`, `UtteranceBuffer`.
- Pulled JSON result/word/alternative parsing out of `VoxrSpeechRecogniser` into `VoxrJsonParser`.
- Unified `SplitSeparator` declarations — `VoxrCommandAsset` and `VoxrNumberParser` now use `VoxrCommandParser.SplitSeparator`.
- Simplified pending command resolution pipeline: all event dispatch goes through `InterpretResolution`; grammar drain based on outcome type instead of explicit flag.
- `UtteranceBuffer.Flush()` returns text only; word data accessed via `GetWordsSpan()` (zero-copy `ReadOnlySpan<VoxrWord>` via `CollectionsMarshal.AsSpan`).
- `VoxrCommandParser.ParseInternal()` accepts pre-split tokens and pre-built word-confidence dictionary, avoiding duplicate `string.Split` and `Dictionary` allocations per utterance.
- Pre-allocated reusable buffers replace per-utterance `List` allocations: `_matchSlotBuf`/`_bestSlotBuf` in `TryMatchScored`, `_acceptedBuf` in the recogniser, `_followUpSlotBuf`/`_unfilledBuf` in `PendingCommandHandler`, pooled `StringBuilder` in `UtteranceBuffer` and `TryMatchNumberSequence`, pooled word-confidence dictionary in `VoxrCommandParser`.
- Pre-computed `_slotNameCache` and `_optionalSlotElements` replace runtime `ExtractSlotName`/`IsOptionalSlot` calls in the match loop.
- `VoxrJsonParser.ParseAlternativesFromJson` pre-counts depth-1 objects for exact-size array allocation, replacing `List<VoxrAlternative>`.
- Vocabulary confirm/cancel matching uses span-based `MatchPhraseAgainstTokens` instead of joining tokens into a temporary string.

### Fixed

- Removed unsound slot array pool (`BorrowSlotArray`/`ReturnSlotArray`) that had two escape bugs: accepted commands' slot arrays were returned to the pool after being fired via `OnCommandRecognised` (subscribers may retain references), and only two pending-slot references were tracked so a third command entering pending in one parse would silently corrupt live data.

## [0.15.0] - 2026-04-13

### Added

- Pending command system for partial match, confirmation, and follow-up slot-fill:
  - `AllowPartialMatch` on `VoxrCommandDefinition` — when a command matches with unfilled required slots, it enters pending state instead of being rejected. Follow-up speech fills the missing slots.
  - `RequiresConfirmation` on `VoxrCommandDefinition` — fully-matched commands enter pending state awaiting explicit voice confirmation before firing.
  - Commands that are both `AllowPartialMatch` and `RequiresConfirmation` go through two pending stages: first slot-fill, then confirmation.
  - Configurable `pendingTimeout` (default 5s) and `pendingTimeoutBehavior` (`Cancel` or `FireAsIs`) on `VoxrCommandRecogniser`.
  - Custom confirm/cancel vocabulary via `confirmVocabulary` and `cancelVocabulary` Inspector arrays. Defaults: confirm = "confirm", "affirmative", "yes", "go ahead", "do it"; cancel = "cancel", "abort", "negative", "belay that", "never mind".
  - Confirm/cancel vocabulary words are automatically merged into the VOSK grammar JSON.
- `VoxrCommandRecogniser` pending command events:
  - `OnCommandPending` — fires when a command enters pending state (partial match or awaiting confirmation).
  - `OnCommandConfirmed` — fires when a pending command is confirmed. Also fires `OnCommandRecognised` and `OnCommandsRecognised`.
  - `OnCommandCancelled` — fires when a pending command is cancelled (timeout, explicit cancel, or preempted by a new complete command).
- `VoxrCommandRecogniser` pending command API:
  - `HasPendingCommand` property — true if a command is currently in pending state.
  - `PendingCommand` property — the currently pending `VoxrCommand`, or null.
  - `CancelPendingCommand()` — programmatically cancels the pending command.
- `VoxrCommandRecogniser.RebuildGrammar()` defers grammar rebuild while a command is pending, draining the rebuild when the pending command resolves.
- `VoxrCommandParser.TryMatchSlotByName()` — internal method for follow-up slot-fill matching against specific slot names.
- `VoxrPendingTimeoutBehavior` enum (`Cancel`, `FireAsIs`).
- `VoxrPendingCommand`, `VoxrPendingReason`, `VoxrFollowUpVocabulary` internal types.
- `VoxrCommandAsset` exposes `allowPartialMatch` and `requiresConfirmation` Inspector fields.
- `VoxrDebugWindow` shows pending command state: intent, reason, filled/unfilled slots, and elapsed time.
- `VoxrPendingCommandTests` — 32 Play Mode tests covering partial match entry, follow-up slot-fill, confirmation flow, cancel vocabulary, timeout behaviours, preemption by new commands, dual partial+confirmation flow, custom vocabulary, and grammar rebuild deferral.

### Changed

- `VoxrCommandDefinition` constructor accepts optional `allowPartialMatch` and `requiresConfirmation` parameters. Existing two-parameter constructor is unchanged.
- `VoxrCommandRecogniser.Configure()` and `SetActiveSets()` cancel any active pending command before rebuilding.
- Grammar generation includes confirm/cancel vocabulary words so VOSK recognises them in grammar mode.

## [0.14.0] - 2026-04-12

### Added

- Dynamic slot value providers for runtime filtering of which slot values the parser accepts. Register a `Func<string[]>` per slot name to narrow the active values without modifying the VOSK grammar:
  - `RegisterSlotValueProvider(slotName, valueProvider)` — registers a function that controls which values of the named slot are accepted by the parser.
  - `UnregisterSlotValueProvider(slotName)` — removes a provider, reverting the slot to its full value set on the next rebuild.
  - `NotifySlotChanged()` — rebuilds the parser to reflect current provider results. Does not touch the grammar or VOSK recogniser.
  - `RebuildParser()` — explicit parser rebuild from current effective slots and active commands.
  - `RebuildGrammar()` — rebuilds and re-applies the VOSK grammar, performing the stop/set/start cycle when recognition is running.
- Value providers automatically filter aliases: aliases pointing to excluded canonical values are pruned from the parser. `NumberSequence` slots are unaffected by providers.
- `VoxrCommandParser.GenerateGrammarJson` static overload — generates grammar from explicit slot and command arrays without constructing a parser instance.
- `VoxrDynamicSlotTests` — 14 Edit Mode tests covering registration API, parser narrowing, alias filtering, buffer preservation, grammar independence, provider updates, error paths, and the register-without-notify contract.

## [0.13.0] - 2026-04-11

### Added

- Batch test runner for regression-testing command definitions after changes. Two interfaces:
  - `VoxrBatchTestRunner` — pure C# runner that instantiates a `VoxrCommandParser`, feeds test cases, applies threshold filtering, and compares against expected intents and slots. Works in Edit Mode without Play Mode or audio hardware; CI-safe.
  - `VoxrBatchTestWindow` Editor window (Window > VoXR > Batch Test Runner) — visual table with input/expected/actual/score/status columns, Run All and Re-run Failed buttons, per-row diagnostics expansion, CSV export, and JSON import/export.
- `VoxrTestCase` data class for test case authoring: input text, expected intent, expected slots, optional simulated word confidence, and description.
- `VoxrTestResult` and `VoxrBatchResults` result classes — per-case pass/fail with failure reason, plus `AllPassed` and `FailureSummary` for NUnit assertion integration.
- `VoxrTestSuiteAsset` ScriptableObject (Assets > Create > VoXR > Test Suite) for Inspector-based test case authoring with JSON import/export for portability.
- `VoxrBatchTestRunnerTests` — Edit Mode meta-tests verifying the runner correctly reports pass/fail for matching commands, intent mismatches, slot mismatches, threshold rejection, command sets, CSV export, and edge cases.

## [0.12.0] - 2026-04-10

### Added

- `VoxrDebugWindow` Editor window (Window > VoXR > Command Debug) for live command pipeline diagnostics during Play Mode. Two-panel layout: left panel shows audio level meters (pre/post-AGC RMS, AGC gain), partial result, final result text, per-word confidence bars, and n-best alternatives; right panel shows active command sets, last match breakdown with score/confidence threshold pass/fail, slot word positions with per-slot confidence, and a scrolling match history (last 20 entries). Bottom bar provides text injection for testing without a microphone, plus pause and clear controls.
- `VoxrMatchDiagnostics`, `VoxrMatchAttempt`, and `VoxrDiagnosticSlotMatch` diagnostic structs in `Runtime/Commands/VoxrMatchDiagnostics.cs` — Editor-only (`#if UNITY_EDITOR`) data captured per utterance by the command pipeline for the debug window to poll.
- `Jinwoo1601.VoXR.Editor` assembly definition for Editor-only code with a reference to the runtime assembly.

### Changed

- `VoxrCommandParser` now records matched pattern strings, slot word positions (start/end indices), and per-parse diagnostic entries behind `#if UNITY_EDITOR`. `UnkToken`, `SplitSeparator`, and `ComputeConfidence` visibility widened to `internal` for Editor assembly access.
- `VoxrCommandRecogniser` builds a `VoxrMatchDiagnostics` snapshot at the end of each parse cycle with per-attempt accept/reject reasons (score, confidence, debounce). Subscribes to `OnPartialResult` in Editor for live partial text display.
- `EditorMicBackend` exposes `PreAgcRms`, `PostAgcRms`, and `AgcGain` properties for the debug window's audio level meters.
- `VoxrSpeechRecogniser` exposes `EditorLastResult` (Editor-only) and audio level forwarding properties (`EditorPreAgcRms`, `EditorPostAgcRms`, `EditorAgcGain` — Windows Editor only).
- `VoxrCommandRecogniser.SpeechRecogniser` internal setter now manages event subscriptions (unsubscribes from old recogniser, subscribes to new) for Edit Mode test support.
- `EditorMicBackend.ComputeRms` visibility widened from `private` to `internal` for test access.
- `CommandDemo` sample stripped of verbose `Debug.Log` calls — event handlers are now minimal stubs.

### Fixed

- Debug window pause/resume now freezes the display with a snapshot and skips stale results on resume instead of jumping to the latest frame.
- Enter-key text injection works reliably — event is consumed before `TextField`, and `KeypadEnter` is accepted alongside `Return`.
- Word confidence column shows `[n/a]` when VOSK omits per-word `conf` (happens with `maxAlternatives > 0`) instead of a misleading 0% bar.
- `VoxrSpeechRecogniser` now always parses full result JSON in Editor builds even when `OnResult` has no subscribers, so the debug window receives word and alternative data.
- `ParseWordsFromJson` handles absent `"conf"` field with a -1 sentinel instead of defaulting to 0.

### Added (tests)

- `AudioMetricTests` — Edit Mode tests for `ComputeRms` (silence, DC, known-amplitude sine).
- `VoxrCommandParserDiagnosticTests` — verifies parser populates `DiagnosticEntries` with matched pattern, slot positions, and score.
- `VoxrCommandRecogniserDiagnosticTests` — end-to-end diagnostic struct population via `InjectText`, covering accept/reject reasons and slot match data.
- `VoxrMatchDiagnosticsTests` — struct-level tests for `VoxrMatchDiagnostics`, `VoxrMatchAttempt`, and `VoxrDiagnosticSlotMatch` defaults and field storage.

## [0.11.0] - 2026-04-09

### Added

- Live microphone capture in the Unity Editor on Windows. Developers can now test voice commands end-to-end without deploying to Quest 3. `VoxrSpeechRecogniser.StartRecognition()` transparently auto-routes to a managed `EditorMicBackend` when running in the Windows Editor — existing sample scenes and user code work unchanged. No public API changes.
- `Runtime/Dsp/Downsampler.cs` — C# port of the 15-tap FIR downsampler (48 kHz → 16 kHz) from the native bridge, with Edit Mode unit tests covering output count, silence, DC gain, reset, and phase continuity across calls.
- `Runtime/Dsp/Agc.cs` — C# port of the asymmetric EMA automatic gain control with tanh soft limiter, with Edit Mode unit tests for silence, loud/quiet convergence, extreme-input bounding, and reset behaviour.
- `Runtime/Native/VoxrNative.cs` — P/Invoke bindings for the upstream `libvosk.dll` desktop build, bound with `CallingConvention.Cdecl` to match the MinGW GCC ABI of the alphacep/vosk-api Windows releases.
- `Runtime/EditorMicBackend.cs` — Editor-only backend that wires `UnityEngine.Microphone` capture into the ported DSP and VOSK recognizer, fed synchronously from the main-thread `Update()` loop. `vosk_model_new` is wrapped in `Task.Run` to avoid a main-thread hitch during the 1–3 second model load.
- `Runtime/Plugins/x86_64/` folder with plugin importer meta files for `libvosk.dll` and three MinGW runtime DLLs (`libgcc_s_seh-1.dll`, `libstdc++-6.dll`, `libwinpthread-1.dll`). Meta files are configured for Editor-only loading on Windows x86_64 — explicitly excluded from Android, standalone Windows, Linux, and macOS builds.

### Changed

- `VoxrSpeechRecogniser` lifecycle methods (`IsInitialised`, `IsRecognising`, `InitialiseAsync`, `StartRecognitionInternal`, `StopRecognition`, `ResetRecogniser`, `SetGrammar`, `ReleaseNativeResources`, `Update`) are now `#if UNITY_EDITOR_WIN` / `#else` gated so that the Windows Editor path routes exclusively through `EditorMicBackend` and other platforms continue to use the existing `BridgeNative` calls with zero behavioural change.

### Notes

- The Android runtime behaviour is unchanged. All 45 v3.0 tests continue to pass unmodified.
- Standalone Windows / PCVR runtime builds remain explicitly unsupported in v3.1 — the architecture is intentionally "PCVR-ready" but scope was kept to Editor testing only. See the scope note in `v3-and-beyond-analysis.md`.
- The binary DLLs are not checked into the repository. Maintainers and developers must download `vosk-win64-*.zip` from https://github.com/alphacep/vosk-api/releases and drop the four DLLs into `Runtime/Plugins/x86_64/`. See `v3.1-editor-mic-plan.md` for step-by-step instructions.

## [0.10.0] - 2026-04-07

### Added

- `VoxrSpeechRecogniser.InjectResult(text, words, alternatives)` — fires `OnFinalResult` and `OnResult` events as if VOSK had recognised the text. Bypasses native bridge state for Editor testing, replay, and CI.
- `VoxrSpeechRecogniser.InjectPartialResult(text)` — fires `OnPartialResult`.
- `VoxrSpeechRecogniser.CreateSimulatedWords(text, confidence)` — generates `VoxrWord[]` with uniform confidence and sequential timing for threshold testing.
- `VoxrCommandRecogniser.InjectText(text, words)` — injects text into the full command pipeline (parser, threshold filter, buffer, debounce) as if it had arrived from VOSK.
- `VoxrCommandRecogniser.FlushPendingBuffer()` — immediately flushes any speech held in the utterance buffer. Useful for push-to-talk release, scene transitions, and synchronous test injection.
- Play Mode tests covering injection, threshold filtering, debounce, buffered-path flushing, and end-to-end speech-to-command wiring.
- Editor test matrix (`v3-test-matrix.md`) with 9 automated suites (145 tests) and 36 manual injection rows across 8 phases — 45/45 pass, no Quest device, native bridge, or model required. Verifies every v2.0–v2.5 feature category (literals, aliases, optional slots, NumberSequence, utterance buffer, sequential extraction, debounce, threshold filtering, command sets, asset authoring) is reachable via injection.

## [0.9.0] - 2026-04-06

### Added

- `VoxrSlotAsset` ScriptableObject for Inspector-based slot definition authoring. Create via Assets > Create > VoXR > Slot Definition.
- `VoxrCommandAsset` ScriptableObject for command definitions with human-readable pattern strings (e.g. `"launch {?quantity} {weapon} target {target}"`).
- `VoxrCommandSetAsset` ScriptableObject for grouping commands into named sets.
- Inspector authoring on `VoxrCommandRecogniser`: assign slot and command set assets directly in the Inspector for zero-code setup. Code-based `Configure()` takes priority.
- `initialActiveSetNames` field on `VoxrCommandRecogniser` for selecting which sets activate on startup when using Inspector authoring.
- Null-guard warnings in `VoxrCommandRecogniser.Awake()` and `VoxrCommandSetAsset.ToSet()` so missing references in inspector arrays are skipped with a clear warning instead of throwing.
- 20-asset Inspector authoring set under `Samples~/CommandRecognition/AssetAuthoring/` (6 slots, 11 commands, 3 sets) covering every slot type and pattern token form.
- `useInspectorAuthoring` toggle on `CommandDemo` to switch between code-based `Configure()` and asset-driven authoring without editing the script.
- Unit tests for all ScriptableObject-to-runtime-struct conversions.
- `KNOWN_LIMITATIONS.md` at the project root — single place to document VOSK acoustic, voice-recognition, and architecture limitations across all versions.
- Quest device test matrix (`v2.5-test-matrix.md`) with 44 on-device tests across 8 phases (Phase 0 editor-only) — 42 pass, 1 known limitation (4.5: VOSK "to" → "two"), 1 skipped (6.3: script execution order edge case).

### Known Limitations

- VOSK misrecognises "to" as "two" in `switch to weapons`. Asset tokenization is correct; this is an acoustic-model limitation. Use the `weapons mode` alternate pattern.
- `SetActiveSets()` triggers a grammar rebuild that briefly stops/restarts AudioCapture (~50ms gap). Speech in that window is dropped at the audio layer. Pause ~500ms after a mode switch before speaking the next command.
- See `KNOWN_LIMITATIONS.md` for full details.

## [0.8.0] - 2026-04-06

### Added

- `VoxrCommandSet` — named groups of command definitions for mode-specific grammar. Activate different command groups per game state to reduce grammar size and improve VOSK accuracy.
- `Configure(slots, sets)` overload on `VoxrCommandRecogniser` registers shared slots and named command sets without activating any.
- `SetActiveSets(params string[])` activates one or more sets, rebuilding the parser and grammar from only the active commands. Handles stop → set grammar → start if recognition is running.
- `SetActiveSet(string)` convenience method for single-set activation.
- `ActiveSetNames` property exposes currently active set names.
- Backwards compatible: existing `Configure(slots, commands)` (no sets) continues to work unchanged.
- `CommandDemo` sample updated with voice-triggered mode switching (weapons mode, navigation mode, all modes, disable all with 5-second auto-restore).
- Quest device test matrix (`v2.4-test-matrix.md`) with 58 tests across 11 phases — 57 pass, 1 known limitation.

### Known Limitations

- VOSK grammar bleed: out-of-grammar words adjacent to in-grammar words can corrupt slot extraction during sequential parsing (test 7.4). Accepted as a VOSK engine limitation.
- "all modes" misrecognized as "fall modes" when navigation set is active (VOSK prefers the nav keyword "fall"). Use "enable all" as a reliable alternative.

## [0.7.0] - 2026-04-06

### Added

- Utterance buffer (`bufferWindow`) merges consecutive VOSK results split by mid-command pauses before parsing. Recommended 2.0s on Quest 3.
- Sequential command extraction — multiple commands in a single utterance are parsed left-to-right (e.g., "cease fire launch missiles target hotel one" → `cease_fire` + `launch_weapon`).
- `OnCommandsRecognised` batch event fires a `VoxrCommand[]` array per utterance alongside per-command `OnCommandRecognised` events.
- Per-intent debounce (`commandCooldown`) suppresses rapid duplicate intents both across VOSK results and within a single parse batch.
- Quest device test matrix (`v2.3-test-matrix.md`) with 40 tests across 12 phases — 40/40 pass.

### Fixed

- Intra-batch debounce: duplicate intents found by sequential extraction within the same parse batch are now correctly suppressed. Previously debounce only applied across separate VOSK results.

## [0.6.0] - 2026-04-05

### Added

- `NumberSequence` slot type for digit-word commands (e.g., "heading two seven zero" → 270).
- `VoxrNumberParser` with `ParseDigitSequence` and `ParseCardinal` for converting spoken digit words to integers.
- `VoxrSlotDefinition.NumberSequence()` factory with configurable `minWords`/`maxWords` greedy matching.
- Digit vocabulary automatically merged into grammar JSON when NumberSequence slots are registered.
- Sample `set_heading` command with heading + optional elevation NumberSequence slots in `CommandDemo.cs`.
- Quest device test matrix (`v2.2-test-matrix.md`) with 40 tests across 11 phases — 31 pass, 1 fail (free speech homophone), 4 known limitations/skips.

### Known Limitations

- Mid-command pauses exceeding VOSK's VAD silence threshold split speech into independent utterances, preventing cross-utterance command matching (test 9.2). This is rare in grammar mode with crisp commands.
- Free speech mode: VOSK may transcribe digit homophones incorrectly ("two" → "to", "orient" → "korean"). Grammar mode is recommended for production use.

## [0.5.1] - 2026-04-05

### Fixed

- Confidence threshold bypass: commands with zero confidence (from `[unk]` preamble tokens) no longer slip past `minConfidence` gate. `ComputeConfidence` now scopes to matched tokens only and returns -1 when word data is unavailable; threshold guard changed from `> 0` to `>= 0`.
- Removed `?a` optional literal from sample launch pattern — single-character words are unreliable in VOSK grammar mode. The existing quantity alias (`"a" → "one"`) handles it via the slot path.
- Validation now warns on single-character alias keys (previously only checked direct slot values).

### Added

- Quest device test matrix (`v2.1-test-matrix.md`) with 35 tests across 7 phases: scored matching, sliding start, optional literals, aliases, threshold rejection, grammar/free-speech mode, and validation warnings. Results: 33/35 pass.

## [0.5.0] - 2026-04-02

### Added

- Scored matching replaces binary pass/fail — normalized 0.0–1.0 score per match.
- Sliding start position — tolerates preamble, hesitations, and false starts.
- Optional literal tokens (`?a`, `?to`, `?the`) in patterns — consumed if present, skipped if absent.
- Slot value aliases (`"jackals" → "jackal"`, `"a" → "one"`) on `VoxrSlotDefinition`.
- `minConfidence` and `minScore` threshold fields on `VoxrCommandRecogniser` to reject low-quality matches.
- `Score` field on `VoxrCommand` for match quality inspection.
- Definition-time validation warnings for uppercase, punctuation, and single-character slot values.
- `GetSlot()` debug warning when called with unregistered slot name.
- Alias and optional literal words included in generated grammar JSON.

### Changed

- `VoxrCommandParser` now uses scored matching with sliding start instead of binary greedy matching.
- `VoxrCommand` constructor takes additional `score` and optional `registeredSlotNames` parameters.

## [0.4.0] - 2026-04-02

### Added

- Command recognition system with intent and slot extraction (`VoxrCommandRecogniser`, `VoxrCommandParser`).
- Grammar-constrained VOSK parsing via `SetGrammar` native bridge call for high-confidence command matching.
- `VoxrCommandDefinition` and `VoxrSlotDefinition` ScriptableObjects for declarative command authoring.
- Optional slot support (`{?slotName}`) and multi-word slot values.
- Free-speech mode toggle on `VoxrCommandRecogniser` for unconstrained vocabulary with best-effort matching.
- `OnCommandRecognised` and `OnUnrecognisedSpeech` events.
- Command Recognition sample scene.
- Unit tests for command parser (`VoxrCommandParserTests`).

## [0.3.0] - 2026-04-01

### Added

- Per-word confidence scores and timing via new `OnResult` event and `VoxrResult`/`VoxrWord` structs.
- N-best alternative hypotheses via `maxAlternatives` inspector field and `VoxrAlternative` struct.
- `ParseAlternativesFromJson` with depth-aware JSON parsing for nested VOSK output.

### Changed

- Refactored `ParseTextFromJson` to delegate to shared `ParseStringValue` helper.
- Zero-alloc float parsing using `AsSpan` instead of `Substring`.
- `IsRecognising` P/Invoke sync now only fires when the result queue had activity.

## [0.2.0] - 2026-03-31

### Added

- Adaptive automatic gain control (AGC) with configurable target level (`micGainTargetDb`).
- Soft tanh saturation to prevent hard clipping.
- FIR low-pass downsampler (48 kHz → 16 kHz) with anti-aliasing.
- AudioRecord JNI fallback for Quest 3 (AAudio input delivers silence on Meta devices).
- Guard against invalid `sample_rate` and null `model_path` in native bridge.

### Changed

- Replaced hardcoded mic gain with adaptive AGC.
- Cleaned up AGC: fast tanh approximation, removed magic literals and double reset.

## [0.1.0] - 2026-03-30

### Added

- Offline speech-to-text via VOSK on Meta Quest (Android arm64).
- `VoxrSpeechRecogniser` MonoBehaviour with event-driven API.
- Two-tier native lifecycle: heavyweight init/destroy, lightweight start/stop.
- Async model extraction from StreamingAssets with atomic rename pattern.
- C++ bridge (`libvosk-bridge`) with AAudio capture and native recognition loop.
- Structured error codes for all failure modes.
- Basic Transcription sample scene.
