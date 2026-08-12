# Matching and Scoring

The reference for the model that decides whether a spoken command fires: how a candidate match is scored, which candidate wins when several match, and what the two acceptance gates do with the winner.

Read this when you are tuning `minScore` / `minConfidence`, diagnosing why a command was rejected, interpreting a [session debug log](editor-testing.md#session-debug-log), or authoring patterns that must not shadow each other. For the surrounding pipeline — buffering, pending commands, grammar mode — see [Command Recognition](command-recognition.md).

Everything here describes `VoxrCommandParser`, which is deterministic: the same transcript against the same command set always produces the same result.

---

## Vocabulary

| Term | Meaning |
|------|---------|
| **Token** | One whitespace-separated word of the transcript. `[unk]` is VOSK's token for audio it could not resolve to a grammar word. |
| **Element** | One entry of a pattern array: a required literal (`"target"`), an optional literal (`"?by"`), a required slot (`"{weapon}"`), or an optional slot (`"{?quantity}"`). |
| **Candidate** | One (command, pattern, start token) triple that the parser scored. Every pattern of every active command is tried at every non-`[unk]` start position. |
| **Winner** | The single candidate selection picks per extraction round. Only winners reach the gates, and only winners are logged as a scored attempt — losing candidates are recorded nowhere. |

---

## 1. The score formula

Each element of the pattern contributes to a numerator (**raw score**) and a **denominator**. The score is their ratio:

```
score = rawScore / denominator          (0 when denominator is 0)
```

| Element | Outcome | Raw score | Denominator |
|---------|---------|-----------|-------------|
| Required literal (`"target"`) | matched | +1.0 | +1.0 |
| Required literal | **missed** | **−0.5** | +1.0 |
| Required slot (`"{weapon}"`) | matched | +1.0 | +1.0 |
| Required slot | **missed** | **−1.0** | +1.0 |
| Optional literal (`"?by"`) | matched | +0.5 | +0.5 |
| Optional literal | omitted | 0 | 0 |
| Optional slot (`"{?quantity}"`) | matched | +1.0 | +1.0 |
| Optional slot | omitted | 0 | 0 |

Three properties follow, and each one matters when you read a score:

- **The denominator is dynamic.** Required elements always count toward it; optional elements count only when they were actually spoken. An omitted optional drops out of *both* sides of the ratio rather than diluting it, so taking advantage of optionality is never penalised and a perfect match is always exactly `1.0`.
- **A miss is charged twice** — it withholds the credit *and* subtracts a penalty, while still occupying the denominator. That is what makes a single dropped word expensive.
- **A matched optional literal is worth half a required one.** Making a literal optional therefore changes the arithmetic of every *imperfect* match of that pattern, not just the ones that drop the word. See [the cost of `?by`](command-recognition.md#never-leave-a-required-function-word-between-a-bare-pattern-and-its-slot).

A candidate whose score is `0` or negative is discarded and never competes.

### Short patterns are disproportionately fragile

The penalty is a fixed size but the denominator is not, so the same dropped word costs a short pattern far more than a long one:

| Pattern | Utterance | Raw / denominator | Score | At `minScore = 0.6` |
|---------|-----------|-------------------|-------|---------------------|
| `decelerate by {burn_level}` (3 elements) | "decelerate hard burn" | `(1 − 0.5 + 1) / 3` = `1.5 / 3` | **0.50** | rejected |
| `launch {weapon} target {target} on my mark` (7 elements) | "launch missiles hotel one on my mark" | `(6 × 1 − 0.5) / 7` = `5.5 / 7` | **0.79** | accepted |

Both utterances dropped exactly one required literal, and in both the slots were recognised and extracted. Only the short one falls under the gate. This is the single most common cause of a puzzling `score 0.50 < minScore 0.60` line in a session log: it is not a garbled utterance, it is one missing function word on a three-element pattern.

The lesson for authoring is not "raise `minScore`" but "do not make a short pattern depend on a short unstressed word" — see [the function-word hazard](command-recognition.md#never-leave-a-required-function-word-between-a-bare-pattern-and-its-slot), which the parser also warns about at construction.

---

## 2. The skipped-word penalty

The parser slides its start point through the utterance, so a pattern can match anywhere in it. `skippedWordPenalty` (default `1.0`) charges the winner for the in-grammar words that sliding start walked past:

```
finalScore = rawScore / (denominator + skippedWords × skippedWordPenalty)
```

Four rules govern it, and all four are load-bearing:

- **It is applied after selection, not during it.** Candidates are compared on their unpenalised scores; the penalty is then applied to the winner alone. So the penalty *filters* through `minScore` — it never changes which pattern wins.
- **`[unk]` is never charged.** Out-of-grammar preamble and hesitation are exactly what the sliding start is for, so filler VOSK could not resolve stays free.
- **Counting restarts at each extraction round.** Words consumed by a previously extracted command are not charged against the next one, so chained commands in one utterance do not penalise each other.
- **It is proportional.** It only bites patterns short enough to be swallowed whole by a stray utterance.

| Utterance | Winner | Unpenalised | Skipped | Final |
|-----------|--------|-------------|---------|-------|
| `report` | `["report"]` | 1.00 | 0 | **1.00** |
| `thrusters report` | `["report"]` | 1.00 | 1 | `1 / (1 + 1)` = **0.50** — rejected |
| `[unk] [unk] report` | `["report"]` | 1.00 | 0 (`[unk]` is free) | **1.00** |
| `launch launch missiles target hotel one` | 4-element `launch_weapon` | 1.00 | 1 | `4 / (4 + 1)` = **0.80** |

Set it to `0` to restore the pre-1.4.0 behaviour where skipped words cost nothing; raise it above `1.0` to demand a command be an even larger share of what was said.

---

## 3. Selection: which candidate wins

Every candidate with a positive score competes. They are ordered by these keys, in this order:

1. **Earliest start token wins.** A candidate that begins earlier beats one that begins later *regardless of score* — a leading match is never displaced by a better-scoring one further along.
2. **Then highest score** (before the skipped-word penalty).
3. **Then the longer consumed span** — how far the last element that *actually matched something* reached. Trailing `[unk]` the pattern merely skipped does not count, so a candidate cannot win by absorbing noise.
4. **Then the most matched literals.**
5. **Then registration order** — the first-declared command wins, and within a command the first-listed pattern. This is a deterministic fallback, not a design surface; do not build behaviour on it.

Key 3 is why a pattern with a tail beats its bare sibling. With `intercept track {track}` declared *before* `intercept track {track} {burn_level}`, "intercept track hotel one hard burn" scores `1.0` on both with equal literal counts — but the longer one consumed 6 tokens against the bare one's 4, so it wins and extracts both slots. Without that key, the bare pattern would win on declaration order and sequential extraction would then match the orphaned `hard burn` as a *second* command, splitting one order in two.

Note that key 3 sits **above** literal count, so it also settles equal-score candidates whose literal counts differ.

**`TryEagerCommit` uses this same ordering**, so an eager verdict always names the command the subsequent flush will actually fire.

---

## 4. Sequential extraction

One utterance can yield several commands. After a winner is chosen, the search restarts from the token where that winner ended and repeats:

```
"cease fire launch missiles target hotel one"
  -> cease_fire                                        score 1.00
  -> launch_weapon(weapon=missiles, target=hotel one)  score 1.00
```

Extraction stops when no candidate scores above `0`, when a match would consume no tokens, or when the result buffer (one slot per active command) is full.

Two consequences for pattern authoring:

- **A pattern that is a prefix of another can steal its head.** If the shorter one wins a round, the remainder of the utterance is offered to the next round — where it may match a *different* command instead of being read as the tail it was meant to be. The span tie-break (key 3 above) prevents this for the equal-score case; it does not help when the longer form is scoring lower for another reason.
- **Each command is scored and gated independently.** One command in an utterance can fire while another from the same utterance is rejected.

---

## 5. The two gates

The winner of each round faces two independent thresholds. Both live on `VoxrCommandRecogniser`.

### `minScore` (default `0.6`)

Compared against the final score from §1 + §2. Rejects partial and garbled matches.

If the command definition sets `allowPartialMatch`, a sub-threshold match with unfilled required slots enters the [pending state](command-recognition.md#pending-commands) instead of being rejected outright.

### `minConfidence` (default `0.4`)

Compared against `VoxrCommand.Confidence`, which is the **minimum** per-word VOSK acoustic confidence over the matched span, ignoring `[unk]` tokens.

Minimum, not average — this is the property that surprises people. One weak word vetoes an otherwise-perfect command:

```
"orient heading two seven zero"
  orient  0.94
  heading 0.39   <-- the minimum, so aggregateConfidence = 0.39
  two     0.50
  seven   0.91
  zero    0.97
```

That command scores a clean `1.00` and is still rejected at the default `0.4` — and note the culprit is not the word you would suspect. "two" came in at its usual ≈0.50, comfortably above the gate; the veto came from a word nobody was watching.

**Repeated words resolve by text, not by position.** The per-word table is built once per utterance, keyed by word *text*, keeping each distinct word's **first** occurrence. Every token in the span is then looked up by text. So if a word appears more than once in the utterance, every occurrence is scored at the first one's confidence — even when that first occurrence lies *outside* the matched span. Two consequences worth knowing:

- A weak repeat inside the span can be masked by a strong earlier one ("orient heading two **two** zero" reports the first "two"'s confidence for both).
- A weak word *before* the match can drag the reported confidence down, though it is not part of the command.

This matters most for `NumberSequence` slots, which are the commands most likely to repeat a word.

**`-1` means "no data", not "zero confidence".** It means no per-word confidence was available *for the matched span*. Usually that is because the utterance carried no word data at all — injected text, where the `words` array is empty too. It can also happen with `words` populated, when the matched span came from a segment that carried none: the utterance buffer appends text unconditionally but words only when a result supplies them, so a buffer merging a spoken result with an injected one can match on the half that has no word data. Either way the `minConfidence` check is **bypassed entirely** and the command is accepted or rejected on score alone. Treat `-1` as *n/a* in any debug UI — never as a low value.

### Tuning them jointly

- Start at the defaults and move one at a time; they filter different failure modes. Low score = the *words* did not fit the pattern. Low confidence = VOSK was not sure it heard the words at all.
- **Do not push `minConfidence` above ~0.5.** "two" scores ≈0.50 essentially always with the small English model, so anything higher rejects every `NumberSequence` command containing it. See [Known Limitations](../KNOWN_LIMITATIONS.md).
- Raising `minScore` above ~0.7 makes three-element patterns require a perfect transcript (§1). Prefer fixing the pattern.
- Regression-test any change with the [Batch Test Runner](api/batch-test-runner.md) rather than by ear.

### The filters after the gates

A command that clears both gates can still not fire. In order:

| Filter | Effect | Session-log `rejectReason` |
|--------|--------|----------------------------|
| Per-intent debounce (`commandCooldown`) | Suppressed if the same intent fired within the window | `debounced (0.3s cooldown)` |
| `requiresConfirmation` | Enters pending, fires on confirmation | `entered pending (awaiting confirmation)` |

And below `minScore`, `allowPartialMatch` diverts to pending rather than rejecting: `entered pending (partial: unfilled [...])`.

> The numbers inside a `rejectReason` are formatted with the **Editor's current culture**, so on a comma-decimal locale the field reads `score 0,50 < minScore 0,60`. If you grep a session log, match on the surrounding words rather than the whole literal. The numeric `score` / `aggregateConfidence` *fields* are unaffected — JSON numbers are written invariantly.

### What `OnUnrecognisedSpeech` actually means

It does **not** mean "nothing matched". It fires whenever an utterance produced no accepted command, *except* when some candidate was dropped by `minConfidence` or by debounce — those two are the only filters that suppress it:

| Outcome | `OnUnrecognisedSpeech` |
|---------|------------------------|
| No pattern matched at all | fires |
| Every candidate fell under `minScore` | **fires** |
| A candidate was diverted to pending (partial match or `requiresConfirmation`) | **fires**, alongside `OnCommandPending` |
| A candidate was rejected by `minConfidence` | silent |
| A candidate was suppressed by debounce | silent |

So the event is not a reliable "I heard nothing" signal: the score-rejection rows of §7 raise it too. If you show the player feedback on it, expect it after a half-heard command as well as after noise.

---

## 6. Eager-flush verdicts

When `eagerFlushOnCompleteMatch` is enabled, each VOSK result triggers one speculative parse of the buffer that returns one of three verdicts. (It is skipped while a command is pending, so confirm and follow-up speech stay on the timer path.) The scan reuses the selection order from §3, so its verdict names the command a flush would fire.

| Verdict | Meaning | Buffer behaviour |
|---------|---------|------------------|
| `Commit` | Complete, confident, and **unextendable** | Flush and fire now |
| `HoldExtendable` | Complete and confident, but more speech could still extend it | Wait `prefixHoldSeconds` (if set and shorter than `bufferWindow`), else the full window |
| `None` | Not a complete confident match of the whole buffer | Wait the full `bufferWindow` |

A verdict above `None` requires **all** of:

1. The winner's score ≥ `minScore` — computed *without* the skipped-word penalty, which is sound only because of condition 2.
2. The match starts at the first **recognised** token. A leading `[unk]` run is skipped for free — nothing arriving later extends an utterance leftward, so out-of-grammar preamble ("Helm, ...") does not block a commit. A leading word VOSK *did* resolve does block it, because the flush would then charge it under §2 and could score below `minScore`.
3. The match reaches the **end** of the buffer. Anything left over — recognised or `[unk]` — is treated as an in-progress tail.
4. Every **required slot** in the winning pattern actually matched. Condition 3 does not imply this: a missed slot consumes no tokens, so it never moves the end of the match, and a pattern can appear to span the buffer while still missing an argument. Required *literals* are exempt — a dropped function word leaves the command fully determined.
5. Confidence ≥ `minConfidence`, or `-1`.

`Commit` additionally requires the winning pattern to be *terminal*: its last element cannot grow (not a trailing optional, not a variable-width `NumberSequence`, not an enumerated slot with a value that is a word-prefix of another value), and no concrete form of it is a prefix of any concrete form of another pattern.

With `["fire"]` and `["fire", "at", "{target}"]` registered:

| Buffer | Verdict | Why |
|--------|---------|-----|
| `fire` | `HoldExtendable` | complete, but a prefix of `fire at {target}` |
| `fire at` | `None` | bare `fire` wins selection (`1.00` vs `0.33`) but leaves `at` unconsumed |
| `fire at hotel one` | `Commit` | complete, terminal, spans the buffer |
| `[unk] fire at hotel one` | `Commit` | leading `[unk]` is skipped for free |
| `fire at hotel one [unk]` | `None` | trailing leftover = possible in-progress tail |

**The `MaxOptionalExpansion` guard.** Deciding terminality means expanding a pattern over its optional elements, which is exponential (2^optionals). A pattern carrying more than **12** optional elements is refused rather than partially analysed — and because a partial analysis could commit the *wrong* command, the refusal covers the whole command set. Nothing then commits early; every complete match degrades to `HoldExtendable`. The parser names the offending pattern, its intent, and its optional count in a construction-time warning.

---

## 7. Worked examples

Each trace ends with the entry it produces in the [session debug log](editor-testing.md#session-debug-log), abridged to the fields under discussion.

### A. A clean multi-slot command

Grammar: `launch_weapon` = `["launch", "{weapon}", "target", "{target}"]`, with `weapon = {missiles, …}` and `target = {hotel one, …}`.

Utterance: **"launch missiles target hotel one"** — 5 tokens.

1. **Candidates.** The pattern is tried at every start token. Start 0 matches all four elements: `4 / 4` = `1.00`. Start 1 misses the `launch` literal but still matches the other three: `2.5 / 4` = `0.63`. Start 2 misses both `launch` and `{weapon}`: `0.5 / 4` = `0.13`.
2. **Selection.** Start 0 is earliest — it wins on key 1 alone.
3. **Skipped-word penalty.** The winner starts at the search origin, so nothing is skipped. Score stays `1.00`.
4. **Confidence.** The minimum per-word confidence over tokens 0–4.
5. **Gates.** `1.00 ≥ 0.6`; confidence compared against `0.4`.

```json
{ "intent": "launch_weapon", "pattern": "launch {weapon} target {target}",
  "score": 1.0, "minScore": 0.6, "accepted": true, "rejectReason": "",
  "slots": [ { "name": "weapon", "value": "missiles",  "startWord": 1, "endWord": 2 },
             { "name": "target", "value": "hotel one", "startWord": 3, "endWord": 5 } ] }
```

### B. A dropped function word sinks a short pattern

Grammar: `decelerate` = `["decelerate"]` **and** `["decelerate", "by", "{burn_level}"]`.

Utterance: **"decelerate hard burn"** — the speaker said the burn level; VOSK dropped "by".

1. **Candidates.** The bare pattern at start 0: `1 / 1` = `1.00`. The slot-filled pattern at start 0: `by` is missed (−0.5, denominator +1) while `decelerate` and `{burn_level}` match, giving `1.5 / 3` = `0.50`.
2. **Selection.** Both start at token 0, so key 1 ties and key 2 decides: `1.00` beats `0.50`. **The bare pattern wins.**
3. **Result.** `decelerate` fires with **no slots**. The `hard burn` the speaker actually said is discarded, and nothing in the log says a slot was lost — the accepted entry simply has an empty `slots` array.

```json
{ "intent": "decelerate", "pattern": "decelerate",
  "score": 1.0, "accepted": true, "slots": [] }
```

No threshold reaches this: the bare pattern scores a clean `1.00`, which nothing normalised to `1.00` can beat. The fix is `"?by"`, which makes the slot-filled form score `2 / 2` = `1.00` too and win on consumed span (key 3). Then:

```json
{ "intent": "decelerate", "pattern": "decelerate ?by {burn_level}",
  "score": 1.0, "accepted": true,
  "slots": [ { "name": "burn_level", "value": "hard burn", "startWord": 1, "endWord": 3 } ] }
```

If instead the intent has *only* the slot-filled pattern, there is no bare sibling to win — the `0.50` candidate is the winner, and is then rejected by the gate:

```json
{ "intent": "decelerate", "pattern": "decelerate by {burn_level}",
  "score": 0.5, "minScore": 0.6, "accepted": false,
  "rejectReason": "score 0.50 < minScore 0.60",
  "slots": [ { "name": "burn_level", "value": "hard burn", "startWord": 1, "endWord": 3 } ] }
```

**Reading that entry:** a `0.50` on a three-element pattern whose slots *did* extract is the signature of exactly one missing required literal. Compare `score` against the pattern's element count before reaching for `minScore`.

### C. One weak word vetoes a perfect match

Grammar: `set_heading` = `["orient", "heading", "{heading}"]`, `heading` a 3-word `NumberSequence`.

Utterance: **"orient heading two seven zero"**, with per-word confidences `0.94 / 0.39 / 0.50 / 0.91 / 0.97`.

1. **Score.** Every element matches: `3 / 3` = `1.00`. Nothing skipped.
2. **Confidence.** The minimum over the matched span — `0.39`, from the literal "heading".
3. **Gates.** Score passes. `0.39 < 0.40` fails.

```json
{ "intent": "set_heading", "pattern": "orient heading {heading}",
  "score": 1.0, "minScore": 0.6,
  "aggregateConfidence": 0.39, "minConfidence": 0.4,
  "accepted": false, "rejectReason": "confidence 0.39 < minConfidence 0.40",
  "slots": [ { "name": "heading", "value": "two seven zero",
               "startWord": 2, "endWord": 5, "confidence": 0.5 } ] }
```

Note the slot's own `confidence` (`0.5`, the minimum over tokens 2–4) is *higher* than the attempt's `aggregateConfidence` (`0.39`, the minimum over the whole matched span 0–4). Per-slot confidences are computed over each slot's span alone, so comparing the two localises the weak word immediately: the slots are clean, therefore the culprit is a literal.

**Reading that entry:** a perfect score with a sub-threshold confidence is never a pattern problem. Check the `words` array for the culprit, and do not assume it is the obvious candidate — the digits are fine here, and "two" is sitting at the ≈0.50 ceiling that is a [known limitation](../KNOWN_LIMITATIONS.md) but still clears the gate. It is the ordinary literal "heading" that vetoed the command, because the gate takes the *minimum*.

That ≈0.50 floor on "two" is also why `minConfidence` defaults to `0.4` rather than higher: at `0.5` every `NumberSequence` command containing "two" would be rejected outright. Lowering it below `0.4` trades against noise-triggered false matches.

---

## Reading a session log

Each log entry is one **utterance**. Its `attempts` array holds one entry per *decision the recogniser logged* for that utterance. On the ordinary parse path that is one entry per extraction round — the winner of that round, accepted or rejected. Losing candidates are never logged, so a pattern's absence means it lost selection, not that it was never tried.

Four paths short-circuit before the parse and publish a **single synthetic attempt** instead. All of them leave `pattern` empty, so an empty `pattern` is how you tell them apart:

| `rejectReason` | What happened |
|----------------|---------------|
| `no match` | The parser extracted nothing. `intent` is empty too, and `aggregateConfidence` is `0` — *not* the `-1` sentinel, which only ever comes from a real matched span. |
| `cancelled via vocabulary` | Follow-up speech cancelled a pending command. The confirm case is the same entry with `accepted: true` and an empty `rejectReason`. |
| *(empty, `accepted: true`)* | Follow-up speech filled a pending command's missing slot. |
| `timeout — cancelled` | A pending command timed out and was discarded. `inputText` is the *original* command's transcript, and `words` is empty — this entry is not an utterance at all. Under `FireAsIs` the same entry carries `accepted: true` and an empty `rejectReason`. |

| Field | What it is | Section |
|-------|-----------|---------|
| `inputText` | The buffered transcript that was parsed | — |
| `words[].confidence` | Per-word VOSK confidence — the inputs to the minimum | §5 |
| `attempts[].pattern` | The winning pattern, space-joined | §3 |
| `attempts[].score` | Final score, **after** the skipped-word penalty | §1, §2 |
| `attempts[].minScore` | The gate it was compared against | §5 |
| `attempts[].aggregateConfidence` | Minimum per-word confidence over the matched span; `-1` = no data | §5 |
| `attempts[].rejectReason` | Empty when accepted; otherwise what stopped it — a gate, a post-gate filter, or one of the pipeline events below | §5 |
| `attempts[].slots[].startWord/endWord` | Half-open token range into the whitespace-split `inputText` | — |

Three diagnoses cover most of what sends you to the log — two rejections and one command that fired but did the wrong thing:

| Symptom | Diagnosis |
|---------|-----------|
| `score` ≈ 0.5 on a short pattern, slots extracted | One dropped required literal (§1). Make the function word optional. |
| `score` = 1.0 but `aggregateConfidence` below the gate | One acoustically weak word (§5). Check `words`; consider a slot alias. |
| Accepted with an empty `slots` array where you expected a value | A bare sibling pattern out-scored the slot-filled one (§7 B). |

---

## See Also

- [Command Recognition](command-recognition.md) — the surrounding pipeline: patterns, slots, buffering, eager flush, pending commands
- [VoxrCommandRecogniser](api/command-recogniser.md) — every tuning field with its default
- [Editor Testing](editor-testing.md) — the debug window and the session debug log
- [Batch Test Runner](api/batch-test-runner.md) — regression-test a threshold change
- [Troubleshooting](troubleshooting.md) — symptom-first index of common problems
- [Known Limitations](../KNOWN_LIMITATIONS.md) — the acoustic-model quirks behind low-confidence words
