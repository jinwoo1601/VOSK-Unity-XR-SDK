# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
