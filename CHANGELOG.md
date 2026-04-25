# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.17.0] - 2026-04-25

### Added

- `VoskPushToTalkController` MonoBehaviour and `VoskListeningMode` enum — hold-to-talk gating with runtime-switchable continuous mode, optional command-recogniser integration, `UnityEvent` hooks, and a guard for the Android mic-permission race. See [Push-to-Talk guide](Documentation~/push-to-talk.md).
- Push-to-talk sample at `Samples~/PushToTalk/` and `VoskPushToTalkControllerTests` (16 Play Mode tests).
- `VoskSlotDefinition.OneOf(name, params values)` factory method for concise enumerated-slot construction.

## [0.16.0] - 2026-04-13

### Changed

- Extracted `VoskCommandRecogniser` responsibilities into single-purpose internal classes: `CommandDebouncer`, `CommandSetManager`, `DynamicSlotManager`, `GrammarManager`, `PendingCommandHandler`, `UtteranceBuffer`.
- Pulled JSON result/word/alternative parsing out of `VoskSpeechRecogniser` into `VoskJsonParser`.
- Unified `SplitSeparator` declarations — `VoskCommandAsset` and `VoskNumberParser` now use `VoskCommandParser.SplitSeparator`.
- Simplified pending command resolution pipeline: all event dispatch goes through `InterpretResolution`; grammar drain based on outcome type instead of explicit flag.
- `UtteranceBuffer.Flush()` returns text only; word data accessed via `GetWordsSpan()` (zero-copy `ReadOnlySpan<VoskWord>` via `CollectionsMarshal.AsSpan`).
- `VoskCommandParser.ParseInternal()` accepts pre-split tokens and pre-built word-confidence dictionary, avoiding duplicate `string.Split` and `Dictionary` allocations per utterance.
- Pre-allocated reusable buffers replace per-utterance `List` allocations: `_matchSlotBuf`/`_bestSlotBuf` in `TryMatchScored`, `_acceptedBuf` in the recogniser, `_followUpSlotBuf`/`_unfilledBuf` in `PendingCommandHandler`, pooled `StringBuilder` in `UtteranceBuffer` and `TryMatchNumberSequence`, pooled word-confidence dictionary in `VoskCommandParser`.
- Pre-computed `_slotNameCache` and `_optionalSlotElements` replace runtime `ExtractSlotName`/`IsOptionalSlot` calls in the match loop.
- `VoskJsonParser.ParseAlternativesFromJson` pre-counts depth-1 objects for exact-size array allocation, replacing `List<VoskAlternative>`.
- Vocabulary confirm/cancel matching uses span-based `MatchPhraseAgainstTokens` instead of joining tokens into a temporary string.

### Fixed

- Removed unsound slot array pool (`BorrowSlotArray`/`ReturnSlotArray`) that had two escape bugs: accepted commands' slot arrays were returned to the pool after being fired via `OnCommandRecognised` (subscribers may retain references), and only two pending-slot references were tracked so a third command entering pending in one parse would silently corrupt live data.

## [0.15.0] - 2026-04-13

### Added

- Pending command system for partial match, confirmation, and follow-up slot-fill:
  - `AllowPartialMatch` on `VoskCommandDefinition` — when a command matches with unfilled required slots, it enters pending state instead of being rejected. Follow-up speech fills the missing slots.
  - `RequiresConfirmation` on `VoskCommandDefinition` — fully-matched commands enter pending state awaiting explicit voice confirmation before firing.
  - Commands that are both `AllowPartialMatch` and `RequiresConfirmation` go through two pending stages: first slot-fill, then confirmation.
  - Configurable `pendingTimeout` (default 5s) and `pendingTimeoutBehavior` (`Cancel` or `FireAsIs`) on `VoskCommandRecogniser`.
  - Custom confirm/cancel vocabulary via `confirmVocabulary` and `cancelVocabulary` Inspector arrays. Defaults: confirm = "confirm", "affirmative", "yes", "go ahead", "do it"; cancel = "cancel", "abort", "negative", "belay that", "never mind".
  - Confirm/cancel vocabulary words are automatically merged into the VOSK grammar JSON.
- `VoskCommandRecogniser` pending command events:
  - `OnCommandPending` — fires when a command enters pending state (partial match or awaiting confirmation).
  - `OnCommandConfirmed` — fires when a pending command is confirmed. Also fires `OnCommandRecognised` and `OnCommandsRecognised`.
  - `OnCommandCancelled` — fires when a pending command is cancelled (timeout, explicit cancel, or preempted by a new complete command).
- `VoskCommandRecogniser` pending command API:
  - `HasPendingCommand` property — true if a command is currently in pending state.
  - `PendingCommand` property — the currently pending `VoskCommand`, or null.
  - `CancelPendingCommand()` — programmatically cancels the pending command.
- `VoskCommandRecogniser.RebuildGrammar()` defers grammar rebuild while a command is pending, draining the rebuild when the pending command resolves.
- `VoskCommandParser.TryMatchSlotByName()` — internal method for follow-up slot-fill matching against specific slot names.
- `VoskPendingTimeoutBehavior` enum (`Cancel`, `FireAsIs`).
- `VoskPendingCommand`, `VoskPendingReason`, `VoskFollowUpVocabulary` internal types.
- `VoskCommandAsset` exposes `allowPartialMatch` and `requiresConfirmation` Inspector fields.
- `VoskDebugWindow` shows pending command state: intent, reason, filled/unfilled slots, and elapsed time.
- `VoskPendingCommandTests` — 32 Play Mode tests covering partial match entry, follow-up slot-fill, confirmation flow, cancel vocabulary, timeout behaviours, preemption by new commands, dual partial+confirmation flow, custom vocabulary, and grammar rebuild deferral.

### Changed

- `VoskCommandDefinition` constructor accepts optional `allowPartialMatch` and `requiresConfirmation` parameters. Existing two-parameter constructor is unchanged.
- `VoskCommandRecogniser.Configure()` and `SetActiveSets()` cancel any active pending command before rebuilding.
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
- `VoskCommandParser.GenerateGrammarJson` static overload — generates grammar from explicit slot and command arrays without constructing a parser instance.
- `VoskDynamicSlotTests` — 14 Edit Mode tests covering registration API, parser narrowing, alias filtering, buffer preservation, grammar independence, provider updates, error paths, and the register-without-notify contract.

## [0.13.0] - 2026-04-11

### Added

- Batch test runner for regression-testing command definitions after changes. Two interfaces:
  - `VoskBatchTestRunner` — pure C# runner that instantiates a `VoskCommandParser`, feeds test cases, applies threshold filtering, and compares against expected intents and slots. Works in Edit Mode without Play Mode or audio hardware; CI-safe.
  - `VoskBatchTestWindow` Editor window (Window > VOSK XR > Batch Test Runner) — visual table with input/expected/actual/score/status columns, Run All and Re-run Failed buttons, per-row diagnostics expansion, CSV export, and JSON import/export.
- `VoskTestCase` data class for test case authoring: input text, expected intent, expected slots, optional simulated word confidence, and description.
- `VoskTestResult` and `VoskBatchResults` result classes — per-case pass/fail with failure reason, plus `AllPassed` and `FailureSummary` for NUnit assertion integration.
- `VoskTestSuiteAsset` ScriptableObject (Assets > Create > VOSK XR > Test Suite) for Inspector-based test case authoring with JSON import/export for portability.
- `VoskBatchTestRunnerTests` — Edit Mode meta-tests verifying the runner correctly reports pass/fail for matching commands, intent mismatches, slot mismatches, threshold rejection, command sets, CSV export, and edge cases.

## [0.12.0] - 2026-04-10

### Added

- `VoskDebugWindow` Editor window (Window > VOSK XR > Command Debug) for live command pipeline diagnostics during Play Mode. Two-panel layout: left panel shows audio level meters (pre/post-AGC RMS, AGC gain), partial result, final result text, per-word confidence bars, and n-best alternatives; right panel shows active command sets, last match breakdown with score/confidence threshold pass/fail, slot word positions with per-slot confidence, and a scrolling match history (last 20 entries). Bottom bar provides text injection for testing without a microphone, plus pause and clear controls.
- `VoskMatchDiagnostics`, `VoskMatchAttempt`, and `VoskDiagnosticSlotMatch` diagnostic structs in `Runtime/Commands/VoskMatchDiagnostics.cs` — Editor-only (`#if UNITY_EDITOR`) data captured per utterance by the command pipeline for the debug window to poll.
- `Jinwoo1601.VoskXR.Editor` assembly definition for Editor-only code with a reference to the runtime assembly.

### Changed

- `VoskCommandParser` now records matched pattern strings, slot word positions (start/end indices), and per-parse diagnostic entries behind `#if UNITY_EDITOR`. `UnkToken`, `SplitSeparator`, and `ComputeConfidence` visibility widened to `internal` for Editor assembly access.
- `VoskCommandRecogniser` builds a `VoskMatchDiagnostics` snapshot at the end of each parse cycle with per-attempt accept/reject reasons (score, confidence, debounce). Subscribes to `OnPartialResult` in Editor for live partial text display.
- `EditorMicBackend` exposes `PreAgcRms`, `PostAgcRms`, and `AgcGain` properties for the debug window's audio level meters.
- `VoskSpeechRecogniser` exposes `EditorLastResult` (Editor-only) and audio level forwarding properties (`EditorPreAgcRms`, `EditorPostAgcRms`, `EditorAgcGain` — Windows Editor only).
- `VoskCommandRecogniser.SpeechRecogniser` internal setter now manages event subscriptions (unsubscribes from old recogniser, subscribes to new) for Edit Mode test support.
- `EditorMicBackend.ComputeRms` visibility widened from `private` to `internal` for test access.
- `CommandDemo` sample stripped of verbose `Debug.Log` calls — event handlers are now minimal stubs.

### Fixed

- Debug window pause/resume now freezes the display with a snapshot and skips stale results on resume instead of jumping to the latest frame.
- Enter-key text injection works reliably — event is consumed before `TextField`, and `KeypadEnter` is accepted alongside `Return`.
- Word confidence column shows `[n/a]` when VOSK omits per-word `conf` (happens with `maxAlternatives > 0`) instead of a misleading 0% bar.
- `VoskSpeechRecogniser` now always parses full result JSON in Editor builds even when `OnResult` has no subscribers, so the debug window receives word and alternative data.
- `ParseWordsFromJson` handles absent `"conf"` field with a -1 sentinel instead of defaulting to 0.

### Added (tests)

- `AudioMetricTests` — Edit Mode tests for `ComputeRms` (silence, DC, known-amplitude sine).
- `VoskCommandParserDiagnosticTests` — verifies parser populates `DiagnosticEntries` with matched pattern, slot positions, and score.
- `VoskCommandRecogniserDiagnosticTests` — end-to-end diagnostic struct population via `InjectText`, covering accept/reject reasons and slot match data.
- `VoskMatchDiagnosticsTests` — struct-level tests for `VoskMatchDiagnostics`, `VoskMatchAttempt`, and `VoskDiagnosticSlotMatch` defaults and field storage.

## [0.11.0] - 2026-04-09

### Added

- Live microphone capture in the Unity Editor on Windows. Developers can now test voice commands end-to-end without deploying to Quest 3. `VoskSpeechRecogniser.StartRecognition()` transparently auto-routes to a managed `EditorMicBackend` when running in the Windows Editor — existing sample scenes and user code work unchanged. No public API changes.
- `Runtime/Dsp/Downsampler.cs` — C# port of the 15-tap FIR downsampler (48 kHz → 16 kHz) from the native bridge, with Edit Mode unit tests covering output count, silence, DC gain, reset, and phase continuity across calls.
- `Runtime/Dsp/Agc.cs` — C# port of the asymmetric EMA automatic gain control with tanh soft limiter, with Edit Mode unit tests for silence, loud/quiet convergence, extreme-input bounding, and reset behaviour.
- `Runtime/Native/VoskNative.cs` — P/Invoke bindings for the upstream `libvosk.dll` desktop build, bound with `CallingConvention.Cdecl` to match the MinGW GCC ABI of the alphacep/vosk-api Windows releases.
- `Runtime/EditorMicBackend.cs` — Editor-only backend that wires `UnityEngine.Microphone` capture into the ported DSP and VOSK recognizer, fed synchronously from the main-thread `Update()` loop. `vosk_model_new` is wrapped in `Task.Run` to avoid a main-thread hitch during the 1–3 second model load.
- `Runtime/Plugins/x86_64/` folder with plugin importer meta files for `libvosk.dll` and three MinGW runtime DLLs (`libgcc_s_seh-1.dll`, `libstdc++-6.dll`, `libwinpthread-1.dll`). Meta files are configured for Editor-only loading on Windows x86_64 — explicitly excluded from Android, standalone Windows, Linux, and macOS builds.

### Changed

- `VoskSpeechRecogniser` lifecycle methods (`IsInitialised`, `IsRecognising`, `InitialiseAsync`, `StartRecognitionInternal`, `StopRecognition`, `ResetRecogniser`, `SetGrammar`, `ReleaseNativeResources`, `Update`) are now `#if UNITY_EDITOR_WIN` / `#else` gated so that the Windows Editor path routes exclusively through `EditorMicBackend` and other platforms continue to use the existing `BridgeNative` calls with zero behavioural change.

### Notes

- The Android runtime behaviour is unchanged. All 45 v3.0 tests continue to pass unmodified.
- Standalone Windows / PCVR runtime builds remain explicitly unsupported in v3.1 — the architecture is intentionally "PCVR-ready" but scope was kept to Editor testing only. See the scope note in `v3-and-beyond-analysis.md`.
- The binary DLLs are not checked into the repository. Maintainers and developers must download `vosk-win64-*.zip` from https://github.com/alphacep/vosk-api/releases and drop the four DLLs into `Runtime/Plugins/x86_64/`. See `v3.1-editor-mic-plan.md` for step-by-step instructions.

## [0.10.0] - 2026-04-07

### Added

- `VoskSpeechRecogniser.InjectResult(text, words, alternatives)` — fires `OnFinalResult` and `OnResult` events as if VOSK had recognised the text. Bypasses native bridge state for Editor testing, replay, and CI.
- `VoskSpeechRecogniser.InjectPartialResult(text)` — fires `OnPartialResult`.
- `VoskSpeechRecogniser.CreateSimulatedWords(text, confidence)` — generates `VoskWord[]` with uniform confidence and sequential timing for threshold testing.
- `VoskCommandRecogniser.InjectText(text, words)` — injects text into the full command pipeline (parser, threshold filter, buffer, debounce) as if it had arrived from VOSK.
- `VoskCommandRecogniser.FlushPendingBuffer()` — immediately flushes any speech held in the utterance buffer. Useful for push-to-talk release, scene transitions, and synchronous test injection.
- Play Mode tests covering injection, threshold filtering, debounce, buffered-path flushing, and end-to-end speech-to-command wiring.
- Editor test matrix (`v3-test-matrix.md`) with 9 automated suites (145 tests) and 36 manual injection rows across 8 phases — 45/45 pass, no Quest device, native bridge, or model required. Verifies every v2.0–v2.5 feature category (literals, aliases, optional slots, NumberSequence, utterance buffer, sequential extraction, debounce, threshold filtering, command sets, asset authoring) is reachable via injection.

## [0.9.0] - 2026-04-06

### Added

- `VoskSlotAsset` ScriptableObject for Inspector-based slot definition authoring. Create via Assets > Create > VOSK XR > Slot Definition.
- `VoskCommandAsset` ScriptableObject for command definitions with human-readable pattern strings (e.g. `"launch {?quantity} {weapon} target {target}"`).
- `VoskCommandSetAsset` ScriptableObject for grouping commands into named sets.
- Inspector authoring on `VoskCommandRecogniser`: assign slot and command set assets directly in the Inspector for zero-code setup. Code-based `Configure()` takes priority.
- `initialActiveSetNames` field on `VoskCommandRecogniser` for selecting which sets activate on startup when using Inspector authoring.
- Null-guard warnings in `VoskCommandRecogniser.Awake()` and `VoskCommandSetAsset.ToSet()` so missing references in inspector arrays are skipped with a clear warning instead of throwing.
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

- `VoskCommandSet` — named groups of command definitions for mode-specific grammar. Activate different command groups per game state to reduce grammar size and improve VOSK accuracy.
- `Configure(slots, sets)` overload on `VoskCommandRecogniser` registers shared slots and named command sets without activating any.
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
- `OnCommandsRecognised` batch event fires a `VoskCommand[]` array per utterance alongside per-command `OnCommandRecognised` events.
- Per-intent debounce (`commandCooldown`) suppresses rapid duplicate intents both across VOSK results and within a single parse batch.
- Quest device test matrix (`v2.3-test-matrix.md`) with 40 tests across 12 phases — 40/40 pass.

### Fixed

- Intra-batch debounce: duplicate intents found by sequential extraction within the same parse batch are now correctly suppressed. Previously debounce only applied across separate VOSK results.

## [0.6.0] - 2026-04-05

### Added

- `NumberSequence` slot type for digit-word commands (e.g., "heading two seven zero" → 270).
- `VoskNumberParser` with `ParseDigitSequence` and `ParseCardinal` for converting spoken digit words to integers.
- `VoskSlotDefinition.NumberSequence()` factory with configurable `minWords`/`maxWords` greedy matching.
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
- Slot value aliases (`"jackals" → "jackal"`, `"a" → "one"`) on `VoskSlotDefinition`.
- `minConfidence` and `minScore` threshold fields on `VoskCommandRecogniser` to reject low-quality matches.
- `Score` field on `VoskCommand` for match quality inspection.
- Definition-time validation warnings for uppercase, punctuation, and single-character slot values.
- `GetSlot()` debug warning when called with unregistered slot name.
- Alias and optional literal words included in generated grammar JSON.

### Changed

- `VoskCommandParser` now uses scored matching with sliding start instead of binary greedy matching.
- `VoskCommand` constructor takes additional `score` and optional `registeredSlotNames` parameters.

## [0.4.0] - 2026-04-02

### Added

- Command recognition system with intent and slot extraction (`VoskCommandRecogniser`, `VoskCommandParser`).
- Grammar-constrained VOSK parsing via `SetGrammar` native bridge call for high-confidence command matching.
- `VoskCommandDefinition` and `VoskSlotDefinition` ScriptableObjects for declarative command authoring.
- Optional slot support (`{?slotName}`) and multi-word slot values.
- Free-speech mode toggle on `VoskCommandRecogniser` for unconstrained vocabulary with best-effort matching.
- `OnCommandRecognised` and `OnUnrecognisedSpeech` events.
- Command Recognition sample scene.
- Unit tests for command parser (`VoskCommandParserTests`).

## [0.3.0] - 2026-04-01

### Added

- Per-word confidence scores and timing via new `OnResult` event and `VoskResult`/`VoskWord` structs.
- N-best alternative hypotheses via `maxAlternatives` inspector field and `VoskAlternative` struct.
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
- `VoskSpeechRecogniser` MonoBehaviour with event-driven API.
- Two-tier native lifecycle: heavyweight init/destroy, lightweight start/stop.
- Async model extraction from StreamingAssets with atomic rename pattern.
- C++ bridge (`libvosk-bridge`) with AAudio capture and native recognition loop.
- Structured error codes for all failure modes.
- Basic Transcription sample scene.
