# VOSK XR Speech Recognition

Offline speech recognition and voice command parsing for Unity XR applications. Wraps the [VOSK](https://alphacephei.com/vosk/) toolkit behind a Unity-native C# API with native audio capture on Android arm64 and live microphone capture in the Unity Editor on Windows.

---

## Getting Started

- [Getting Started](getting-started.md) -- Installation, model setup, quick start examples, and the recognition lifecycle
- [Command Recognition](command-recognition.md) -- How utterances become commands: the full parsing pipeline from audio to events, including pending commands

## Guides

- [Command Sets](command-sets.md) -- Runtime mode switching with named command groups and grammar management
- [Inspector Authoring](inspector-authoring.md) -- Zero-code setup using ScriptableObject assets in the Unity Inspector
- [Editor Testing](editor-testing.md) -- Debug window, live microphone, text injection, and batch test runner
- [Push-to-Talk and Error Handling](push-to-talk.md) -- Push-to-talk pattern, error handling, and the error code reference

## API Reference

- [VoskSpeechRecogniser](api/speech-recogniser.md) -- Core speech recognition component: events, properties, methods
- [VoskCommandRecogniser](api/command-recogniser.md) -- Command parsing component: configuration, events, injection
- [Data Types](api/data-types.md) -- VoskResult, VoskWord, VoskCommand, and related structs
- [Command Definitions](api/command-definitions.md) -- VoskCommandDefinition, VoskSlotDefinition, VoskCommandSet
- [ScriptableObject Assets](api/scriptable-objects.md) -- VoskSlotAsset, VoskCommandAsset, VoskCommandSetAsset, VoskTestSuiteAsset
- [VoskNumberParser](api/number-parser.md) -- Digit word to integer conversion
- [VoskBridgeErrorCode](api/error-codes.md) -- Structured error codes for all failure modes
- [VoskBatchTestRunner](api/batch-test-runner.md) -- Regression-testing runner, test cases, batch results

## Support

- [Native Bridge](native-bridge.md) -- Building the C++ native bridge from source
- [Troubleshooting](troubleshooting.md) -- Platform support table, common issues, and solutions
- [Known Limitations](../KNOWN_LIMITATIONS.md) -- Full list of known constraints with repro steps, root causes, and workarounds
