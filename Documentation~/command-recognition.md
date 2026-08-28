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
Pending Command Check (only while a command is pending)
    |  the flushed transcript is offered to the pending command FIRST:
    |  cancel, then a disambiguation choice, then confirm, then slot-fill
    |  cancel/choice/confirm answers bypass every stage below; a slot-fill
    |  still parses, and a complete new command wins over the fill
    |  (speech that answers nothing falls through and is parsed normally)
    v
Parser (pattern match + scoring)
    |  tries each command pattern against the transcript
    |  uses sliding start to skip preamble/filler words
    |  extracts slot values, computes normalised score (0.0-1.0)
    v
Sequential Extraction
    |  extracts multiple commands left-to-right from a single utterance
    |  ("cease fire launch missiles target hotel one" -> two commands)
    |
    |  leading-required-miss bar -- applied within extraction, before any
    |  threshold: a round whose winner missed its FIRST required element
    |  consumes its span and yields no command, so a round can produce nothing
    v
Threshold Filter
    |  rejects commands below minScore or minConfidence
    |  confidence of -1 (no data) bypasses the minConfidence check
    |  rejects commands missing a required slot, at ANY score --
    |  with allowPartialMatch they enter pending state instead
    v
Debounce
    |  suppresses duplicate intents within commandCooldown seconds
    v
Pending Entry
    |  sibling ties enter pending state when disambiguateSiblingTies is on
    |  commands with requiresConfirmation enter pending state
    v
Events: OnCommandRecognised, OnCommandsRecognised, OnUnrecognisedSpeech
        OnCommandPending, OnCommandConfirmed, OnCommandCancelled
```

Each stage is configurable. The most common tuning points are `bufferWindow` (how long to wait for split speech), `minScore` / `minConfidence` (quality thresholds), and `commandCooldown` (debounce window).

Four terms recur throughout this guide:

- **Flush** -- the moment the utterance buffer hands its merged transcript to the parser: at the end of `bufferWindow`, or early via eager flush or a push-to-talk release.
- **Pending** -- a command held waiting for follow-up speech: missing slots, a confirmation, or a disambiguation answer. See [Pending Commands](#pending-commands).
- **Eager flush** -- the opt-in that fires a complete, unambiguous, unextendable command before the window closes. See [Eager flush](#eager-flush-low-latency-complete-commands).
- **Sibling tie** -- two patterns of *different* intents that differ at exactly one required word, so dropping that word leaves them indistinguishable. See [Authoring hazards](#do-not-separate-two-commands-by-a-single-word).

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

**The `?` for an optional slot goes *inside* the braces: `{?quantity}`.** Writing `?{quantity}` does not make the slot optional -- it parses as a required *literal* token no utterance can ever produce, so every match of that pattern silently misses a required element, with no warning and no exception.

Two pattern shapes carry authoring hazards the parser warns about at construction: a required function word standing between a bare pattern and its slot, and two commands separated by a single word. A third hazard is not a pattern shape at all -- one intent registered by more than one command definition. A fourth shape, a bare pattern whose tail can be read as another command, carries no construction-time warning at all. All are covered in [Authoring hazards](#authoring-hazards), after the scoring and buffering concepts they depend on.

---

## Scored Matching

> This section is the working summary. For the full model — the per-element score table, the selection and tie-break order, the eager-flush verdict rules, and worked examples traced through to their session-log entries — see [Matching and Scoring](scoring.md).

Every match produces a normalised **score** (0.0--1.0) built from two halves: how well the transcript satisfied the pattern, and how much of the utterance the match left unexplained. The parser uses a sliding start to tolerate preamble, hesitations, and false starts, so a pattern can match anywhere in the transcript -- and what it walks past or leaves behind counts against it (see [Coverage](#coverage)).

Two independent thresholds control what gets through, both set on the `VoxrCommandRecogniser` component **in the Inspector**:

```
minScore        0.6    // Reject low-quality pattern matches
minConfidence   0.4    // Reject low VOSK word confidence
```

They are serialized fields with no public setter, so there is no code path for changing them at runtime -- tune them on the component, and regression-test the change with the [Batch Test Runner](api/batch-test-runner.md), which does take both as constructor arguments.

**Score** (`VoxrCommand.Score`) is computed by the parser based on how well the transcript satisfies the pattern, normalised against a *dynamic* denominator. Required tokens always count toward that denominator; optional tokens (`?word` literals and `{?slot}` slots) count only when they are actually spoken. An omitted optional therefore drops out of both sides of the ratio rather than diluting it, so a perfect match scores 1.0 whether or not its optional tokens were uttered — taking advantage of optionality is never penalized. A missed *required* token still pulls the score down, and so does anything the match left unexplained — see [Coverage](#coverage).

**Confidence** (`VoxrCommand.Confidence`) is the minimum per-word VOSK acoustic confidence across matched tokens. This reflects how certain VOSK was about the words it heard. A value of `-1` means no word-level data was available *for the matched span* (usually injected text, which carries none), which bypasses the `minConfidence` check entirely -- the command is accepted or rejected on score alone. See [the two gates](scoring.md#minconfidence-default-04) for the second, less obvious way `-1` arises and for how a repeated word resolves.

### Coverage

The sliding start can begin a match anywhere in the utterance, and a pattern stops when its elements run out. A command is therefore scored on how much of the utterance it **explains**, not only on how neatly it matched the part it chose: `coverageWeight` (default `1.0`, named `skippedWordPenalty` before #65) adds every in-grammar token the match leaves unexplained to the score denominator — both those the start walked past to reach the match and those left over after it.

Without the leading half, any stray sentence whose *tail* happened to resemble a short pattern would execute it at a full 1.0 — "thrusters port", misheard as "thrusters report", would skip the unmatched "thrusters" and fire a one-word `report` command.

| Utterance | Matched pattern | Score |
|-----------|-----------------|-------|
| `disengage` | `["disengage"]` | `1 / 1` = 1.0 |
| `target disengage` | `["disengage"]` | `1 / (1 + 1)` = 0.5 -- rejected at the default `minScore` |
| `disengage target` | `["disengage"]` | `1 / (1 + 1)` = 0.5 -- the trailing side, charged the same |
| `launch launch all missiles target hotel one` | 5-element `launch_weapon` form | `5 / (5 + 1)` = 0.83 -- still accepted |

The charge is proportional, so it only bites patterns short enough to be swallowed whole by a longer utterance; longer commands still absorb a false start.

**It is applied while candidates are compared, not to the winner afterwards.** So it decides *which pattern wins* — and that is what stops a bare pattern out-ranking a slot-filled sibling that explained more of what was said (see [the function-word hazard](#never-leave-a-required-function-word-between-a-bare-pattern-and-its-slot) above).

Three things go uncharged (the third with one exception, noted below):

- **`[unk]` tokens.** Out-of-grammar preamble and hesitation are exactly what the sliding start is for, so filler VOSK could not resolve stays free — and it is transparent rather than a run terminator, so one noise token cannot hide the real leftovers behind it. Only the literal `[unk]` is exempt, which is why `freeSpeechMode`, `InjectText`, and the batch runner charge trailing filler that the grammar-constrained decoder would have hidden.
- **Words before a previous match ended.** Counting restarts after each extracted command, so chained commands in one utterance ("cease fire resume fire") do not penalise each other.
- **Trailing tokens that could begin another match.** Counting stops at the first token some active pattern can be matched from with more of its required elements matched than missed — including a pattern that gets there by missing leading elements the decoder dropped — which is what keeps multi-command utterances intact: "cease fire launch missiles target hotel one" scores `cease_fire` at a full `2 / 2`, not `2 / 7`. The exception is a token the candidate's own next required element just tried and failed to match, which is always charged — see [the full rule](scoring.md#what-counts-as-orphaned).

Set `coverageWeight` to `0` to restore the pre-#31 behaviour — note this also switches off the #42 protection added in #65, so a bare pattern can once more win over its slot-filled sibling. Raise it above `1.0` to demand that a command be an even larger share of what was said.

Existing grammars re-score on upgrade, with no compatibility mode. The visible change is that a short command trailed by words the grammar cannot place may stop firing where it used to — see [Known Limitations](../KNOWN_LIMITATIONS.md) for the measured cases and the authoring responses. The full rule, including the orphan test above and the exception that keeps it from rewarding a worse match, is in [Matching and Scoring](scoring.md#2-coverage).

When tuning thresholds:
- Start with the defaults (`minScore=0.6`, `minConfidence=0.4`) and adjust based on testing.
- Don't push `minConfidence` above `0.5` unless you've verified your vocabulary avoids "two" and other low-confidence words (see [Known Limitations](../KNOWN_LIMITATIONS.md)).
- Use the [Batch Test Runner](editor-testing.md#batch-test-runner) to regression-test threshold changes.

---

## Slot Value Aliases

Map variant words to canonical values so the parser normalises them automatically:

```csharp
var quantity = new VoxrSlotDefinition("quantity",
    new[] { "one", "two", "three", "all" },
    new Dictionary<string, string> { { "a", "one" }, { "jackals", "jackal" } });
```

When VOSK transcribes `"a"`, the alias resolves it to `"one"` in the extracted slot value. Aliases are included in the generated grammar JSON, so VOSK knows to listen for the variant words.

**Validation:** The parser warns at configure time about slot *values* and alias keys alike that are uppercase (VOSK outputs lowercase, so such a form can never match), that carry punctuation (VOSK strips it -- write `oclock`, not `o'clock`), or that are a single character. The last is informational: short tokens are recognised unreliably, but `a` above is deliberate, since an alias to a longer canonical resolves when VOSK does hear the word and costs nothing when it is dropped. Unlike the Editor-only authoring scans in [Authoring hazards](#authoring-hazards), these warnings fire in player builds too.

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

The **grammar** (VOSK vocabulary) always contains the full universe of slot values registered via `Configure()`. This means VOSK can transcribe any value at any time. The **parser** is rebuilt with only the provider's active values, so excluded values produce `OnUnrecognisedSpeech` instead of `OnCommandRecognised` -- or, on a command that sets `allowPartialMatch`, `OnCommandPending` asking for the slot to be filled again, since an excluded value reads as an unfilled slot rather than as a wrong one.

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

`bufferWindow` is an Inspector field on `VoxrCommandRecogniser` -- like the thresholds, it is a serialized field with no public setter, so tune it on the component. Set it to `2.0` for Quest 3. Setting it to `0` disables buffering entirely: each VOSK result is parsed the moment it arrives, and eager flush and prefix hold below no longer apply.

If the speaker says "launch missiles" *pause* "target hotel one" and both results arrive within the window, they are concatenated and parsed as a single command.

**Tuning:** The default is 0.5s (tuned for typical PC latency). Quest 3 VOSK latency adds ~0.5--1.0s to inter-result gaps, so the default is usually too short on device — 2.0s is more reliable. Don't exceed ~2.5--3.0s or unrelated utterances may merge ("cross-command bleed").

### Eager flush (low-latency complete commands)

By default the buffer is purely time-driven: every command -- complete or not -- waits the full `bufferWindow` before firing. Enable **Eager Flush On Complete Match** in the Inspector to fire a command the instant the buffered speech forms a complete match that *cannot* be extended or completed by more words:

- **Complete and unambiguous** -> fires immediately, with zero buffer latency.
- **A prefix of a longer command**, or a **trailing slot that could still grow** (a multi-word enumerated value such as `"red"` -> `"red dragon"`, or a variable-length number sequence) -> keeps waiting the full window, so split commands are still recovered. "Prefix" is judged against slot vocabularies, not just pattern shape: a lone `{burn_level}` is *not* a prefix of `decelerate {burn_level}`, because no value of the slot begins with "decelerate".
- **Missing a required slot** (the pattern's literals are all in, but an argument has not been spoken yet) -> keeps waiting the full window, even where the arithmetic leaves the partial match above `minScore`. Unlike the case above it never qualifies for the shortened `prefixHoldSeconds` hold, because it is not a complete match. An unspoken slot consumes no words, so such a buffer otherwise looks complete right up to the moment the missing words arrive.
- **Still owing its last word** (the pattern's final required element has not been spoken yet, as in "switch to" against `switch to weapons`) -> keeps waiting the full window. A missing word consumes nothing, so such a buffer looks complete by every other measure; where a sibling command shares the prefix, committing would fire whichever of them happens to be registered first.
- **Missing its own first required element** -> keeps waiting the full window. The eager gate refuses a winner whose first required element matched nothing, above the point where the shortened `prefixHoldSeconds` hold would be armed, so such a buffer never qualifies for it. See [the bar](scoring.md#the-leading-required-miss-bar).
- **Ambiguous between two intents** -> keeps waiting the full window. Where the buffer fits two patterns of *different* intents exactly equally — same score, same span, same literal count — and they differ at just one required word, the winner would be decided by registration order alone. The gate declines rather than commit a coin flip early. This covers the *medial* drop the tail rule above cannot see: `set {ship} mode on` against `set {ship} level on`, heard as "set alpha on". The same command still fires at the end of the window — or, with `disambiguateSiblingTies` on, the speaker is asked which they meant. See [the one-word hazard](#do-not-separate-two-commands-by-a-single-word).
- **Split command** -> fires as soon as its second half completes, instead of waiting another full window on top.
- **Out-of-grammar preamble** (a station address such as "Helm, ...", reported by VOSK as `[unk]`) is skipped, so an addressed command commits as fast as the bare one. Only a *leading* run is skipped: anything left over at the end -- recognised or `[unk]` -- is treated as an in-progress tail and keeps waiting, as does a leading word VOSK did resolve.
- **While a command is pending, the eager path is skipped entirely.** Confirmations, slot-fills, and disambiguation answers always wait the full `bufferWindow` -- `prefixHoldSeconds` does not shorten them either. Push-to-talk's release flush is the way to give follow-up speech a deterministic endpoint.

The feature is off by default; leaving it off preserves the exact time-only behaviour above. Each command's eligibility is computed once when commands are configured, so the per-utterance cost is one speculative parse of the buffer per VOSK result, not per command.

**Grammars past the analysis limit.** Deciding eligibility means expanding a pattern's optional elements, which is exponential (2^optionals), so a pattern carrying more than 12 of them is refused rather than partially analysed -- and since a partially analysed set could commit the wrong command, the refusal covers the whole command set. Nothing in it then commits early; every complete match is *held* instead, so it waits `prefixHoldSeconds` where that is set and the full `bufferWindow` where it is not. The parser names the offending pattern, its intent, and its optional count in a warning at construction, so the condition surfaces when the grammar is authored rather than mid-session.

### Prefix hold (shortening the ambiguous wait)

The second bullet above -- a complete command that more speech could still extend -- has to wait, but it does not have to wait the *whole* window. It is only waiting on a continuation, and a speaker who is continuing starts almost immediately; the rest of `bufferWindow` is dead air. `prefixHoldSeconds` gives that state its own, shorter timer:

Set the three fields together in the Inspector (all serialized, none settable from code):

```
Buffer Window                    2.0    // Quest 3
Eager Flush On Complete Match    [x]
Prefix Hold Seconds              0.6    // held matches wait 0.6s, not 2.0s
```

With `["fire"]` and `["fire", "at", "{target}"]` registered, "fire" alone now fires ~0.6s after the speaker stops instead of ~2.0s, while "fire at hotel one" still parses as the longer command -- the continuation lands well inside 0.6s.

- Applies **only** to a buffer that already parses as one complete, confident command spanning the whole buffer (bar a leading `[unk]` run). Partial speech mid-split-command and speech that matches nothing keep the full `bufferWindow` — and so does a buffer whose winner was [barred](scoring.md#the-leading-required-miss-bar) for missing its first required element, which the eager gate refuses before it can arm the shortened hold. The barred candidate never fires on either path; what happens at the end of the window depends on the rest of the utterance, which may still yield a command from a later round.
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

`OnCommandsRecognised` is not exclusive to multi-command utterances: a command resolved through the pending path (confirmed, disambiguated, or slot-filled) also arrives in it, as a one-element batch, after `OnCommandConfirmed` and `OnCommandRecognised`. A handler subscribed to both events must expect every command to appear in both.

---

## Debounce

Per-intent debounce suppresses duplicate firings within `commandCooldown` seconds (an Inspector field, default `0.3`). This applies both across separate VOSK results and within a single parse batch from sequential extraction.

If the user says the same command twice quickly (or VOSK produces overlapping results), the second firing is suppressed.

---

## Authoring hazards

The parser scans the grammar at construction for shapes that silently misbehave -- two that go wrong when the recogniser drops a word, and one that is wrong in the registration list itself -- and warns about each in the Editor. A third drop-a-word shape below — a bare pattern whose tail can be read as another command — is **not** machine-detected: nothing warns about it at construction. All are worth designing away rather than discovering in the field.

### Never leave a required function word between a bare pattern and its slot

If a command has a bare pattern *and* a longer one that extends it with a required literal followed by a slot, mark that literal optional:

```csharp
// Warned about -- a dropped "by" leaves the slot-filled form barely above the gate
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

Short unstressed function words (`by`, `at`, `to`, `mark`) are the tokens VOSK drops most, and speakers elide them too. When one goes missing, the slot-filled pattern loses that element's credit while still counting it in its denominator, so it falls to `2/3` = 0.67 while the bare pattern still matches everything it claims.

**Until #65 that decided it.** The bare pattern scored a flat 1.0, won selection, and the slot value the speaker *did* say was discarded with nothing to signal it -- "decelerate hard burn" executed a default-level decelerate. No threshold tuning reached it, since nothing normalised to 1.0 can be out-scored. [Coverage](#coverage) closes the common case: the bare pattern is now charged for the "hard burn" it leaves unexplained, scores `1/(1+2)` = 0.33, and loses to the 0.67. The command fires **with** its argument.

**The warning stands, and the swap is still worth making** -- what changed is the cost of ignoring it, not the advice. Three reasons: `0.67` clears the default `minScore` by only `0.07`, so anything else going wrong in the same utterance puts it back under the gate; setting `coverageWeight` to `0` brings the old selection behaviour back; and coverage does **not** reach the case where the stranded value's own first word begins some other pattern, because the orphan run terminates there and the bare form is charged nothing -- register `["hard", "stop"]` here and the bug returns in full at the default weight. With the literal optional, an omitted optional drops out of both sides of the ratio, so the slot-filled pattern scores 1.0 whether or not the word was spoken and wins outright -- in the residual case too. Both phrasings then extract the slot, and a bare "decelerate" still matches the bare pattern.

**The swap is not free.** Two costs, both worth knowing before you apply it wholesale:

- **It lowers the score of imperfect matches.** A matched *required* literal adds 1.0 to both sides of the ratio; a matched *optional* literal adds only 0.5 to both. Those are equivalent only when everything else in the pattern matches. As soon as something else misses, `(r - 0.5) / (d - 0.5)` is strictly below `r / d` -- so a partial match that used to clear `minScore` can fall under it. Usually an improvement (a half-heard command stops firing with slots missing), but it is a behaviour change, not a no-op.
- **It stops anchoring what follows it.** A required literal is a word that *must be spoken* before the next element can consume anything. Make it optional and the following slot can claim adjacent tokens the literal never introduced. With `orient heading {heading} ?mark {?elevation}`, a spurious digit after a full three-word heading -- "orient heading two seven zero **four**" -- is now absorbed as `elevation = "four"` and wins on span, where the required form scored `4/5` = 0.8, lost, and dropped the stray digit. Be wary when the slot after the literal is a `NumberSequence` or otherwise shares vocabulary with the slot before it.

The parser logs a validation warning at construction naming the literal and the slot at risk. This holds whether the trailing slot is required or optional (`{?elevation}` after a required `mark` strands the elevation exactly the same way). The warning is **Editor-only**: it is authoring guidance, and since coverage closed the common case it fires on many grammars that now behave correctly, so it is kept out of player builds and device logs where it would only be suppressed wholesale.

**The check follows what the parser actually compares**, so it covers the hazard in every form it takes:

- **Across commands, not just within one.** Selection runs over every pattern of every command through a single comparison, so declaring the two phrasings as separate intents (`decelerate` and `decelerate_by`) reproduces the hazard exactly. It is warned about.
- **Over a run of required literals, not just one.** Dropping any single word in `decelerate by the {burn_level}` strands the value just as dropping `by` alone does.
- **Over optional forms.** `fire {?quantity} {weapon}` is not literally a prefix of `fire {weapon} at {target}`, but it is once its own optional is omitted -- which is exactly the form the parser matches when no quantity is spoken. Patterns are expanded before comparison, as the eager-flush prefix analysis already does.

The one limit: a pattern carrying more than six optional elements is compared unexpanded, since this scan runs on every parser rebuild an Editor session makes and expansion is exponential. For *this* scan that costs recall on that pattern only. The same bound applies to the scan below, where it costs more, because those forms now decide whether the recogniser warns you and whether it can ask the speaker -- see `KNOWN_LIMITATIONS.md`.

### Do not separate two commands by a single word

If two *different* intents differ at exactly one required word, the parser cannot tell them apart when the recogniser drops it:

```csharp
// Warned about -- one dropped word and registration order picks the intent
new VoxrCommandDefinition("mode_weapons",    new[] { new[] { "switch", "to", "weapons" } })
new VoxrCommandDefinition("mode_navigation", new[] { new[] { "switch", "to", "navigation" } })

// Safe -- the two phrasings differ in TWO places, so losing one word still leaves
// the other to decide
new VoxrCommandDefinition("mode_weapons",    new[] { new[] { "arm", "weapons" } })
new VoxrCommandDefinition("mode_navigation", new[] { new[] { "show", "navigation" } })
```

Say "switch to navigation", lose the last word, and the surviving `switch to` fits **both** patterns exactly equally: same start, same `(1 + 1 + 0) / 3` = 0.67, same consumed span, same literal count. Selection exhausts every key it has and falls through to its last — the order the patterns were registered in — so `mode_weapons` fires because it happens to be declared first. It fires consistently, not randomly, which is what makes it easy to miss in testing and easy to hit in the field.

This is not a scoring bug and no threshold reaches it. The word that would have decided is exactly the word that went missing; the evidence is not weak but **absent**. So there are two things to do about it, and this section covers both: notice the shape before you ship it, and — where you cannot design it away — [ask the speaker](#ambiguous-commands-ask-instead-of-guessing) rather than guess.

**The differing word can sit anywhere.** A word at the *end* is caught by the eager-flush gate's tail rule, which refuses to commit a pattern whose trailing required element never matched. A word in the *middle* clears that rule, because the elements after it still match and the tail check resets:

```
set {ship} mode  on      "set alpha on"  ->  3/4 = 0.75, spans the buffer,
set {ship} level on                          and fits both intents exactly equally
```

**The eager gate refuses to commit on either shape.** When two patterns of *different* intents tie on a buffer and differ only at one required word, firing early would commit a coin flip before the utterance is even over — so the gate declines and lets the buffer run its full window. The same command still fires; it fires at the end of the window rather than immediately.

That does not *resolve* the ambiguity, and it is worth being clear why the wait helps at all — because the obvious reason is the wrong one. Deferring does **not** give the missing word a chance to arrive: speech only ever appends to the buffer, and for a medial drop the position that word would have occupied is already behind the match. It can never land.

What deferring buys is exactly one thing: the decision happens once, at the flush, on a final transcript — **which is where the recogniser can ask you instead of guessing.** Turn `disambiguateSiblingTies` on and it does. Leave it off and the flush picks the first-registered pattern, exactly as it always has.

**What the warning reports, and what it does not.** It fires in the Editor at construction, naming the intents, the patterns as you wrote them, the element they differ at, and the competing values. It is deliberately narrow in three ways:

- **Same-intent patterns are not reported.** Two phrasings of one command dispatch the same intent whichever wins, so the tie is between things you made equivalent on purpose — `set` / `hold` / `keep` / `maintain distance {range}` is not a hazard.
- **Ties that cannot clear `minScore` are not reported.** Losing one required element from a pattern worth `D` leaves `(D − 1) / D`, so a two-element pattern falls to 0.5 — below the default `0.6`, where *both* siblings are rejected and nothing fires. That is a different problem from the wrong command firing, and saying "the wrong intent can fire" about it would be untrue. The threshold is the one you configured, so at `0.4` that same pair *is* live and *is* reported.
- **Ties whose discriminating word is every pattern's *first required element* are not reported.** Dropping that word triggers [the leading-required-miss bar](scoring.md#the-leading-required-miss-bar): whichever candidate wins the round missed its own first required element, so it is barred, the round yields nothing, and no command fires — nor does a disambiguation question open for that set. Saying "the wrong intent can fire" there would be false, as in the case above — though here the reason is positional rather than arithmetic. It takes **every** member of the set: where one pattern is anchored on the discriminator and another is not, the two still tie exactly, registration order can hand the round to the *unbarred* one, and the warning fires as before.

**The second exclusion tracks your threshold; the third reads none at all.** The recogniser hands the parser its configured `minScore` when it builds it, so the second is judged against the value that will actually gate rather than against a copy of the default (#140): lower `minScore` and the short pairs it makes live are reported, raise it and pairs that can no longer fire wrongly go quiet. The third is positional, so it holds at every `minScore` — where the discriminator leads **every** member of the set, no threshold makes the tie live, because whichever candidate wins the round is barred either way. That reach is exact: in a mixed set, where the discriminator leads only some of the members, the tie is live and a lowered `minScore` does reach it. The threshold is read when the parser is built, so an Inspector edit to `minScore` reaches these warnings at the next `RebuildParser` / `Configure` / `SetActiveSets` / `NotifySlotChanged` rather than on the next utterance — unlike the runtime gate of the same name, which is read fresh on every parse.

**Remedies**, in the order worth trying. Note that 2 and 3 are a **fork, not a ladder**: making the discriminator lead *every* pattern in the set means nothing fires on the tie, so there is never a pending for 3 to ask from or for 4 to confirm. Choose between silence and a question; 1 avoids the choice.

1. **Make the two commands differ in more than one element.** The only fix that removes the tie rather than managing it: with two differing words, losing one still leaves the other to decide. Prefer it whenever the phrasing is yours to choose.
2. **Make the differing word *every* pattern's *first required element*.** It has to be every one of them, not just one of the pair: where the discriminator leads them all, dropping it triggers [the leading-required-miss bar](scoring.md#the-leading-required-miss-bar): whichever of the two wins the tie missed its own first required element, so it is barred and the round yields nothing — **nothing fires instead of the wrong thing**, at any pattern length. `weapons mode` and `navigation mode` still tie at `(0 + 1) / 2`, and so do `weapons mode active` and `navigation mode active` at `0.67`; the difference is that neither pair can now fire on the tie. This converts a wrong command into silence, which is a real improvement but not a free one: the speaker must say the command again, it does nothing for an utterance where the discriminator *was* heard, and it forecloses remedies 3 and 4 for that pair. It also **silences the warning above** for that pair, which is correct — nothing can fire, so there is nothing left to report — but means a warning that disappears after you apply this is the remedy landing, not the pair going away.
3. **[Turn on `disambiguateSiblingTies`](#ambiguous-commands-ask-instead-of-guessing) and let the speaker settle it.** The only remedy that keeps both phrasings *and* gets the right command. Needs somewhere to put the question — see below. Note it cannot rescue the case in 2: whichever candidate wins that round is barred, so the round yields nothing and there is no pending to ask from.
4. **Give the more destructive of the pair `requiresConfirmation`**, so a coin flip costs a confirmation prompt rather than an action. Worth doing *alongside* 3, not instead of it: the two combine into "which did you mean?" then "are you sure?".
5. **Where both phrasings must exist verbatim and you cannot prompt, register the safer one first** — the tie-break is deterministic, so first-registered is what fires. This is choosing which way to lose, not a fix.

Remedy 2 is a reversal of earlier advice. Before the bar, moving the difference earlier genuinely did not help — the pair tied identically wherever the discriminating word sat, and a short pair fell under `minScore` only by accident of length. That accident is now a rule, and it holds at every length.

One further warning fires if a discriminating value is also cancel vocabulary. Follow-up handling checks cancel before anything else, so if that ambiguity is routed back to the speaker, answering with that word would cancel rather than choose it. It is judged against *your* `cancelVocabulary` if you set one, and against the defaults (`cancel`, `abort`, `negative`, …) if you did not — so overriding the vocabulary to dodge a collision actually silences the warning, and a collision you introduce *with* an override is reported. It is also only raised for values that could really be offered as an answer, which rules out three shapes: a value whose only same-set partners share its intent is never asked about; neither is a set whose discriminating word leads *every* pattern in it — remedy 2 above bars whichever member wins that round, so no question is ever posed for it; and neither is a set whose tie cannot clear your configured `minScore`, where both members are rejected on score, nothing fires and there is again no question for the answer to be swallowed (#140). The last two are the same conditions the warning above is narrowed by, so applying remedy 2 — or running a threshold the pair cannot clear — silences this warning along with that one.

### Do not leave a bare pattern's tail readable as another command

Sequential extraction offers whatever a winning command leaves behind to the next round, and a leftover tail can match the *tail* of some other intent's pattern — one whose leading word was never spoken. Say **"time to target track one two four four"** at a grammar holding `query_time_to_target : ["time","to","target"]` and `intercept_target : ["intercept","track","{track}"]`, and round 1 fires the query while round 2 finds `track one two four four` matching `intercept_target` minus its verb.

[The leading-required-miss bar](scoring.md#the-leading-required-miss-bar) stops that from firing, and it needs no configuration. But silence is not the same as being understood, and the bar cannot tell "the speaker never said it" from "the decoder dropped it" — so where the tail is a shape your speakers actually produce, fix it in the grammar. Three approaches, in the order worth trying:

1. **Let a legitimate pattern claim the tail.** Give the intent that *owns* those words a phrasing that reaches them: adding `time to target track {track}` as a second pattern of `query_time_to_target` makes the whole utterance one command scoring `1.00`, degrading to `0.80` when the decoder drops `track`. This is the best outcome, because the speaker gets the command they asked for rather than silence.
2. **Register a benign intent for the standalone fragment.** An intent on `["track", "{track}"]` covers the case where the tail arrives alone — after a pause longer than `bufferWindow`, say. It displaces the phantom on **score**, not on registration order, so it does not depend on declaration sequence.
3. **Put `requiresConfirmation` on destructive intents.** This covers the whole class rather than one shape of it, because it diverts on *consequence* rather than on match shape. Worth doing regardless of the other two.

All three work on any version and are worth applying whether or not you rely on the bar.

### Register each intent exactly once

An intent is the identity of a command, and the package treats it as one: **two `VoxrCommandDefinition`s under the same `Intent` are an authoring mistake**, whether they arrive in one `Configure(slots, commands)` call or in two command sets made active together. Both definitions' patterns stay live in the parse — either can win an utterance — but only one of them is reachable *back from the intent*, and the two lookups disagree about which.

```csharp
// Warned about -- one intent, two definitions: both patterns still match, but only
// one definition is reachable back from the intent
new VoxrCommandDefinition("fire_at", new[] { new[] { "fire", "at", "{target}", "now" } }),
new VoxrCommandDefinition("fire_at", new[] { new[] { "fire", "at", "{target}" } }),

// Safe -- the same two phrasings as two patterns of one definition
new VoxrCommandDefinition("fire_at", new[] {
    new[] { "fire", "at", "{target}", "now" },
    new[] { "fire", "at", "{target}" },
})
```

Note what is *not* wrong: selection walks the command list and never consults the intent lookup, so the second definition's patterns are matched and scored exactly like any other — "the second one never fires" is not what goes wrong here. What breaks is everything downstream of the parse, because the two places which resolve an intent back to a definition break the tie in *opposite* directions. The command-set lookup is a dictionary keyed on intent, so the **last** registration wins there; the follow-up re-score scans the command list and stops on the **first**. A `VoxrCommand` carries its `Intent` and `MatchedPatternIndex` but not the command that produced it, so every consumer re-derives the definition from the intent string and they can disagree -- `MatchedPatternIndex` applied to a pattern of a different length, a different unfilled-slot set for a follow-up to chase, a different `allowPartialMatch`, and a different `requiresConfirmation`. That last one is the one to state plainly: with two definitions under one intent, one of them `requiresConfirmation`, whether a destructive command asks before firing depends on registration order rather than on the command that matched.

**Two registrations that are identical are a different mistake, and get a different warning.** If the two definitions the lookups reach are ones no consumer could tell apart -- the same patterns in the same order, the same `allowPartialMatch`, the same `requiresConfirmation` -- then nothing disagrees, because both resolutions land on the same thing. What remains is that only one registration is reachable from the intent and each extra copy adds a parse candidate that ties the original exactly, so registration order breaks a tie between a command and itself. The two usual causes are a set named twice in one `SetActiveSets` call (or in `initialActiveSetNames`), and one command asset placed in two sets that are active together -- neither is caught anywhere else, since `Activate` concatenates the active sets without de-duplicating. The remedy is to remove the duplicate registration; merging patterns does not apply, because there is only one distinct definition.

Per-intent debounce is keyed on the intent too, so duplicate definitions share one cooldown.

The warning names the intent, how many definitions carry it, and the first pattern of each of the two the resolutions reach -- or, for identical registrations, of the one definition involved. Like the two scans above it is **Editor-only**. Intents are compared ordinally, matching both lookups -- `fire_at` and `Fire_At` are distinct commands, not duplicates.

---

## Ambiguous Commands: Ask Instead of Guessing

Everything above stops the recogniser committing to a coin flip early. It does not stop it coin-flipping at the end — the flush still picks the first-registered pattern. **`disambiguateSiblingTies` is what replaces that guess with a question — with one exception: where the discriminating word is **every** competing pattern's **first required element** — two members or ten — dropping it [bars](scoring.md#the-leading-required-miss-bar) whichever candidate wins the round, so nothing fires and no pending opens for that set. That case is remedy 2 above, and this flag does not reach it.** Wherever that exception does not apply — the discriminating word is not **every** competing pattern's first required element — the round can resolve normally — it does when the member the bar does **not** touch is the one registration order hands it to — and a member whose *own* first required element went unheard can then be offered among the choices and fire if it is picked — see [Known Limitations](../KNOWN_LIMITATIONS.md).

**This is independent of `eagerFlushOnCompleteMatch`.** The refusal described above is an eager-gate rule and only applies when you have turned eager flush on; the flag below acts on the *flush* path, which every utterance takes. Eager flush is off by default — that does not exempt you from this hazard, and does not stop the flag fixing it.

```csharp
// Inspector: Follow-Up / Pending Commands > Disambiguate Sibling Ties
set_mode  : ["set", "{ship}", "mode",  "on"]
set_level : ["set", "{ship}", "level", "on"]
```

The speaker says "set alpha mode on". VOSK drops `mode`. With the flag off, `set_mode` fires because it was declared first — and would have fired even if they had said `level`. With the flag on:

1. Nothing fires. `OnCommandPending` raises, carrying the candidate that *would* have fired.
2. `PendingAmbiguity` is non-null, and carries the competing commands with the one word that tells each apart: `mode` or `level`.
3. You prompt however suits your game — this package ships no speech synthesis and no UI.
4. The speaker says **`level`**, one word. `set_level` fires with its slots intact (`ship = alpha`), through `OnCommandConfirmed`, then `OnCommandRecognised`, then `OnCommandsRecognised` (a one-element batch).

**If the chosen command sets `requiresConfirmation`, step 4 asks again instead of firing** — "which?" first, then "are you sure?", which is the only coherent order, since you cannot confirm an intent you have not identified. `OnCommandPending` raises a second time, `PendingAmbiguity` is now null (this question is a confirmation), and the confirm vocabulary resolves it. Worth planning for: remedy 4 above tells you to mark the more destructive sibling `requiresConfirmation`, so taking both remedies together is exactly what produces this two-stage exchange.

The answer needs no grammar work: discriminating values are pattern literals, so the decoder already knows them.

**Off by default, and that is not timidity.** The flag makes an ambiguous utterance fire *nothing* until it is answered. With no `OnCommandPending` subscriber the speaker is never prompted, the pending times out, and the command is lost — worse than the coin flip it replaced. Turn it on when you have somewhere to put the question.

### What answering looks like

- **A discriminating value** picks that choice. Matched as a *whole* utterance, so "set alpha mode on" is a re-utterance that preempts the question, not an answer to it — both work, they just take different routes.
- **Cancel** works, and keeps its precedence. A value that is also a cancel word cancels rather than choosing; the construction warning above tells you when your grammar has one.
- **"Yes" does nothing.** It is not an answer to "which?" — but it is not an abandonment either, so the question stays open for the real answer.
- **Silence** cancels. Even with `pendingTimeoutBehavior = FireAsIs`: there the *intent* is unknown rather than the arguments, and firing the first-registered after a pause is the same coin flip, merely later.
- **Saying it all again** works, and preempts the question. A second *ambiguous* utterance re-asks it. Either way the superseded question is cancelled first, so `OnCommandCancelled` precedes the new command's events -- or the fresh `OnCommandPending`.

### Three or more, and what does not fit

A sibling set is n-ary: `set auto pilot on` / `off` / `standby` offers three choices, and each one-word answer fires its own intent. The runtime offers the winner plus up to four alternatives.

Past that — or where the winner is ambiguous in two different ways at once, or where a pattern carries too many optional elements to analyse fully — some intent that also matched will not be on the list. `PendingAmbiguity.IsTruncated` tells you, so you can word "…or say the whole command again", which is the only way to reach what is missing.

### Push-to-talk: one setting to check

Under push-to-talk, **Cancel Pending On Release** discards the question the instant it is raised — release flushes, and the flush is what creates it. Leave that setting off and the question survives for the speaker to answer on their next press. See [Cancel Pending On Release](push-to-talk.md#cancel-pending-on-release) for the ordering and the timer arithmetic.

---

## Pending Commands

Sometimes a command partially matches (some required slots are unfilled), or needs explicit confirmation before a high-consequence action fires, or cannot be told apart from another command at all. The pending command system handles all three by holding the command in a "pending" state and listening for follow-up speech.

The third kind is [ambiguity](#ambiguous-commands-ask-instead-of-guessing), and it only ever arises with `disambiguateSiblingTies` enabled. Everything in this section applies to it — preemption, cancellation, `CancelPendingCommand()` — **with one exception, called out under Timeout Behaviour below.**

### Partial Match with Follow-Up Slot-Fill

Set `allowPartialMatch: true` on a command definition to let it enter pending state when matched with unfilled required slots, instead of being refused. The diversion is decided by completeness alone, independently of `minScore` -- a command scoring `0.8` with a missing argument routes to pending exactly as a sub-threshold one does. One thing is consulted before completeness: [the leading-required-miss bar](scoring.md#the-leading-required-miss-bar). A winner that missed its own first required element is refused outright, so it never reaches this diversion at any score.

```csharp
var launchCmd = new VoxrCommandDefinition("launch_weapon",
    new[] { new[] { "launch", "{weapon}", "target", "{target}" } },
    allowPartialMatch: true);
```

If the user says "launch missiles" without specifying a target, the command enters pending state and `OnCommandPending` fires. The system then listens for follow-up speech. If the user says "hotel one" within the `pendingTimeout` window, the target slot is filled and the command fires via `OnCommandConfirmed`, then `OnCommandRecognised`, then `OnCommandsRecognised` (a one-element batch).

**Several missing slots take several utterances if they have to.** Follow-up speech fills the unfilled slots in pattern order and stops at the first one it cannot find, so an utterance that answers only part of what is missing leaves the command *still pending* — with what it just filled kept, and `OnCommandPending` fired again carrying the updated command. It fires only once no required slot is left. A prompt driven off `OnCommandPending` therefore sees each fill as it lands and can name what is still outstanding. Nothing is lost by answering one slot at a time, and each fill restarts the `pendingTimeout` window: `pendingTimeout` bounds how long the command waits for *you*, so answering buys another window rather than eating into a fixed one. What ends a stalled exchange is silence — the first window nobody answers. The same restart applies when the last slot lands on a command that also sets `requiresConfirmation`: the confirmation stage begins its own window.

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

Default confirm vocabulary: "confirm", "affirmative", "yes", "go ahead", "do it". Default cancel vocabulary: "cancel", "abort", "negative", "belay that", "never mind". Override these with the `confirmVocabulary` and `cancelVocabulary` Inspector arrays on `VoxrCommandRecogniser`. The *matcher* reads the live arrays on every utterance, so a changed vocabulary is honoured immediately. What is frozen when the parser is built: the construction-time cancel-collision warning's copy of the cancel vocabulary, the `minScore` both that warning and the sibling warning beside it are judged against, and `disambiguateSiblingTies` (in a player build -- the Editor records ties regardless). And the decoder *grammar* learns new words only when it is rebuilt -- `Configure`, `SetActiveSets`, or `RebuildGrammar` -- so a novel overridden word is matched as an answer at once but may not be *decodable* until then.

Follow-up speech is checked against a live pending in a fixed order: **cancel first** (under every reason -- a cancel word always cancels), then a disambiguation choice, then a confirm phrase, then slot-fill. Because confirm outranks slot-fill, a confirm phrase also resolves a pending command that is waiting on slots rather than on confirmation — see below.

### Combined Partial + Confirmation

A command with both `allowPartialMatch` and `requiresConfirmation` goes through two pending stages: follow-up speech fills missing slots first; once nothing required is left, the confirmation stage begins with a fresh `pendingTimeout` window. A confirm phrase spoken *during* the slot-fill stage does not skip to stage two -- it fires the command immediately, as it stands, absent slots and all (see [the two ways an incomplete command still fires](#the-two-ways-an-incomplete-command-still-fires)).

### Timeout Behaviour

Configure `pendingTimeout` (default 5s) and `pendingTimeoutBehavior` on `VoxrCommandRecogniser`:

- **Cancel** (default) -- the pending command is discarded and `OnCommandCancelled` fires.
- **FireAsIs** -- the pending command fires with whatever slots were filled, even if some are still missing.

**`FireAsIs` does not apply to a pending ambiguity, which always cancels.** The two settings answer different questions: `FireAsIs` means "the command is known, fire it with what I have", and under an ambiguity the *command itself* is what is unknown. Firing the first-registered candidate after a pause would be the same coin flip the question was asked to avoid, arriving later.

### The two ways an incomplete command still fires

A command missing a required argument does not fire on the ordinary path: it is refused outright, or, with `allowPartialMatch`, held pending for slot-fill. (A third case reaches neither branch: a winner that missed its *first required element* is [barred](scoring.md#the-leading-required-miss-bar) before completeness is consulted, so it is not rejected for incompleteness and not held pending either — the round simply yields nothing.) Two opt-ins deliberately override the ordinary path, and both hand your handler a command with arguments absent:

- **Confirming a partial match.** Saying a confirm phrase while a command waits for slot-fill fires it as it stands. The confirm check runs before slot-fill, and it is taken at face value — the user has been shown what is missing (via `OnCommandPending`) and said go anyway.
- **`FireAsIs` on timeout.** The name is the contract: whatever was filled when the window closed is what fires.

**Both have the same precondition: `allowPartialMatch`.** Only a partial-match pending ever holds a command with a required slot still absent — a pending that is merely awaiting confirmation was complete when it entered, because the completeness rule refused it otherwise. So `pendingTimeoutBehavior = FireAsIs` on its own cannot fire an incomplete command; it needs a command that opted into partial matching to have one to fire.

Set `allowPartialMatch` on a command and its handler **must** tolerate every required slot being absent — via a confirm phrase, which needs no second opt-in, or on timeout if `pendingTimeoutBehavior` is `FireAsIs`:

```csharp
commandRecogniser.OnCommandConfirmed += cmd =>
{
    // Do not assume a slot is present just because the pattern requires it.
    if (!cmd.HasSlot("target")) { PromptForTarget(); return; }
    Launch(cmd.GetSlot("target"));
};
```

Test with `HasSlot`, not with the value: `GetSlot` returns `string.Empty` for a slot that was never filled, so a null check will not catch it.

### Preemption

If a new complete command is recognised while a command is pending, the pending command is cancelled and the new command fires normally. This prevents stale pending commands from blocking normal operation.

The order is worth knowing for prompt UIs: the superseded pending is cancelled *first*, so `OnCommandCancelled` fires before the new command's events -- and before the fresh `OnCommandPending` when the new utterance is itself ambiguous or incomplete. An *incomplete* new command whose definition does **not** allow partial matching never preempts: taking a half-finished command away to put nothing in its place would be a loss, so the live pending stays. An incomplete command that *does* set `allowPartialMatch` enters pending itself and displaces the old one -- cancel first, as above. An utterance whose only winner was [barred](scoring.md#the-leading-required-miss-bar) does not preempt either, for the same reason and by the same general rule: it produces no accepted command, so there is nothing to put in the pending's place. It also does not cancel a live pending or interrupt follow-up slot-fill.

### Grammar Integration

Confirm and cancel vocabulary is merged into the VOSK grammar JSON **word by word**, so it is recognised reliably in grammar mode. A multi-word phrase like "belay that" contributes `belay` and `that` as individual entries -- unlike pattern literals, follow-up phrases get no multi-word phrase entry to bias their word order (see [What the grammar contains](#what-the-grammar-contains)). An overridden vocabulary is merged the same way, so a novel phrase of your own is decodable too. The two layers treat an override differently: the *matcher* uses your arrays **instead of** the defaults, while the *grammar* keeps the default words **alongside** yours -- so a default word stays audible to the decoder even though it is no longer accepted as an answer.

### Programmatic Control

Call `CancelPendingCommand()` to cancel the pending command from code (e.g. on a scene transition or mode switch). Check `HasPendingCommand` and `PendingCommand` to inspect the current pending state, and [`PendingAmbiguity`](api/data-types.md#voxrpendingambiguity) to tell an ambiguity from a confirmation — `OnCommandPending` carries no reason of its own, so that property is how you know which question you were asked.

Two lifecycle interactions worth knowing:

- **Reconfiguration cancels a live pending.** `Configure` (either overload), `SetActiveSets`, and disabling the component all discard the pending command and raise `OnCommandCancelled` -- so a mode switch mid-confirmation produces a cancellation your handler should expect.
- **`RebuildGrammar()` defers while a command is pending.** Rebuilding the decoder grammar would destroy the utterance the pending is waiting on, so the rebuild is queued silently and applied when the pending resolves. A `RebuildGrammar()` call that appears to do nothing is usually this.

---

## Unrecognised Speech

When speech passes through the pipeline but no command is produced, `OnUnrecognisedSpeech` fires with the raw transcript. This happens in five situations:

1. **No pattern match** -- the parser could not match any command pattern against the transcript.
2. **Every match fell under `minScore`** -- patterns matched, but no candidate scored high enough to fire.
3. **A match was missing a required argument** -- the command may have scored well, but a required slot went unfilled and the command does not set `allowPartialMatch`. The completeness rule is independent of score, so this is the one case where "unrecognised" does not mean "scored badly".
4. **The winning candidate's first required element was never heard** -- the round's winner was [barred](scoring.md#the-leading-required-miss-bar). It may have scored well above `minScore`; the refusal is positional, not arithmetic, and the round leaves no session-log attempt behind.
5. **A follow-up fill was refused for re-scoring at or below zero** -- follow-up speech completed a pending command, but the re-score landed at or below zero, so the same floor the flush paths apply refused it (see [the session-log table](scoring.md#reading-a-session-log)). The pending is left standing. Reaching this at all means two definitions share one intent.

It does **not** fire in three others: a candidate rejected by `minConfidence`, one suppressed by `commandCooldown` debounce, or one diverted to a pending of **any** kind -- partial-match, confirmation, or disambiguation alike. The first two are silent on the reasoning that the user did say a valid command, just not confidently or not soon enough after the last one. A pending is silent because telling you the speech was not understood, in the same frame you were asked to prompt the speaker about it, is a contradiction: the prompt is the recogniser saying it understood enough to ask ([#133](https://github.com/jinwoo1601/VoXR-Speech-Recognition/issues/133)). See [the gates](scoring.md#what-onunrecognisedspeech-actually-means) for the full table.

The `string` parameter is the full buffered transcript (after utterance merging), exactly as VOSK transcribed it.

### When it does not fire

- If `Configure` has not been called -- or the set-based `Configure(slots, sets)` overload was called without a following `SetActiveSets()` -- speech is silently dropped and no events fire.
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

That recipe assumes the command leaves `allowPartialMatch` off, which is the default. With the flag **on**, an excluded value is an unfilled required slot, so the utterance opens a slot-fill pending and is *not* reported unrecognised -- put the same message on `OnCommandPending` instead, testing the pending command with `HasSlot("target")` to see which argument went missing.

### Relationship to other events

`OnUnrecognisedSpeech` and `OnCommandRecognised`/`OnCommandsRecognised` are mutually exclusive per utterance: a transcript that produces at least one accepted command never also fires `OnUnrecognisedSpeech`.

The converse does not hold. A transcript that produces *no* accepted command is silent when a candidate was filtered by `minConfidence`, suppressed by debounce, or diverted to a pending of any kind, so "no command fired" and "`OnUnrecognisedSpeech` fired" are not the same condition.

---

## Grammar Mode vs Free Speech

By default, `VoxrCommandRecogniser` constrains VOSK's decoder to only the words that appear in registered commands and slots. This is **grammar mode**, and it dramatically improves recognition accuracy for command-driven UX.

Enabling **Free Speech Mode** in the Inspector disables the grammar constraint, allowing VOSK to recognise any word in its vocabulary. Command matching becomes best-effort.

### What the grammar contains

The grammar is not just a bag of words. Each **contiguous run of required literals** in a pattern, and each **multi-word slot value or alias**, is emitted as a single multi-word entry, alongside the individual words:

```csharp
new[] { "close", "distance", "{range}", "target", "{target}" }
// phrase entries: "close distance" (a one-word run like "target" yields none),
// plus the single words "close", "distance", "target",
// plus each {range}/{target} surface form ("safe range", "hotel one", ...)
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

Use grammar mode (the default) for all command-driven features. Only enable free speech when your feature genuinely needs arbitrary vocabulary, and accept that command matching will be best-effort in that mode. **The mode is an authoring-time choice, not a runtime switch**: `freeSpeechMode` is a serialized Inspector field with no public accessor, and the command layer never lifts an applied grammar itself -- treat the mode as fixed. (The low-level escape hatch exists: `VoxrSpeechRecogniser.SetGrammar` with an empty grammar, while stopped, puts the *decoder* in free dictation -- but the command recogniser re-applies its grammar on its next rebuild, so it is not a supported mode switch.)

---

## See Also

- [Matching and Scoring](scoring.md) -- The score formula, coverage, selection order, gates, and eager-flush verdicts in full
- [Command Sets](command-sets.md) -- Group commands into switchable named sets for mode-specific grammars
- [Number Parser](api/number-parser.md) -- Convert a `NumberSequence` slot's spoken words into an integer
- [Inspector Authoring](inspector-authoring.md) -- Define commands and slots with ScriptableObject assets instead of code
- [Editor Testing](editor-testing.md) -- Test commands with the debug window, session debug log, text injection, and batch runner
- [Known Limitations](../KNOWN_LIMITATIONS.md) -- VOSK model quirks, homophones, and recognition edge cases
