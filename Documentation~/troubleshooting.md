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
- Open the [Command Debug Window](editor-testing.md) to see the match breakdown: for each command the parser extracted, the pattern that **won** selection, its score and confidence against the thresholds, and why it was accepted or rejected. Losing candidates are not shown -- a pattern's absence means it lost selection, not that it was never tried.
- For a pattern across a whole playtest rather than one utterance, read the [session debug log](editor-testing.md#session-debug-log) written to `Library/VoxrDebugLogs/` when Play Mode ends -- it records every match attempt of the session, so repeated near-threshold rejections are easy to spot.
- To interpret the numbers in either view -- what produced a given `score`, why that pattern won, which gate stopped it -- see [Matching and Scoring](scoring.md), whose [worked examples](scoring.md#7-worked-examples) trace three common outcomes end to end.

### A command fires but a slot value is missing

The utterance had the slot value in it, the command fired, and `GetSlot` returns `""`. The cause is a **bare sibling pattern out-ranking the slot-filled one** after a required function word was dropped. [Coverage](command-recognition.md#coverage) closes the common case -- the bare pattern is charged for the words it leaves unexplained, so the slot-filled form wins and the argument survives -- but it does not close all of it.

If you still see this, in this order:

- **Check whether the stranded value's first word begins another pattern.** The orphan run stops at the first token that could start a match, so the bare form is charged nothing and wins exactly as it did before. With `["hard", "stop"]` registered, "decelerate hard burn" fires bare `decelerate` at `1.00` again -- at the default `coverageWeight`. This is the residue coverage cannot reach.
- **Mark the droppable literal optional** (`"?by"` rather than `"by"`). This is the real fix: it reaches `1.00` rather than `0.67`, so it wins by more, and it wins in the residual case above where coverage alone does not. The parser warns about the shape at construction, naming the literal and the slot at risk -- and that warning was deliberately *not* narrowed when coverage shipped, for exactly this reason.
- **Check `coverageWeight` is not `0`.** Zeroing it turns coverage off entirely -- back to pre-#31 scoring, before skipped words cost anything -- and brings this bug with it.
- Full explanation, both costs of the swap, and the score arithmetic: [Never leave a required function word between a bare pattern and its slot](command-recognition.md#never-leave-a-required-function-word-between-a-bare-pattern-and-its-slot) and [worked example B](scoring.md#b-coverage-picks-the-pattern-that-explains-more).

### A command stopped firing after upgrading

It worked before, the transcript looks right, and the score in the log is lower than the pattern's own arithmetic predicts. This is [coverage](scoring.md#2-coverage): a command is now scored on how much of the utterance it accounts for, so a short pattern trailed by words the grammar cannot place is demoted -- "decelerate hard burn" against a lone `decelerate` pattern falls from `1.0` to `1 / (1 + 2)` = `0.33`, and "cease fire please" from `1.0` to `0.67`.

Work out `matched / elements` for the winning pattern. If the reported `score` is lower, the difference is the tokens outside the match.

- **If you had tuned `coverageWeight` down, check the value survived the upgrade.** It was named `skippedWordPenalty` before #65. The component's own value migrates, but a prefab-*instance* override of the old name may not -- and a lost override silently restores the `1.0` default, which produces exactly this symptom.
- Register the fuller phrasing as a sibling pattern, so the demotion has somewhere to land. This is the intended response.
- Bring natural trailing words into the grammar as optional literals (`?please`, `?now`).
- Blunter: lower `minScore`, or set `coverageWeight` below `1.0`. Setting it to `0` turns coverage off entirely -- back to pre-#31 scoring, and the discarded-argument bug above comes with it. **The two knobs have different lifetimes**, which matters if you are tuning live: `minScore` is read fresh on every parse, so an Inspector edit applies to the very next utterance. `coverageWeight` is captured when the parser is built, and nothing watches the field -- so a Play Mode edit does nothing until you call `RebuildParser()` (or `Configure`, `SetActiveSets`, `NotifySlotChanged`), or re-enter Play Mode. Drag it mid-session and you will wrongly conclude coverage was not the cause.
- Measured cases and the shapes with no user-level workaround: [Known Limitations](../KNOWN_LIMITATIONS.md), plus worked example [B2](scoring.md#b2-the-same-demotion-with-nowhere-to-land).

### A command scores ~0.50 and is rejected

A score near `0.50` with slots that *did* extract is the signature of exactly one dropped required literal on a **two-element** pattern -- `(0 + 1) / 2`. Two elements is the floor: half the evidence is genuinely ambiguous (`cease fire` heard as "fire" is a different command where `fire` is registered), so it stays rejected by design.

**On three or more elements this no longer happens.** One dropped required literal costs `1/N`, so a three-element pattern scores `2/3` = `0.67` and fires; the same drop on a seven-element pattern scores `6/7` = `0.86`. If you are seeing `~0.50` on a longer pattern, more than one element missed.

Lowering `minScore` is still the wrong fix (it lets genuinely partial matches through everywhere else). For the two-element case, lengthen the pattern or accept the ambiguity. See [Short patterns are disproportionately fragile](scoring.md#short-patterns-are-disproportionately-fragile).

### A command reports nothing at all, though it clearly part-matched

Distinct from a score rejection: there is no scored attempt in the log either, because the candidate never became one. A pattern that **missed more of its required elements than it matched** is refused admission before selection, whatever it would have scored -- so a very sparse partial match produces no result rather than a low-scoring one.

Count the pattern's required elements against how many the transcript actually supplied. Optional elements count toward neither side. See [Admission](scoring.md#admission-what-counts-as-a-candidate-at-all).

### Commands match but with wrong slot values

This typically occurs when VOSK mishears a word due to phonetic similarity. For example, "to" may be transcribed as "two", or "all" as "fall" when a phonetically similar word is prominent in the grammar.

- Check the raw transcript (via `OnUnrecognisedSpeech` or the Command Debug Window) to see what VOSK actually heard.
- Try grammar mode if you are in free speech mode -- constrained grammar greatly reduces homophone confusion.
- Add slot value aliases for common mishearings (e.g. `"a"` -> `"one"`).
- See [Known Limitations](../KNOWN_LIMITATIONS.md) for the full list of documented homophone issues with the small English model.

### Confidence shows -1.00

A confidence value of `-1.00` means **"no word data available"**, not "zero confidence." It occurs when VOSK provided no per-word confidence for the matched span -- most often because the utterance carried none at all (injected text), and occasionally because a buffered utterance matched on a segment that carried none. Commands with `-1` confidence bypass the `minConfidence` threshold and are accepted or rejected on pattern-match score alone. See [Matching and Scoring](scoring.md#minconfidence-default-04).

If you display confidence in a debug UI, treat `-1.00` as "n/a" rather than as a numeric value.

### Commands split across two results

VOSK's voice activity detector treats pauses as utterance boundaries and flushes an interim result. If the user pauses mid-command (e.g. "launch missiles" *pause* "target hotel one"), VOSK produces two separate transcripts and neither matches a complete pattern on its own.

The utterance buffer (`bufferWindow`) is designed for this case -- it merges consecutive results before parsing. The default is `0.5` (tuned for typical PC latency). On Quest 3, VOSK latency adds ~0.5--1.0s to inter-result gaps, so the 0.5s default is usually too short.

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
- [Matching and Scoring](scoring.md) -- The model behind `score`, `aggregateConfidence`, and every `rejectReason`
- [Push-to-Talk and Error Handling](push-to-talk.md) -- Error codes and the push-to-talk pattern
- [Editor Testing](editor-testing.md) -- Debug tools for diagnosing issues
- [Native Bridge](native-bridge.md) -- Building from source if you need to modify the native layer
