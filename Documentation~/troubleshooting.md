# Troubleshooting

Common issues, platform support details, and solutions for problems you may encounter with the SDK.

---

## Platform Support

| Platform | Status |
|----------|--------|
| Meta Quest 2/3/Pro (Android arm64) | Supported -- primary target, extensively tested |
| Other Android arm64 XR (Pico, Lynx) | Should work -- same native bridge, not yet device-tested |
| Windows Editor (x86_64) | Supported -- live mic + text injection for iteration |
| Standalone Windows (PCVR) | Not yet supported -- architecturally ready, deferred to a future release |
| macOS / Linux Editor | Text injection only -- no live mic backend |

---

## Common Issues

### "Model archive not found in StreamingAssets"

Ensure the model `.zip` is at `Assets/StreamingAssets/<modelName>.zip` where `<modelName>` matches the `modelRelativePath` field on the `VoxrSpeechRecogniser` component. The default path expects `vosk-model-small-en-us-0.15.zip`.

### "Microphone permission (RECORD_AUDIO) was not granted"

Add `RECORD_AUDIO` to your Android manifest or enable it in Player Settings > Android > Other Settings. The SDK requests the permission at runtime, but the manifest entry must be present for the request to succeed.

### No transcription output on Quest

- Verify the model extracted successfully -- check the `OnModelReady` event or `IsModelReady` property.
- Check logcat: `adb logcat -s "vosk-bridge:*" "Unity:*"`
- Ensure `RECORD_AUDIO` permission is granted.
- Quest 3 microphone gain is low by default. The AGC compensates, but verify `micGainTargetDb` is set (default `-18` dB).

### No transcription output in Editor

- Check the Console for VOSK model loading errors.
- Verify a microphone is connected and set as the default Windows input device.
- On macOS or Linux, the live mic backend is not available -- use the [Text Injection API](editor-testing.md) instead.

### Commands not matching

- Verify patterns and slot values are **lowercase**. VOSK outputs lowercase text.
- Check that grammar mode is active (`freeSpeechMode = false`).
- Lower `minScore` and `minConfidence` temporarily to see if matches are being filtered by thresholds.
- Use `OnUnrecognisedSpeech` to log raw transcripts and compare against your patterns.
- Open the [Command Debug Window](editor-testing.md) to see the full match breakdown: which patterns were tried, what score each received, and why they were accepted or rejected.

### Commands match but with wrong slot values

This typically occurs when VOSK mishears a word due to phonetic similarity. For example, "to" may be transcribed as "two", or "all" as "fall" when a phonetically similar word is prominent in the grammar.

- Check the raw transcript (via `OnUnrecognisedSpeech` or the Command Debug Window) to see what VOSK actually heard.
- Try grammar mode if you are in free speech mode -- constrained grammar greatly reduces homophone confusion.
- Add slot value aliases for common mishearings (e.g. `"a"` -> `"one"`).
- See [Known Limitations](../KNOWN_LIMITATIONS.md) for the full list of documented homophone issues with the small English model.

### Confidence shows -1.00

A confidence value of `-1.00` means **"no word data available"**, not "zero confidence." This occurs when the matched span of the transcript contains only `[unk]` tokens or VOSK did not provide per-word confidence data. Commands with `-1` confidence bypass the `minConfidence` threshold and are accepted or rejected on pattern-match score alone.

If you display confidence in a debug UI, treat `-1.00` as "n/a" rather than as a numeric value.

### Commands split across two results

VOSK's voice activity detector treats pauses as utterance boundaries and flushes an interim result. If the user pauses mid-command (e.g. "launch missiles" *pause* "target hotel one"), VOSK produces two separate transcripts and neither matches a complete pattern on its own.

The utterance buffer (`bufferWindow`) is designed for this case -- it merges consecutive results before parsing. On Quest 3, VOSK latency adds ~0.5--1.0s to inter-result gaps, so the default 1.5s window can be marginal.

- Set `bufferWindow = 2.0` for Quest 3 builds.
- Do not exceed ~2.5--3.0s or unrelated utterances may merge ("cross-command bleed").
- Encourage users to speak commands in one breath, or use push-to-talk to scope each recognition burst.

### "Native bridge library (libvosk-bridge) not found"

The native libraries are Android arm64 only. In the Editor, recognition routes through `EditorMicBackend` instead (Windows only). On macOS/Linux Editor, only text injection is available.

### False matches from noise or coughs

In grammar mode, VOSK must produce an in-vocabulary word for any audio input -- it has no "silence" output. Short noises can map to short grammar words like "on", "from", or "four".

- Use [push-to-talk](push-to-talk.md) to gate recognition to intentional speech.
- Increase `minConfidence` slightly -- noise-derived matches usually have confidence well below 0.5. Don't push it past ~0.5 or NumberSequence commands containing "two" will be rejected.
- Prefer longer, multi-token commands -- false triggers rarely produce more than one in-grammar word in a row.

### Set switching drops the first words of the next command

`SetActiveSets()` causes a ~50ms audio gap during grammar rebuild. Speech during this window is lost at the audio layer.

- Pause ~500ms after a mode switch before speaking the next command.
- Provide visual or audio feedback when the switch completes.
- See [Command Sets](command-sets.md) for the full explanation and the single-set gating alternative.

---

## Further Resources

- [Known Limitations](../KNOWN_LIMITATIONS.md) -- Full list of known constraints with repro steps, root causes, and workarounds
- [Push-to-Talk and Error Handling](push-to-talk.md) -- Error codes and the push-to-talk pattern
- [Editor Testing](editor-testing.md) -- Debug tools for diagnosing issues
- [Native Bridge](native-bridge.md) -- Building from source if you need to modify the native layer
