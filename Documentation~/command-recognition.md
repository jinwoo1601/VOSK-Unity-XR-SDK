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

### Never leave a required function word between a bare pattern and its slot

If a command has a bare pattern *and* a sibling that extends it with one required literal followed by a slot, mark that literal optional:

```csharp
// Hazardous -- a dropped "by" discards the burn level the speaker did say
new VoxrCommandDefinition("decelerate", new[] {
    new[] { "decelerate" },
    new[] { "decelerate", "by", "{burn_level}" },
})

// Safe -- the same two phrasings, with the droppable word optional
new VoxrCommandDefinition("decelerate", new[] {
    new[] { "decelerate" },
    new[] { "decelerate", "?by", "{burn_level}" },
})
```

Short unstressed function words (`by`, `at`, `to`, `mark`) are the tokens VOSK drops most, and speakers elide them too. When one goes missing, the slot-filled pattern is penalised for the missing *required* literal while the bare pattern still matches perfectly -- so the bare pattern wins, and the slot value that was recognised is discarded with nothing to signal it. "decelerate hard burn" executes a default-level decelerate. No threshold tuning reaches this: the bare pattern scores a clean 1.0, which nothing normalised to 1.0 can beat.

With the literal optional, an omitted optional drops out of both sides of the ratio, so the slot-filled pattern also scores 1.0 whether or not the word was spoken -- and being the candidate that covers more of the utterance, it wins. Both phrasings then extract the slot, and a bare "decelerate" still matches the bare pattern.

**The swap is not free.** Two costs, both worth knowing before you apply it wholesale:

- **It lowers the score of imperfect matches.** A matched *required* literal adds 1.0 to both sides of the ratio; a matched *optional* literal adds only 0.5 to both. Those are equivalent only when everything else in the pattern matches. As soon as something else misses, `(r - 0.5) / (d - 0.5)` is strictly below `r / d` -- so a partial match that used to clear `minScore` can fall under it. Usually an improvement (a half-heard command stops firing with slots missing), but it is a behaviour change, not a no-op.
- **It stops anchoring what follows it.** A required literal is a word that *must be spoken* before the next element can consume anything. Make it optional and the following slot can claim adjacent tokens the literal never introduced. With `orient heading {heading} ?mark {?elevation}`, a spurious digit after a full three-word heading -- "orient heading two seven zero **four**" -- is now absorbed as `elevation = "four"` and wins on span, where the required form scored 0.7, lost, and dropped the stray digit. Be wary when the slot after the literal is a `NumberSequence` or otherwise shares vocabulary with the slot before it.

The parser logs a validation warning at construction naming the literal and the slot at risk. This holds whether the trailing slot is required or optional (`{?elevation}` after a required `mark` strands the elevation exactly the same way).

**The check follows what the parser actually compares**, so it covers the hazard in every form it takes:

- **Across commands, not just within one.** Selection runs over every pattern of every command through a single comparison, so declaring the two phrasings as separate intents (`decelerate` and `decelerate_by`) reproduces the hazard exactly. It is warned about.
- **Over a run of required literals, not just one.** Dropping any single word in `decelerate by the {burn_level}` strands the value just as dropping `by` alone does.
- **Over optional forms.** `fire {?quantity} {weapon}` is not literally a prefix of `fire {weapon} at {target}`, but it is once its own optional is omitted -- which is exactly the form the parser matches when no quantity is spoken. Patterns are expanded before comparison, as the eager-flush prefix analysis already does.

The one limit: a pattern carrying more than six optional elements is compared unexpanded, since this scan runs on every parser rebuild and expansion is exponential. That costs recall on that pattern only.

---

## Scored Matching

> This section is the working summary. For the full model — the per-element score table, the selection and tie-break order, the eager-flush verdict rules, and worked examples traced through to their session-log entries — see [Matching and Scoring](scoring.md).

Every match produces a normalised **score** (0.0--1.0) that indicates how well the transcript covers the pattern. The parser uses a sliding start to tolerate preamble, hesitations, and false starts -- the score reflects the quality of the best-positioned match, discounted by how much of the utterance the start had to skip (see [Skipped-word penalty](#skipped-word-penalty)).

Two independent thresholds control what gets through:

```csharp
commandRecogniser.minScore = 0.6f;       // Reject low-quality pattern matches
commandRecogniser.minConfidence = 0.4f;   // Reject low VOSK word confidence
```

**Score** (`VoxrCommand.Score`) is computed by the parser based on how well the transcript satisfies the pattern, normalised against a *dynamic* denominator. Required tokens always count toward that denominator; optional tokens (`?word` literals and `{?slot}` slots) count only when they are actually spoken. An omitted optional therefore drops out of both sides of the ratio rather than diluting it, so a perfect match scores 1.0 whether or not its optional tokens were uttered — taking advantage of optionality is never penalized. A missed *required* token still pulls the score down.

**Confidence** (`VoxrCommand.Confidence`) is the minimum per-word VOSK acoustic confidence across matched tokens. This reflects how certain VOSK was about the words it heard. A value of `-1` means no word-level data was available *for the matched span* (usually injected text, which carries none), which bypasses the `minConfidence` check entirely -- the command is accepted or rejected on score alone. See [the two gates](scoring.md#minconfidence-default-04) for the second, less obvious way `-1` arises and for how a repeated word resolves.

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

Capture spoken number words for headings, frequencies, grid coordinates, and similar numeric commands:

```csharp
var heading = VoxrSlotDefinition.NumberSequence("heading", minWords: 1, maxWords: 3);

// "heading two seven zero" -> heading="two seven zero"
// "heading one eight"      -> heading="one eight"
```

The parser greedily consumes consecutive number words within the configured `minWords`/`maxWords` range. The accepted set is the full `VoxrNumberParser.DigitVocabulary` — zero through nineteen, the tens (twenty, thirty, …, ninety), plus `hundred` and `thousand`. The full vocabulary is merged into the grammar JSON automatically.

> **The slot value is the spoken words, not a number.** `cmd.GetSlot("heading")` returns `"two seven zero"`, never `"270"`. `int.TryParse` on it fails on every utterance and returns `0` — silently, since `TryParse` does not throw — which reads as a command that simply never works. Convert the value yourself with [`VoxrNumberParser`](api/number-parser.md).

### Converting the value

`VoxrNumberParser.ParseDigitSequence()` handles digit-by-digit utterances ("two seven zero" → `270`) and rejects anything outside `zero`–`nine`. `VoxrNumberParser.ParseCardinal()` handles cardinal phrases ("two hundred" → `200`) and accepts the whole vocabulary. Both throw `FormatException` on words they do not accept, rather than returning a sentinel you could branch on, so the canonical pattern is to try the digit path first and fall back to the cardinal one. Guard for the empty string separately: an unmatched slot makes `GetSlot` return `""`, and both parsers map that to `0` rather than throwing — without the guard an absent slot silently becomes heading zero.

```csharp
using System;
using VoXR.Commands;

// Returns false when the slot is absent or the words parse as neither form.
static bool TryParseNumberSlot(VoxrCommand cmd, string slotName, out int value)
{
    value = 0;
    string words = cmd.GetSlot(slotName);   // e.g. "two seven zero" — words, not digits
    if (string.IsNullOrEmpty(words))
        return false;

    try { value = VoxrNumberParser.ParseDigitSequence(words); return true; }
    catch (FormatException) { }             // contains "ten"+ or a cardinal — try the other path

    try { value = VoxrNumberParser.ParseCardinal(words); return true; }
    catch (FormatException) { return false; }
}

commandRecogniser.OnCommandRecognised += cmd =>
{
    if (cmd.Intent == "set_heading" && TryParseNumberSlot(cmd, "heading", out int heading))
        Debug.Log($"Heading: {heading}");
};
```

The order is load-bearing, because the two parsers read the same words differently: `"two seven zero"` is `270` on the digit path but `9` on the cardinal one, so trying the digit path first is what lets digit dictation win. And a phrase only the cardinal path accepts gets its reading whatever the speaker meant — `"two seventy"` throws on the digit path, then parses as `72`, not the `270` most speakers intend by it.

So the fallback resolves *which parser* to use, not what the speaker meant. Pick one convention per slot: where a slot is always dictated digit-by-digit, call `ParseDigitSequence` alone and treat the `FormatException` as a misrecognition.

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
- **Missing a required slot** (the pattern's literals are all in, but an argument has not been spoken yet) -> keeps waiting the full window, even where the arithmetic leaves the partial match above `minScore`. Unlike the case above it never qualifies for the shortened `prefixHoldSeconds` hold, because it is not a complete match. An unspoken slot consumes no words, so such a buffer otherwise looks complete right up to the moment the missing words arrive.
- **Split command** -> fires as soon as its second half completes, instead of waiting another full window on top.
- **Out-of-grammar preamble** (a station address such as "Helm, ...", reported by VOSK as `[unk]`) is skipped, so an addressed command commits as fast as the bare one. Only a *leading* run is skipped: anything left over at the end -- recognised or `[unk]` -- is treated as an in-progress tail and keeps waiting, as does a leading word VOSK did resolve.

The feature is off by default; leaving it off preserves the exact time-only behaviour above. Each command's eligibility is computed once when commands are configured, so the only per-utterance cost is a single speculative parse of the buffer.

**Grammars past the analysis limit.** Deciding eligibility means expanding a pattern's optional elements, which is exponential (2^optionals), so a pattern carrying more than 12 of them is refused rather than partially analysed -- and since a partially analysed set could commit the wrong command, the refusal covers the whole command set. Nothing in it then commits early; every complete match is *held* instead, so it waits `prefixHoldSeconds` where that is set and the full `bufferWindow` where it is not. The parser names the offending pattern, its intent, and its optional count in a warning at construction, so the condition surfaces when the grammar is authored rather than mid-session.

### Prefix hold (shortening the ambiguous wait)

The second bullet above -- a complete command that more speech could still extend -- has to wait, but it does not have to wait the *whole* window. It is only waiting on a continuation, and a speaker who is continuing starts almost immediately; the rest of `bufferWindow` is dead air. `prefixHoldSeconds` gives that state its own, shorter timer:

```csharp
commandRecogniser.bufferWindow = 2.0f;          // Quest 3
commandRecogniser.eagerFlushOnCompleteMatch = true;
commandRecogniser.prefixHoldSeconds = 0.6f;     // held matches wait 0.6s, not 2.0s
```

With `["fire"]` and `["fire", "at", "{target}"]` registered, "fire" alone now fires ~0.6s after the speaker stops instead of ~2.0s, while "fire at hotel one" still parses as the longer command -- the continuation lands well inside 0.6s.

- Applies **only** to a buffer that already parses as one complete, confident command spanning the whole buffer (bar a leading `[unk]` run). Partial speech mid-split-command and speech that matches nothing keep the full `bufferWindow`.
- A grammar too complex for the eligibility precompute to analyse (above) never commits early, but its complete matches are held like any other, so the hold applies to them too.
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
2. **Every match fell under `minScore`** -- patterns matched, but no candidate scored high enough to fire.
3. **A match was diverted to pending** -- a partial match or a `requiresConfirmation` command entered the pending state; `OnCommandPending` fires as well.

It does **not** fire when a candidate was rejected by `minConfidence` or suppressed by `commandCooldown` debounce -- those two are dropped silently, on the reasoning that the user did say a valid command, just not confidently or not soon enough after the last one. See [the gates](scoring.md#what-onunrecognisedspeech-actually-means) for the full table.

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

`OnUnrecognisedSpeech` and `OnCommandRecognised`/`OnCommandsRecognised` are mutually exclusive per utterance: a transcript that produces at least one accepted command never also fires `OnUnrecognisedSpeech`.

The converse does not hold. A transcript that produces *no* accepted command is silent when a candidate was filtered by `minConfidence` or debounce, so "no command fired" and "`OnUnrecognisedSpeech` fired" are not the same condition.

---

## Grammar Mode vs Free Speech

By default, `VoxrCommandRecogniser` constrains VOSK's decoder to only the words that appear in registered commands and slots. This is **grammar mode**, and it dramatically improves recognition accuracy for command-driven UX.

Setting `freeSpeechMode = true` disables the grammar constraint, allowing VOSK to recognise any word in its vocabulary. Command matching becomes best-effort.

### What the grammar contains

The grammar is not just a bag of words. Each **contiguous run of required literals** in a pattern, and each **multi-word slot value or alias**, is emitted as a single multi-word entry, alongside the individual words:

```csharp
new[] { "close", "distance", "{range}", "target", "{target}" }
// entries: "close distance", "target", plus "close", "distance", "target",
//          plus each {range}/{target} surface form ("safe range", "hotel one", ...)
```

VOSK charges one language-model transition per entry, so a three-word entry costs one transition where the same three words cost three. That makes the order you declared the cheaper path through the decoder's search, which is what stops in-grammar words substituting freely for one another -- `switch to navigation` no longer decodes as `switch two navigation`.

Two consequences worth knowing when you author patterns:

- **A slot or an optional literal ends a run.** Neither is guaranteed to be spoken, so the words either side of it are not reliably adjacent and are never welded together. Literals stranded alone between two slots get no phrase protection -- that is one more reason to prefer runs of required literals over lone function words (see [Known Limitations](../KNOWN_LIMITATIONS.md)).
- **The single words are still there.** The phrase entries bias the decoder; they do not forbid anything. An utterance the VAD splits mid-phrase still decodes as fragments, and the parser's sliding start reassembles what it can.

This is automatic -- there is no setting, and nothing about your pattern or slot declarations changes.

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

- [Matching and Scoring](scoring.md) -- The score formula, penalties, selection order, gates, and eager-flush verdicts in full
- [Command Sets](command-sets.md) -- Group commands into switchable named sets for mode-specific grammars
- [Number Parser](api/number-parser.md) -- Convert a `NumberSequence` slot's spoken words into an integer
- [Inspector Authoring](inspector-authoring.md) -- Define commands and slots with ScriptableObject assets instead of code
- [Editor Testing](editor-testing.md) -- Test commands with the debug window, session debug log, text injection, and batch runner
- [Known Limitations](../KNOWN_LIMITATIONS.md) -- VOSK model quirks, homophones, and recognition edge cases
