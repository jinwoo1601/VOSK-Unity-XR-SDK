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
    |  partial matches with allowPartialMatch enter pending state
    v
Pending Command Check
    |  commands with requiresConfirmation enter pending state
    |  follow-up speech fills missing slots or confirms/cancels
    v
Debounce
    |  suppresses duplicate intents within commandCooldown seconds
    v
Events: OnCommandRecognised, OnCommandsRecognised, OnUnrecognisedSpeech
        OnCommandPending, OnCommandConfirmed, OnCommandCancelled
```

Each stage is configurable. The most common tuning points are `bufferWindow` (how long to wait for split speech), `minScore` / `minConfidence` (quality thresholds), and `commandCooldown` (debounce window).

---

## Patterns and Slots

Commands are defined as token arrays. **Literal tokens** must appear in the speech exactly as written. **Slot tokens** (wrapped in `{}`) match against registered slot values.

```csharp
// Pattern: "launch {weapon} target {target}"
// Matches: "launch missiles target alpha one"
// Extracts: weapon="missiles", target="alpha one"
new VoxrCommandDefinition("launch_weapon",
    new[] { new[] { "launch", "{weapon}", "target", "{target}" } })
```

Multi-word slot values (e.g. `"alpha one"`) are consumed greedily -- the parser tries longer matches first to avoid partial matches.

A command can have multiple alternative patterns, each representing a different way the user might phrase the same intent:

```csharp
new VoxrCommandDefinition("launch_weapon", new[] {
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

Every match produces a normalised **score** (0.0--1.0) that indicates how well the transcript covers the pattern. The parser uses a sliding start to tolerate preamble, hesitations, and false starts -- the score reflects the quality of the best-positioned match, discounted by how much of the utterance the start had to skip (see [Skipped-word penalty](#skipped-word-penalty)).

Two independent thresholds control what gets through:

```csharp
commandRecogniser.minScore = 0.6f;       // Reject low-quality pattern matches
commandRecogniser.minConfidence = 0.4f;   // Reject low VOSK word confidence
```

**Score** (`VoxrCommand.Score`) is computed by the parser based on how well the transcript satisfies the pattern, normalised against a *dynamic* denominator. Required tokens always count toward that denominator; optional tokens (`?word` literals and `{?slot}` slots) count only when they are actually spoken. An omitted optional therefore drops out of both sides of the ratio rather than diluting it, so a perfect match scores 1.0 whether or not its optional tokens were uttered — taking advantage of optionality is never penalized. A missed *required* token still pulls the score down.

**Confidence** (`VoxrCommand.Confidence`) is the minimum per-word VOSK acoustic confidence across matched tokens. This reflects how certain VOSK was about the words it heard. A value of `-1` means no word-level data was available (e.g. the transcript contained only `[unk]` tokens), which bypasses the `minConfidence` check entirely -- the command is accepted or rejected on score alone.

### Skipped-word penalty

The sliding start can begin a match anywhere in the utterance, and the words it walks past used to cost nothing. That let any stray sentence whose *tail* happened to resemble a short pattern execute it at a full 1.0 — "thrusters port", misheard as "thrusters report", would skip the unmatched "thrusters" and fire a one-word `report` command.

`skippedWordPenalty` (default `1.0`) adds each skipped in-grammar word to the score denominator, so the score becomes the fraction of the utterance the pattern actually covers:

| Utterance | Matched pattern | Score |
|-----------|-----------------|-------|
| `disengage` | `["disengage"]` | `1 / 1` = 1.0 |
| `target disengage` | `["disengage"]` | `1 / (1 + 1)` = 0.5 -- rejected at the default `minScore` |
| `launch launch all missiles target hotel one` | 5-element `launch_weapon` form | `5 / (5 + 1)` = 0.83 -- still accepted |

The penalty is proportional, so it only bites patterns short enough to be swallowed whole by a stray utterance; longer commands still absorb a false start. Two things are never charged:

- **`[unk]` tokens.** Out-of-grammar preamble and hesitation are exactly what the sliding start is for, so filler VOSK could not resolve stays free.
- **Words before a previous match ended.** Counting restarts after each extracted command, so chained commands in one utterance ("cease fire resume fire") do not penalise each other.

Set `skippedWordPenalty` to `0` to restore the previous behaviour. Raise it above `1.0` to demand that a command be an even larger share of what was said.

When tuning thresholds:
- Start with the defaults (`minScore=0.6`, `minConfidence=0.4`) and adjust based on testing.
- Don't push `minConfidence` above `0.5` unless you've verified your vocabulary avoids "two" and other low-confidence words (see [Known Limitations](../KNOWN_LIMITATIONS.md)).
- Use the [Batch Test Runner](editor-testing.md) to regression-test threshold changes.

---

## Slot Value Aliases

Map variant words to canonical values so the parser normalises them automatically:

```csharp
var quantity = new VoxrSlotDefinition("quantity",
    new[] { "one", "two", "three", "all" },
    new Dictionary<string, string> { { "a", "one" }, { "jackals", "jackal" } });
```

When VOSK transcribes `"a"`, the alias resolves it to `"one"` in the extracted slot value. Aliases are included in the generated grammar JSON, so VOSK knows to listen for the variant words.

**Validation:** The parser warns at configure time about single-character slot values and alias keys, as these are unreliable in VOSK grammar mode. Prefer longer, phonetically distinct alternatives.

---

## Dynamic Slot Filtering

Slot value providers let you narrow which values the parser accepts for a slot at runtime, without changing the VOSK grammar. This is useful when the set of valid targets, items, or options changes based on game state -- for example, only allowing the player to target enemies currently on screen, or restricting weapon selection to what's in their inventory.

```csharp
// Register a provider that returns only currently visible targets
commandRecogniser.RegisterSlotValueProvider("target", () =>
{
    return visibleTargets.Select(t => t.voiceName).ToArray();
});

// When targets change (spawn, die, enter/leave view), rebuild the parser
commandRecogniser.NotifySlotChanged();
```

### How it works

The **grammar** (VOSK vocabulary) always contains the full universe of slot values registered via `Configure()`. This means VOSK can transcribe any value at any time. The **parser** is rebuilt with only the provider's active values, so excluded values produce `OnUnrecognisedSpeech` instead of `OnCommandRecognised`.

This two-layer design avoids the audio gap that grammar rebuilds cause (see [Command Sets](command-sets.md)). The trade-off: VOSK may still transcribe an excluded value since it's in the grammar, but the parser will reject it.

### Alias filtering

Aliases that point to excluded canonical values are automatically pruned. If "hotel one" is excluded, the alias "h one" → "hotel one" is also removed from the parser.

### Null and empty providers

- A provider returning **null** is treated as "no opinion" -- the slot uses its full static values.
- A provider returning an **empty array** means nothing matches -- all values for that slot are excluded.
- `NumberSequence` slots are unaffected by providers.

### When to use dynamic slots vs command sets

| | Dynamic Slot Filtering | Command Sets |
|---|---|---|
| **What it narrows** | Which *values* a slot accepts | Which *commands* are active |
| **Grammar impact** | None -- no audio gap | Full rebuild -- ~50ms audio gap |
| **Best for** | Contextual value lists (targets, items, locations) | Mode switching (weapons, navigation) |
| **Combines with** | Command sets (orthogonal) | Dynamic slots (orthogonal) |

The two features are complementary. Use command sets for coarse mode switching and dynamic slots for fine-grained value filtering within a mode.

---

## NumberSequence Slots

Parse spoken digit words into concatenated integers for headings, frequencies, grid coordinates, and similar numeric commands:

```csharp
var heading = VoxrSlotDefinition.NumberSequence("heading", minWords: 1, maxWords: 3);

// "heading two seven zero" -> heading="270"
// "heading one eight"      -> heading="18"
```

The parser greedily consumes consecutive number words within the configured `minWords`/`maxWords` range. The accepted set is the full `VoxrNumberParser.DigitVocabulary` — zero through nineteen, the tens (twenty, thirty, …, ninety), plus `hundred` and `thousand`. The full vocabulary is merged into the grammar JSON automatically.

Use `VoxrNumberParser.ParseDigitSequence()` when you author commands as digit-by-digit utterances ("two seven zero" → `270`); use `VoxrNumberParser.ParseCardinal()` when you want the slot to read as a cardinal number ("two hundred" → `200`). `ParseDigitSequence` rejects anything outside `zero`–`nine`, so design your commands accordingly:

```csharp
commandRecogniser.OnCommandRecognised += cmd =>
{
    if (cmd.Intent == "set_heading")
    {
        int heading = VoxrNumberParser.ParseDigitSequence(cmd.GetSlot("heading"));
        Debug.Log($"Heading: {heading}");
    }
};
```

---

## Utterance Buffer

VOSK's voice activity detector can split mid-command pauses into separate utterances. The utterance buffer merges consecutive VOSK results within `bufferWindow` seconds before parsing.

```csharp
commandRecogniser.bufferWindow = 2.0f; // Recommended for Quest 3
```

If the speaker says "launch missiles" *pause* "target hotel one" and both results arrive within the window, they are concatenated and parsed as a single command.

**Tuning:** The default is 0.5s (tuned for typical PC latency). Quest 3 VOSK latency adds ~0.5--1.0s to inter-result gaps, so the default is usually too short on device — 2.0s is more reliable. Don't exceed ~2.5--3.0s or unrelated utterances may merge ("cross-command bleed").

### Eager flush (low-latency complete commands)

By default the buffer is purely time-driven: every command -- complete or not -- waits the full `bufferWindow` before firing. Enable **Eager Flush On Complete Match** in the Inspector to fire a command the instant the buffered speech forms a complete match that *cannot* be extended or completed by more words:

- **Complete and unambiguous** -> fires immediately, with zero buffer latency.
- **A prefix of a longer command**, or a **trailing slot that could still grow** (a multi-word enumerated value such as `"red"` -> `"red dragon"`, or a variable-length number sequence) -> keeps waiting the full window, so split commands are still recovered. "Prefix" is judged against slot vocabularies, not just pattern shape: a lone `{burn_level}` is *not* a prefix of `decelerate {burn_level}`, because no value of the slot begins with "decelerate".
- **Split command** -> fires as soon as its second half completes, instead of waiting another full window on top.

The feature is off by default; leaving it off preserves the exact time-only behaviour above. Each command's eligibility is computed once when commands are configured, so the only per-utterance cost is a single speculative parse of the buffer.

### Prefix hold (shortening the ambiguous wait)

The second bullet above -- a complete command that more speech could still extend -- has to wait, but it does not have to wait the *whole* window. It is only waiting on a continuation, and a speaker who is continuing starts almost immediately; the rest of `bufferWindow` is dead air. `prefixHoldSeconds` gives that state its own, shorter timer:

```csharp
commandRecogniser.bufferWindow = 2.0f;          // Quest 3
commandRecogniser.eagerFlushOnCompleteMatch = true;
commandRecogniser.prefixHoldSeconds = 0.6f;     // held matches wait 0.6s, not 2.0s
```

With `["fire"]` and `["fire", "at", "{target}"]` registered, "fire" alone now fires ~0.6s after the speaker stops instead of ~2.0s, while "fire at hotel one" still parses as the longer command -- the continuation lands well inside 0.6s.

- Applies **only** to a buffer that already parses as one complete, confident command spanning the whole buffer. Partial speech mid-split-command, speech that matches nothing, and grammars too complex for the eligibility precompute to analyse all keep the full `bufferWindow`.
- **Never lengthens** the wait: a value above `bufferWindow` is ignored.
- Re-evaluated on every VOSK result, so a continuation that does arrive puts the buffer back on the full window for the rest of the utterance.
- Requires `eagerFlushOnCompleteMatch`. Default `0` keeps the full window, i.e. the pre-`prefixHoldSeconds` behaviour.

Tune it against the pause you expect *inside* a command, not between commands: too short and the extended form becomes unspeakable, too long and you are back to paying the full window.

> With `prefixHoldSeconds` left at `0`, a command that is *also* a prefix of a longer one is the one case eager flush can't accelerate -- see [Known Limitations](../KNOWN_LIMITATIONS.md). Push-to-talk (`VoxrPushToTalkController.ReleaseTalk` -> `FlushPendingBuffer()`) gives those a deterministic, zero-latency endpoint.

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

## Pending Commands

Sometimes a command partially matches (some required slots are unfilled) or needs explicit confirmation before a high-consequence action fires. The pending command system handles both cases by holding the command in a "pending" state and listening for follow-up speech.

### Partial Match with Follow-Up Slot-Fill

Set `allowPartialMatch: true` on a command definition to let it enter pending state when matched with unfilled required slots, instead of being rejected by the score threshold.

```csharp
var launchCmd = new VoxrCommandDefinition("launch_weapon",
    new[] { new[] { "launch", "{weapon}", "target", "{target}" } },
    allowPartialMatch: true);
```

If the user says "launch missiles" without specifying a target, the command enters pending state and `OnCommandPending` fires. The system then listens for follow-up speech. If the user says "hotel one" within the `pendingTimeout` window, the target slot is filled and the command fires normally via `OnCommandConfirmed` and `OnCommandRecognised`.

```csharp
commandRecogniser.OnCommandPending += cmd =>
    Debug.Log($"Waiting for: {cmd.Intent}");

commandRecogniser.OnCommandConfirmed += cmd =>
    Debug.Log($"Confirmed: {cmd.Intent} target={cmd.GetSlot("target")}");

commandRecogniser.OnCommandCancelled += cmd =>
    Debug.Log($"Cancelled: {cmd.Intent}");
```

### Explicit Confirmation

Set `requiresConfirmation: true` to require the user to say a confirmation phrase before the command fires, even when fully matched.

```csharp
var selfDestruct = new VoxrCommandDefinition("self_destruct",
    new[] { new[] { "self", "destruct" } },
    requiresConfirmation: true);
```

After saying "self destruct", the command enters pending state. The user must say "confirm" (or another confirm phrase) to fire it, or "cancel" to discard it.

Default confirm vocabulary: "confirm", "affirmative", "yes", "go ahead", "do it". Default cancel vocabulary: "cancel", "abort", "negative", "belay that", "never mind". Override these with the `confirmVocabulary` and `cancelVocabulary` Inspector arrays on `VoxrCommandRecogniser`.

### Combined Partial + Confirmation

A command with both `allowPartialMatch` and `requiresConfirmation` goes through two pending stages: first, follow-up speech fills missing slots; then, the user confirms before the command fires.

### Timeout Behaviour

Configure `pendingTimeout` (default 5s) and `pendingTimeoutBehavior` on `VoxrCommandRecogniser`:

- **Cancel** (default) -- the pending command is discarded and `OnCommandCancelled` fires.
- **FireAsIs** -- the pending command fires with whatever slots were filled, even if some are still missing.

### Preemption

If a new complete command is recognised while a command is pending, the pending command is cancelled and the new command fires normally. This prevents stale pending commands from blocking normal operation.

### Grammar Integration

Confirm and cancel vocabulary words are automatically included in the VOSK grammar JSON, so they are recognised reliably in grammar mode. Custom vocabulary phrases are also merged into the grammar.

### Programmatic Control

Call `CancelPendingCommand()` to cancel the pending command from code (e.g. on a scene transition or mode switch). Check `HasPendingCommand` and `PendingCommand` to inspect the current pending state.

---

## Unrecognised Speech

When speech passes through the pipeline but no command is produced, `OnUnrecognisedSpeech` fires with the raw transcript. This happens in two situations:

1. **No pattern match** -- the parser could not match any command pattern against the transcript.
2. **All matches rejected** -- patterns matched but every candidate was rejected by `minScore`, `minConfidence`, or `commandCooldown` debounce.

The `string` parameter is the full buffered transcript (after utterance merging), exactly as VOSK transcribed it.

### When it does not fire

- If `Configure` has not been called, speech is silently dropped -- no events fire.
- If speech arrives during a grammar rebuild (stop/set/start cycle), it is discarded before reaching the parser.

### Common uses

**Diagnostics and tuning** -- Log unrecognised speech to identify patterns that need adding, thresholds that need adjusting, or VOSK transcription issues:

```csharp
commandRecogniser.OnUnrecognisedSpeech += text =>
{
    Debug.Log($"[VoXR] Unrecognised: \"{text}\"");
};
```

**Player feedback** -- Show a subtle UI hint so the player knows they were heard but their words didn't match a command:

```csharp
commandRecogniser.OnUnrecognisedSpeech += text =>
{
    hudController.ShowTransientMessage("Command not recognised");
};
```

**Dynamic slot interaction** -- When a value provider excludes a target, speech that names the excluded target fires `OnUnrecognisedSpeech` instead of `OnCommandRecognised`. You can use this to explain *why* the command failed:

```csharp
commandRecogniser.OnUnrecognisedSpeech += text =>
{
    if (text.Contains("target"))
        hudController.ShowTransientMessage("Target not available");
};
```

### Relationship to other events

`OnUnrecognisedSpeech` and `OnCommandRecognised`/`OnCommandsRecognised` are mutually exclusive per utterance. A given buffered transcript either produces commands (and fires the command events) or produces none (and fires `OnUnrecognisedSpeech`). It never fires both for the same transcript.

---

## Grammar Mode vs Free Speech

By default, `VoxrCommandRecogniser` constrains VOSK's decoder to only the words that appear in registered commands and slots. This is **grammar mode**, and it dramatically improves recognition accuracy for command-driven UX.

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
- [Editor Testing](editor-testing.md) -- Test commands with the debug window, session debug log, text injection, and batch runner
- [Known Limitations](../KNOWN_LIMITATIONS.md) -- VOSK model quirks, homophones, and recognition edge cases
