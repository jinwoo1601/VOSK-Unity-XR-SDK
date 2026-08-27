# VoXR Speech Recognition

Offline speech recognition and voice command parsing for Unity XR applications. Wraps the [VOSK](https://alphacephei.com/vosk/) toolkit behind a Unity-native C# API with native audio capture on Android arm64 and live microphone capture in the Unity Editor on Windows.

---

## Getting Started

- [Getting Started](getting-started.md) -- Installation, model setup, quick start examples, and the recognition lifecycle
- [Command Recognition](command-recognition.md) -- How utterances become commands: the full parsing pipeline from audio to events, pending commands, and what to do when two commands are too alike to tell apart

## Guides

- [Matching and Scoring](scoring.md) -- The score formula and its miss costs, coverage, selection order, the leading-required-miss bar, the two gates, eager-flush verdicts, and how to read a session log
- [Command Sets](command-sets.md) -- Runtime mode switching with named command groups and grammar management
- [Inspector Authoring](inspector-authoring.md) -- Zero-code setup using ScriptableObject assets in the Unity Inspector
- [Editor Testing](editor-testing.md) -- Debug window, session debug log, live microphone, text injection, and batch test runner
- [Push-to-Talk and Error Handling](push-to-talk.md) -- Push-to-talk pattern, error handling, and the error code reference

## API Reference

- [VoxrSpeechRecogniser](api/speech-recogniser.md) -- Core speech recognition component: events, properties, methods
- [VoxrCommandRecogniser](api/command-recogniser.md) -- Command parsing component: configuration, events, injection
- [Data Types](api/data-types.md) -- VoxrResult, VoxrWord, VoxrCommand, VoxrPendingAmbiguity, VoxrListeningMode, and related structs and enums
- [Command Definitions](api/command-definitions.md) -- VoxrCommandDefinition, VoxrSlotDefinition, VoxrCommandSet
- [ScriptableObject Assets](api/scriptable-objects.md) -- VoxrSlotAsset, VoxrCommandAsset, VoxrCommandSetAsset, VoxrTestSuiteAsset, VoxrAudioTestSuiteAsset
- [VoxrNumberParser](api/number-parser.md) -- Digit word to integer conversion
- [VoxrBridgeErrorCode](api/error-codes.md) -- Structured error codes for all failure modes
- [VoxrBatchTestRunner](api/batch-test-runner.md) -- Regression-testing runner, test cases, batch results

## Support

- [Native Bridge](native-bridge.md) -- Building the C++ native bridge from source
- [Troubleshooting](troubleshooting.md) -- Platform support table, common issues, and solutions: start here if the wrong command fires, or none does
- [Known Limitations](../KNOWN_LIMITATIONS.md) -- Full list of known constraints with repro steps, root causes, and workarounds
