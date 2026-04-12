# Command Recognition

This guide explains how the SDK turns raw speech into structured commands. It covers the full parsing pipeline, pattern syntax, slot types, scoring, and the choice between grammar-constrained and free-speech recognition.

---

## Overview: How an Utterance Becomes a Command

When the user speaks, the audio passes through a multi-stage pipeline before your `OnCommandRecognised` handler fires. Understanding these stages helps you diagnose matching issues and tune the system effectively.

```
Microphone Audio
    |
    v
VOSK Recogniser (speech-to-text)
    |  produces a transcript string + per-word confidence
    v
Utterance Buffer
    |  merges consecutive VOSK results within bufferWindow seconds
    |  (handles mid-command pauses that VOSK splits into separate utterances)
    v
Parser (pattern match + scoring)
    |  tries each command pattern against the transcript
    |  uses sliding start to skip preamble/filler words
    |  extracts slot values, computes normalised score (0.0-1.0)
    v
Sequential Extraction
    |  extracts multiple commands left-to-right from a single utterance
    |  ("cease fire launch missiles target hotel one" -> two commands)
    v
Threshold Filter
    |  rejects commands below minScore or minConfidence
    |  confidence of -1 (no data) bypasses the minConfidence check
    v
Debounce
    |  suppresses duplicate intents within commandCooldown seconds
    v
Events: OnCommandRecognised, OnCommandsRecognised, OnUnrecognisedSpeech
```

Each stage is configurable. The most common tuning points are `bufferWindow` (how long to wait for split speech), `minScore` / `minConfidence` (quality thresholds), and `commandCooldown` (debounce window).

---

## Patterns and Slots

Commands are defined as token arrays. **Literal tokens** must appear in the speech exactly as written. **Slot tokens** (wrapped in `{}`) match against registered slot values.

```csharp
// Pattern: "launch {weapon} target {target}"
// Matches: "launch missiles target alpha one"
// Extracts: weapon="missiles", target="alpha one"
new VoskCommandDefinition("launch_weapon",
    new[] { new[] { "launch", "{weapon}", "target", "{target}" } })
```

Multi-word slot values (e.g. `"alpha one"`) are consumed greedily -- the parser tries longer matches first to avoid partial matches.

A command can have multiple alternative patterns, each representing a different way the user might phrase the same intent:

```csharp
new VoskCommandDefinition("launch_weapon", new[] {
    new[] { "launch", "{?quantity}", "{weapon}", "target", "{target}" },
    new[] { "fire", "{weapon}", "at", "{target}" },
})
```

---

## Optional Slots

Prefix a slot reference with `?` to make it optional. The parser consumes it if present and skips it if absent -- both phrasings match the same intent.

```csharp
// "{?quantity}" is optional
new[] { "launch", "{?quantity}", "{weapon}", "target", "{target}" }
// Matches both: "launch missiles target alpha one"
//           and: "launch two missiles target alpha one"
```

Optional literal tokens also work: `"?the"`, `"?a"`. However, single-character words are unreliable in VOSK grammar mode -- the acoustic model frequently misrecognises or drops them. Prefer slot value aliases instead (see below).

---

## Scored Matching

Every match produces a normalised **score** (0.0--1.0) that indicates how well the transcript covers the pattern. The parser uses a sliding start to tolerate preamble, hesitations, and false starts -- the score reflects the quality of the best-positioned match.

Two independent thresholds control what gets through:

```csharp
commandRecogniser.minScore = 0.6f;       // Reject low-quality pattern matches
commandRecogniser.minConfidence = 0.4f;   // Reject low VOSK word confidence
```

**Score** (`VoskCommand.Score`) is computed by the parser based on how many pattern tokens were satisfied. A perfect match with all required and optional tokens scores 1.0. Missing optional tokens reduce the score proportionally.

**Confidence** (`VoskCommand.Confidence`) is the minimum per-word VOSK acoustic confidence across matched tokens. This reflects how certain VOSK was about the words it heard. A value of `-1` means no word-level data was available (e.g. the transcript contained only `[unk]` tokens), which bypasses the `minConfidence` check entirely -- the command is accepted or rejected on score alone.

When tuning thresholds:
- Start with the defaults (`minScore=0.6`, `minConfidence=0.4`) and adjust based on testing.
- Don't push `minConfidence` above `0.5` unless you've verified your vocabulary avoids "two" and other low-confidence words (see [Known Limitations](../KNOWN_LIMITATIONS.md)).
- Use the [Batch Test Runner](editor-testing.md) to regression-test threshold changes.

---

## Slot Value Aliases

Map variant words to canonical values so the parser normalises them automatically:

```csharp
var quantity = new VoskSlotDefinition("quantity",
    new[] { "one", "two", "three", "all" },
    new Dictionary<string, string> { { "a", "one" }, { "jackals", "jackal" } });
```

When VOSK transcribes `"a"`, the alias resolves it to `"one"` in the extracted slot value. Aliases are included in the generated grammar JSON, so VOSK knows to listen for the variant words.

**Validation:** The parser warns at configure time about single-character slot values and alias keys, as these are unreliable in VOSK grammar mode. Prefer longer, phonetically distinct alternatives.

---

## NumberSequence Slots

Parse spoken digit words into concatenated integers for headings, frequencies, grid coordinates, and similar numeric commands:

```csharp
var heading = VoskSlotDefinition.NumberSequence("heading", minWords: 1, maxWords: 3);

// "heading two seven zero" -> heading="270"
// "heading one eight"      -> heading="18"
```

The parser greedily consumes consecutive digit words ("zero" through "nine") within the configured `minWords`/`maxWords` range. Digit vocabulary is automatically merged into the grammar JSON.

Use `VoskNumberParser.ParseDigitSequence()` in your command handler to convert the extracted string to an integer:

```csharp
commandRecogniser.OnCommandRecognised += cmd =>
{
    if (cmd.Intent == "set_heading")
    {
        int heading = VoskNumberParser.ParseDigitSequence(cmd.GetSlot("heading"));
        Debug.Log($"Heading: {heading}");
    }
};
```

`VoskNumberParser` also provides `ParseCardinal()` for natural-language numbers (`"fifteen"` -> `15`, `"two hundred"` -> `200`).

---

## Utterance Buffer

VOSK's voice activity detector can split mid-command pauses into separate utterances. The utterance buffer merges consecutive VOSK results within `bufferWindow` seconds before parsing.

```csharp
commandRecogniser.bufferWindow = 2.0f; // Recommended for Quest 3
```

If the speaker says "launch missiles" *pause* "target hotel one" and both results arrive within the window, they are concatenated and parsed as a single command.

**Tuning:** The default is 1.5s. Quest 3 VOSK latency adds ~0.5--1.0s to inter-result gaps, so 2.0s is more reliable on device. Don't exceed ~2.5--3.0s or unrelated utterances may merge ("cross-command bleed").

---

## Sequential Extraction

Multiple commands in a single utterance are extracted left-to-right:

```
"cease fire launch missiles target hotel one"
  -> cease_fire + launch_weapon(weapon=missiles, target=hotel one)
```

Both `OnCommandRecognised` (fired once per command) and `OnCommandsRecognised` (fired once with the full batch array) events fire.

---

## Debounce

Per-intent debounce suppresses duplicate firings within `commandCooldown` seconds. This applies both across separate VOSK results and within a single parse batch from sequential extraction.

```csharp
commandRecogniser.commandCooldown = 0.3f; // Default: 0.3s
```

If the user says the same command twice quickly (or VOSK produces overlapping results), the second firing is suppressed.

---

## Grammar Mode vs Free Speech

By default, `VoskCommandRecogniser` constrains VOSK's decoder to only the words that appear in registered commands and slots. This is **grammar mode**, and it dramatically improves recognition accuracy for command-driven UX.

Setting `freeSpeechMode = true` disables the grammar constraint, allowing VOSK to recognise any word in its vocabulary. Command matching becomes best-effort.

### When to use each mode

| | Grammar Mode (default) | Free Speech Mode |
|---|---|---|
| **Accuracy** | High -- VOSK only considers in-vocabulary words | Significantly lower for commands -- homophones and uncommon words break frequently |
| **Vocabulary** | Limited to words in your commands and slots | Unrestricted |
| **Best for** | Voice commands, menu navigation, game controls | Dictation, note-taking, chat, any feature that needs arbitrary text |
| **NumberSequence** | Reliable -- digit words are constrained | Unreliable -- "two" becomes "to", "orient" becomes "korean" |
| **False matches** | Possible from noise (grammar must pick *something*) | Fewer false matches, but fewer true matches too |

### Recommendation

Use grammar mode (the default) for all command-driven features. Only enable free speech when your feature genuinely needs arbitrary vocabulary, and accept that command matching will be best-effort in that mode. You can switch between the two by toggling `freeSpeechMode` at runtime and calling `SetActiveSets()` or `Configure()` to rebuild the grammar.

---

## See Also

- [Command Sets](command-sets.md) -- Group commands into switchable named sets for mode-specific grammars
- [Inspector Authoring](inspector-authoring.md) -- Define commands and slots with ScriptableObject assets instead of code
- [Editor Testing](editor-testing.md) -- Test commands with the debug window, text injection, and batch runner
- [Known Limitations](../KNOWN_LIMITATIONS.md) -- VOSK model quirks, homophones, and recognition edge cases
