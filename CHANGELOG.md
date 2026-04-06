# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
