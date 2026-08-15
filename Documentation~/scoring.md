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

A score answers two questions at once: **how well did this candidate match what it claimed**, and **how much of the utterance did it leave unexplained**. This section is the first half; [§2](#2-coverage) adds the second, and the two share one ratio.

Each element of the pattern contributes to a numerator (**raw score**) and a **denominator**:

```
rawScore / denominator                  (0 when denominator is 0)
```

| Element | Outcome | Raw score | Denominator |
|---------|---------|-----------|-------------|
| Required literal (`"target"`) | matched | +1.0 | +1.0 |
| Required literal | **missed** | **0** | +1.0 |
| Required slot (`"{weapon}"`) | matched | +1.0 | +1.0 |
| Required slot | **missed** | **−1.0** | +1.0 |
| Optional literal (`"?by"`) | matched | +0.5 | +0.5 |
| Optional literal | omitted | 0 | 0 |
| Optional slot (`"{?quantity}"`) | matched | +1.0 | +1.0 |
| Optional slot | omitted | 0 | 0 |

Three properties follow, and each one matters when you read a score:

- **The denominator is dynamic.** Required elements always count toward it; optional elements count only when they were actually spoken. An omitted optional drops out of *both* sides of the ratio rather than diluting it, so taking advantage of optionality is never penalised and a perfect match is always exactly `1.0`.
- **A missed required literal is charged once.** It withholds its credit but keeps its place in the denominator, so one dropped function word costs `1/N` of the ceiling on an `N`-element pattern. It used to be charged *twice* — the withheld credit plus a `−0.5` penalty, i.e. `1.5/N` — which is what made short patterns so fragile. A missed required **slot** is still charged twice, and deliberately: an absent argument is not a dropped function word.
- **A matched optional literal is worth half a required one.** Making a literal optional therefore changes the arithmetic of every *imperfect* match of that pattern, not just the ones that drop the word. See [the cost of `?by`](command-recognition.md#never-leave-a-required-function-word-between-a-bare-pattern-and-its-slot).

A candidate whose score is `0` or negative is discarded and never competes. So is one that **missed more of its required elements than it matched**, whatever it scored — see [admission](#admission-what-counts-as-a-candidate-at-all). Coverage only ever *adds* to the denominator, so it cannot move a candidate across either floor: both behave identically with coverage on or off.

### Admission: what counts as a candidate at all

Before any of the ordering below, a candidate must clear two filters:

1. Its score must be **above `0`**.
2. Its **matched required elements must be at least as many as its missed ones**. Optional elements count toward neither side — omitting one is not evidence against a pattern, and matching one is not evidence that the required elements were spoken.

The second is a count, not a threshold: there is nothing to configure, and it is unrelated to `minScore`. It exists because a pattern that missed most of what it requires is not a weak reading of the utterance, it is a different command — and admitting one has knock-on effects, since a candidate that wins a round consumes tokens and changes what later rounds see.

The visible consequence is that a very sparse partial match now produces *no* result at all rather than a low-scoring one. If you are debugging a command that reports nothing whatsoever, check whether the pattern matched at least half its required elements before assuming a score problem.

### Short patterns are disproportionately fragile

A miss costs a fixed `1.0` of credit but the denominator is not fixed, so the same dropped word still costs a short pattern more than a long one — just no longer enough to sink it:

| Pattern | Utterance | Raw / denominator | Score | At `minScore = 0.6` |
|---------|-----------|-------------------|-------|---------------------|
| `decelerate by {burn_level}` (3 elements) | "decelerate hard burn" | `(1 + 0 + 1) / 3` = `2 / 3` | **0.67** | accepted |
| `launch {weapon} target {target} on my mark` (7 elements) | "launch missiles hotel one on my mark" | `(6 × 1 + 0) / 7` = `6 / 7` | **0.86** | accepted |
| `cease fire` (2 elements) | "fire" | `(0 + 1) / 2` = `1 / 2` | **0.50** | rejected |

In all three the pattern accounts for the whole utterance, so [§2](#2-coverage) adds nothing and the ratio above *is* the score. Both of the first two dropped exactly one required literal, and in both the slots were recognised and extracted. Both now clear the gate — a single dropped function word no longer silences a pattern of three or more elements.

**Two elements is the floor.** `cease fire` heard as "fire" is half the evidence, and half the evidence is genuinely ambiguous — with a `fire` command registered it is a different command entirely — so the cost stays proportional to length rather than being abolished.

The authoring lesson is unchanged and still worth following: do not make a short pattern depend on a short unstressed word — see [the function-word hazard](command-recognition.md#never-leave-a-required-function-word-between-a-bare-pattern-and-its-slot), which the parser warns about at construction. What has changed is that ignoring it now costs accuracy rather than silence.

---

## 2. Coverage

A pattern need not begin where the utterance begins or run to where it ends: the parser slides its start point through the transcript, and a match stops when the pattern's elements run out. **Coverage** charges a candidate for the in-grammar tokens it leaves unexplained, on *both* sides of the match:

```
score = rawScore / (denominator + (skippedBefore + orphanedAfter) × coverageWeight)
```

| Term | What it counts |
|------|----------------|
| `skippedBefore` | Recognised tokens between where this extraction round began and where the candidate starts. |
| `orphanedAfter` | Recognised tokens after the candidate's consumed span, stopping at the first one that could begin another match. |

`coverageWeight` (default `1.0`, named `skippedWordPenalty` before #65) scales both. At `1.0` the score is close to *the fraction of the utterance this candidate accounts for* — close, not equal, because of the orphan rule below.

The effect is proportional to pattern length, so it bites hardest on the patterns short enough to be swallowed whole by a longer utterance. A one-element pattern found past one skipped word scores `1 / (1 + 1)` = `0.50` and fails the default gate; a five-element pattern reached past the same word scores `5 / 6` = `0.83` and still fires.

### Coverage is applied before candidates are compared

This is the property to internalise, and the half that changed in #65. The leading term used to be applied to the winner *alone, after* selection, deliberately so that it could not reorder anything — it filtered through `minScore` and nothing else. Coverage now enters the score selection ranks on, so **it decides which pattern wins**.

That relocation is the entire point. A bare pattern matching its one word perfectly used to score a flat `1.0`, and nothing normalised to `1.0` can be beaten at any weight — so no amount of tuning could stop it out-ranking the slot-filled sibling that explained more of what the speaker said. It now scores `1.0` only when the rest of the utterance is genuinely someone else's business. See [worked example B](#b-coverage-picks-the-pattern-that-explains-more).

### What counts as orphaned

A trailing token is orphaned only if **no active pattern can begin a match at it**, and counting stops at the first token where one can. Without that test coverage would destroy multi-command utterances, charging the first command for the second one's words:

| Utterance | Candidate | Consumed | Trailing | Orphaned | Score |
|-----------|-----------|----------|----------|---------|-------|
| `decelerate hard burn` | `decelerate` | 1 token | `hard burn` — begins no pattern | 2 | `1 / (1 + 2)` = **0.33** |
| `decelerate hard burn` | `decelerate by {burn_level}`, "by" dropped | 3 tokens | — | 0 | `2 / 3` = **0.67** |
| `cease fire launch missiles target hotel one` | `cease fire` | 2 tokens | `launch …` — **begins a pattern** | 0 | `2 / 2` = **1.00** |

**"Can begin a match" means what the matcher does, not what the pattern starts with.** Selection tries every pattern at every token, so a pattern whose leading elements the decoder dropped begins wherever its surviving ones do — and a token some pattern explains that way was never an orphan. The test therefore runs the matcher at each token and asks whether any pattern yields an *admissible* candidate there, admissible in the same sense §3 uses: it matched at least as many of its required elements as it missed.

That admission rule is what keeps the test from degenerating. Without it the run would terminate on any token that appears anywhere in the grammar — including the slot value a bare pattern strands — which would revert the protection in [worked example B](#b-coverage-picks-the-pattern-that-explains-more) for every grammar at once. A pattern that reaches a token only by missing more than it matched is not a candidate there, and does not stop the count.

The test reads the registered patterns alone — never which candidates happened to survive admission in a real round — and it is otherwise deliberately **conservative**: where it is unsure whether a pattern could start at a token, it answers yes and charges nothing. The failure modes are not symmetric. Over-charging destroys sequential extraction; under-charging merely leaves a score where it already was.

Three consequences of that conservatism, all of them grammar-wide — the start test is shared by every candidate, so widening it anywhere weakens trailing coverage everywhere:

- **It reads the decoder's word list, not only the pattern set.** Confirm/cancel follow-up vocabulary is in the grammar, so the decoder returns "yes" as a real token rather than `[unk]`. Since a follow-up can legitimately begin there, "disengage, yes" is not charged for the "yes".
- **A slot-initial pattern over a permissive slot weakens trailing coverage.** If a pattern can begin with an open-ended `NumberSequence`, nearly every token becomes a possible start and almost nothing is charged anywhere in that grammar. See [Known Limitations](../KNOWN_LIMITATIONS.md).
- **A pattern's *leading optional* elements are pattern starts too.** The walk that collects start tokens continues past each optional element and stops at the first required one — because an omitted optional lets the element behind it legitimately begin the match. So `["?please", "fire"]` puts **both** "please" and "fire" into the start set, and a stray "please" then terminates the orphan run for every candidate in the grammar. Worth knowing before you bring filler words in as optionals: put them where they are actually spoken, and prefer the **end** of a pattern to the front.

**One exception, at the run's first position only.** Where the candidate's *own* next required element failed to match at that token, the token is charged rather than tested against the predicate. A candidate that has just mis-predicted a token may not then claim that some *other* pattern could have begun there.

Without the exception a candidate is rewarded for matching **less**. Register `["switch", "to", "weapons"]`, `["switch", "to", "navigation"]` and `["weapons", "mode"]`, and say "switch to weapons target hotel". Had the first orphan been tested rather than charged:

| Candidate | Its final element | Where its consumed span stops | Orphaned | Would score |
|-----------|-------------------|-------------------------------|----------|-------------|
| `switch to navigation` | **missed** | before `weapons` — which begins `weapons mode`, terminating the run at once | 0 | `2 / 3` = `0.67` |
| `switch to weapons` | **matched** | past `weapons`, so it pays for `target hotel` | 2 | `3 / (3 + 2)` = `0.60` |

The wrong command would win by `0.067`. Because the token *is* charged, `switch to navigation` pays for the "weapons" it mis-predicted as well as what follows, scoring `2 / (3 + 3)` = `0.33` — and `mode_weapons`, the command actually spoken, wins at `0.60`. Counting then continues normally from the token after the charged one. Measured threshold for the flip this closes: safe at one leftover token, wrong at two.

### `[unk]` is never charged, and never blocks

Out-of-grammar preamble and hesitation are exactly what the sliding start is for, so filler the decoder could not resolve stays free on both sides. `[unk]` is also **transparent** rather than a run terminator — one noise token cannot shield every real orphan behind it, so "decelerate `[unk]` hard burn" costs exactly what "decelerate hard burn" costs.

Only the literal `[unk]` token is exempt, and that is what a *grammar-constrained* decoder returns for a word outside its vocabulary. `freeSpeechMode`, `InjectText`, and the [Batch Test Runner](api/batch-test-runner.md) deliver real text instead, so a word the decoder would have hidden arrives verbatim and is charged: "cease fire please" scores `2 / (2 + 1)` = `0.67` on those paths against `1.00` through the live grammar-constrained decoder. Treat a batch score as a **lower bound** on the runtime score rather than as equal to it.

### Counting restarts at each extraction round

On the **leading** side, tokens consumed by a previously extracted command are not charged against the next one: the count is taken from where the round began, so chained commands do not penalise each other.

The **trailing** side works differently, and the difference is worth knowing. It has no notion of a round at all — the orphan run is a property of the utterance and the grammar, measured forward from the candidate's consumed span. It lands in the same place because the start test and the matcher ask the same question: a token a later round will explain by missing its way into it terminates the run now, so the earlier command is not charged for it. [Worked example D](#d-two-commands-in-one-breath-and-one-of-them-loses-a-word) is that case.

### Setting the weight

| Value | Effect |
|-------|--------|
| `1.0` (default) | Each unexplained token costs one denominator slot. |
| Above `1.0` | Demands that a command be an even larger share of what was said. |
| `0` | Coverage off — pre-1.4.0 scoring, where nothing outside the match costs anything. **This also switches off the protection above**, so a bare pattern can once more out-rank its slot-filled sibling and discard the argument the speaker did say. |
| Negative, NaN, or infinity | Treated as `0`. |

---

## 3. Selection: which candidate wins

Every candidate that clears [admission](#admission-what-counts-as-a-candidate-at-all) competes. They are ordered by these keys, in this order:

1. **Earliest start token wins.** A candidate that begins earlier beats one that begins later *regardless of score* — a leading match is never displaced by a better-scoring one further along.
2. **Then highest score** — the full score, [coverage](#2-coverage) included.
3. **Then the longer consumed span** — how far the last element that *actually matched something* reached. Trailing `[unk]` the pattern merely skipped does not count, so a candidate cannot win by absorbing noise.
4. **Then the most matched literals.**
5. **Then registration order** — the first-declared command wins, and within a command the first-listed pattern. This is a deterministic fallback, not a design surface; do not build behaviour on it.

Keys 2 and 3 both express "this candidate explains more of the utterance", and since #65 the score carries most of that load. With `intercept track {track}` declared *before* `intercept track {track} {burn_level}`, "intercept track hotel one hard burn" is now settled on **key 2**: the bare pattern leaves `hard burn` unexplained and scores `3 / (3 + 2)` = `0.60`, against the longer form's `4 / 4` = `1.00`. Before coverage entered selection both scored a flat `1.0` with equal literal counts, and **key 3** was the only thing separating them — the longer one consumed 6 tokens against the bare one's 4.

Key 3 therefore matters where coverage cannot see a difference: at `coverageWeight = 0`, or where the trailing tokens *could* begin another match and so are not charged to either candidate. In both, it still prevents the outcome it was added for — the bare pattern winning on declaration order, after which sequential extraction matches the leftover `hard burn` as a *second* command and splits one order in two. Note it sits **above** literal count, so it also settles equal-score candidates whose literal counts differ.

**Key 1 bounds what coverage can do.** Because earliest start outranks score outright, coverage can only reorder candidates that *begin at the same token*; a better-scoring candidate starting later is never promoted over a demoted one starting earlier. Sequential extraction normally recovers it on the next round, and fails only when the winner's consumed span covers the start the better candidate needed. Swept over 699 utterances: 29 candidates were blocked this way and 28 were recovered by a later round — see [Known Limitations](../KNOWN_LIMITATIONS.md) for the one that was not.

**`TryEagerCommit` uses this same ordering**, so an eager verdict always names the command the subsequent flush will actually fire.

---

## 4. Sequential extraction

One utterance can yield several commands. After a winner is chosen, the search restarts from the token where that winner ended and repeats:

```
"cease fire launch missiles target hotel one"
  -> cease_fire                                        score 1.00
  -> launch_weapon(weapon=missiles, target=hotel one)  score 1.00
```

Both score a clean `1.00`, and it is the orphan test that keeps them there: `cease fire` is not charged for the five tokens it leaves behind, because `launch` begins a pattern of its own. Charging every trailing token would score it `2 / 7` = `0.29` and reject it, which is why [coverage](#what-counts-as-orphaned) stops counting at the first token that could start a match.

Extraction stops when no candidate is [admitted](#admission-what-counts-as-a-candidate-at-all) — which means either nothing scored above `0` or nothing matched at least as many required elements as it missed — when a match would consume no tokens, or when the result buffer (one slot per active command) is full.

Two consequences for pattern authoring:

- **A pattern that is a prefix of another can steal its head.** If the shorter one wins a round, the remainder of the utterance is offered to the next round — where it may match a *different* command instead of being read as the tail it was meant to be. Two keys guard against it, over complementary cases. Coverage (key 2) charges the shorter pattern for the tail it abandons — but only while no active pattern could begin a match there. Where one could, coverage charges nothing and the span tie-break (key 3) settles it instead, for candidates that score equally. Neither helps when the longer form is scoring lower for some other reason.
- **Each command is scored and gated independently.** One command in an utterance can fire while another from the same utterance is rejected.

---

## 5. The two gates

The winner of each round faces two independent thresholds. Both live on `VoxrCommandRecogniser`.

### `minScore` (default `0.6`)

Compared against the full score — [fidelity](#1-the-score-formula) and [coverage](#2-coverage) together. Rejects partial and garbled matches, and matches that account for too little of what was said.

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

1. The winner's score ≥ `minScore` — the same number the flush path would compute. The scan mirrors an extraction round starting at token 0, so it charges [coverage](#2-coverage) identically and the two can no longer disagree about a buffer they both see.
2. The match starts at the first **recognised** token. A leading `[unk]` run is skipped for free — nothing arriving later extends an utterance leftward, so out-of-grammar preamble ("Helm, ...") does not block a commit. A leading word VOSK *did* resolve does block it, and what that buys is completeness rather than agreement: a run of recognised words the match did not consume means the buffer holds more than this one command, and committing would fire on part of it.
3. The match reaches the **end** of the buffer. Anything left over — recognised or `[unk]` — is treated as an in-progress tail. Note what this implies for condition 1: whichever candidate ends up being checked here has nothing trailing it, so its *own* trailing charge is always zero. That does **not** make the trailing term irrelevant to the verdict — it still runs in selection, and selection picks the candidate these conditions are then applied to. On the buffer "decelerate hard burn", coverage demotes bare `decelerate` and hands the check to the buffer-spanning `decelerate by {burn_level}`, which commits; at `coverageWeight = 0` the bare form wins selection instead, fails this condition, and the verdict is `None`.
4. Every **required slot** in the winning pattern actually matched. Condition 3 does not imply this: a missed slot consumes no *recognised* token, so it never moves the end of the match past anything the pattern matched — and where it does carry the end over a trailing `[unk]` run, that only makes condition 3 pass more readily. Either way a pattern can appear to span the buffer while still missing an argument. Required *literals* are exempt from **this** condition — a dropped function word still leaves every argument present — but see condition 5.
5. **No required element sits after the last element that actually matched.** Condition 3 cannot express this either, and for the same reason: a miss consumes nothing, so a pattern whose *trailing* elements were never spoken ends up looking exactly like one that genuinely finished at the buffer end. A **medial** miss is fine and still commits — "launch all missiles hotel one" drops the "target" literal but fills every slot and still lands its last element on the buffer's final token, so nothing arriving next was owed to it. A **terminal** one is not: with `["switch", "to", "weapons"]` and `["switch", "to", "navigation"]` registered, the buffer "switch to" matches both at `(1 + 1 + 0) / 3` = `0.67`, and the winner is decided by registration order — so committing there fires the *wrong* command, not merely an early one.
6. Confidence ≥ `minConfidence`, or `-1`.

`Commit` additionally requires the winning pattern to be *terminal*: its last element cannot grow (not a trailing optional, not a variable-width `NumberSequence`, not an enumerated slot with a value that is a word-prefix of another value), and no concrete form of it is a prefix of any concrete form of another pattern.

With `["fire"]` and `["fire", "at", "{target}"]` registered:

| Buffer | Verdict | Why |
|--------|---------|-----|
| `fire` | `HoldExtendable` | complete, but a prefix of `fire at {target}` |
| `fire at` | `None` | bare `fire` still wins selection (`0.50` vs `0.33`) but leaves `at` unconsumed — and at `0.50` it now fails condition 1 as well |
| `fire at hotel one` | `Commit` | complete, terminal, spans the buffer |
| `[unk] fire at hotel one` | `Commit` | leading `[unk]` is skipped for free |
| `fire at hotel one [unk]` | `None` | trailing leftover = possible in-progress tail |

**The `MaxOptionalExpansion` guard.** Deciding terminality means expanding a pattern over its optional elements, which is exponential (2^optionals). A pattern carrying more than **12** optional elements is refused rather than partially analysed — and because a partial analysis could commit the *wrong* command, the refusal covers the whole command set. Nothing then commits early; every complete match degrades to `HoldExtendable`. The parser names the offending pattern, its intent, and its optional count in a construction-time warning.

---

## 7. Worked examples

Each trace ends with the entry it produces in the [session debug log](editor-testing.md#session-debug-log), abridged to the fields under discussion and with scores shown to two decimals. The real `score` field carries the full float — `2 / 3` is written as `0.6666667`, not `0.67` — so match on ranges rather than on an exact literal when you grep or assert against a log.

### A. A clean multi-slot command

Grammar: `launch_weapon` = `["launch", "{weapon}", "target", "{target}"]`, with `weapon = {missiles, …}` and `target = {hotel one, …}`.

Utterance: **"launch missiles target hotel one"** — 5 tokens.

1. **Candidates.** The pattern is tried at every start token, and each is scored with coverage already included. Start 0 matches all four elements and leaves nothing unexplained on either side: `4 / 4` = `1.00`. Start 1 misses the `launch` literal *and* has to skip past it to begin, so that one token costs twice — once in the denominator, once in coverage: `3 / (4 + 1)` = `0.60`. Start 2 misses both `launch` and `{weapon}` — two matched against two missed, so it is still admitted — and skips two tokens to get there: `(0 − 1 + 1 + 1) / (4 + 2)` = `0.17`.
2. **Selection.** Start 0 is earliest, so it wins on key 1 alone; here it is also the highest-scoring.
3. **Confidence.** The minimum per-word confidence over tokens 0–4.
4. **Gates.** `1.00 ≥ 0.6`; confidence compared against `0.4`.

```json
{ "intent": "launch_weapon", "pattern": "launch {weapon} target {target}",
  "score": 1.0, "minScore": 0.6, "accepted": true, "rejectReason": "",
  "slots": [ { "name": "weapon", "value": "missiles",  "startWord": 1, "endWord": 2 },
             { "name": "target", "value": "hotel one", "startWord": 3, "endWord": 5 } ] }
```

### B. Coverage picks the pattern that explains more

Grammar: `decelerate` = `["decelerate"]` **and** `["decelerate", "by", "{burn_level}"]`.

Utterance: **"decelerate hard burn"** — the speaker said the burn level; VOSK dropped "by".

1. **Candidates.** Both start at token 0.
   - The **bare** pattern matches its one element perfectly, but consumes only that token. `hard` and `burn` begin no pattern in this grammar, so both are orphaned: `1 / (1 + 2)` = **0.33**.
   - The **slot-filled** pattern misses `by` (no credit, denominator +1) while `decelerate` and `{burn_level}` match, and it consumes to the end, so coverage adds nothing: `2 / 3` = **0.67**.
2. **Selection.** Key 1 ties, so key 2 decides: `0.67` beats `0.33`. **The slot-filled pattern wins.**
3. **Gates.** `0.67 ≥ 0.6`. The command fires **with** its argument.

```json
{ "intent": "decelerate", "pattern": "decelerate by {burn_level}",
  "score": 0.67, "minScore": 0.6, "accepted": true,
  "slots": [ { "name": "burn_level", "value": "hard burn", "startWord": 1, "endWord": 3 } ] }
```

**This is the ordering that #65 inverted.** Until coverage entered selection, the bare pattern scored a flat `1.00` — it did match everything it claimed — and won, firing `decelerate` with an empty `slots` array while the "hard burn" the speaker actually said was silently dropped. No threshold reached it, because nothing normalised to `1.00` can be out-scored. Charging a candidate for what it leaves unexplained is what reverses the order, and it is the reason coverage had to move *above* the selection barrier rather than stay a filter on the winner.

**`"?by"` is still the better grammar, and the parser still warns about this shape at construction.** Coverage closes the *common* case, not the whole hazard — see below. Make the literal optional and the omitted optional drops out of both sides of the ratio, so the slot-filled form scores `2 / 2` = `1.00` whether or not the word was spoken, a comfortable margin instead of `0.07` above the gate:

```json
{ "intent": "decelerate", "pattern": "decelerate ?by {burn_level}",
  "score": 1.0, "accepted": true,
  "slots": [ { "name": "burn_level", "value": "hard burn", "startWord": 1, "endWord": 3 } ] }
```

Note the `?` survives into the logged `pattern` — it is the pattern as you declared it, not as it matched — so grep a session log for `decelerate ?by {burn_level}`, not for `decelerate by {burn_level}`. Read [the cost of the swap](command-recognition.md#never-leave-a-required-function-word-between-a-bare-pattern-and-its-slot) before applying it wholesale.

**The case coverage does not close.** The orphan run stops at the first token another match can begin at — so if the stranded value's *own first word* begins some pattern, the bare candidate is charged nothing and strands the value exactly as it did before #65. Register `["hard", "stop"]` alongside the pair above and "decelerate hard burn" goes back to firing bare `decelerate` at a full `1.00`, argument discarded, at the **default** `coverageWeight`:

```json
{ "intent": "decelerate", "pattern": "decelerate",
  "score": 1.0, "accepted": true, "slots": [] }
```

This is why the construction-time warning was not narrowed when coverage shipped: `?by` fixes both the common case and this residue, and coverage alone fixes only the first. The same applies wherever [the orphan test](#what-counts-as-orphaned) charges nothing — including a grammar with a slot-initial pattern over a permissive slot.

**Reading that entry:** a `0.67` on a three-element pattern whose slots *did* extract is the signature of exactly one missing required literal — `(N−1)/N` — with nothing left unexplained. If the number is lower than that arithmetic predicts, the difference is coverage: count the tokens outside the match.

### B2. The same demotion with nowhere to land

Same utterance, but the intent registers **only** `["decelerate"]` — no slot-filled sibling.

Nothing changes about the bare pattern's score: it still explains one token of three and still scores `1 / (1 + 2)` = **0.33**. What changes is that no better candidate exists to win instead, so the demoted one is the winner of the round and simply fails the gate:

```json
{ "intent": "decelerate", "pattern": "decelerate",
  "score": 0.33, "minScore": 0.6,
  "accepted": false, "rejectReason": "score 0.33 < minScore 0.60", "slots": [] }
```

Before #65 this fired. It is the main behaviour change coverage brings to grammars that were working — a command is now judged on how much of the utterance it accounts for, and that judgement applies whether or not a fuller phrasing exists to take its place. Measured over 699 utterances, 17 stopped clearing `minScore` this way and none started firing wrongly. The intended authoring response is to register the fuller phrasing, or bring the natural trailing words into the grammar as optional literals (`?please`); the blunt escapes are a lower `minScore` or a `coverageWeight` below `1.0`. See [Known Limitations](../KNOWN_LIMITATIONS.md).

### C. One weak word vetoes a perfect match

Grammar: `set_heading` = `["orient", "heading", "{heading}"]`, `heading` a 3-word `NumberSequence`.

Utterance: **"orient heading two seven zero"**, with per-word confidences `0.94 / 0.39 / 0.50 / 0.91 / 0.97`.

1. **Score.** Every element matches: `3 / 3` = `1.00`. Nothing skipped before it, nothing left unexplained after it.
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

### D. Two commands in one breath, and one of them loses a word

Grammar: `cease_fire` = `["cease", "fire"]`, `approach_target` = `["approach", "target", "{target}"]`.

Utterance: **"cease fire target hotel one"** — the speaker said both commands; VOSK dropped the second one's "approach".

1. **Round 1.** `cease fire` matches at token 0 and consumes two tokens. What follows is `target hotel one`. No pattern *starts with* "target" — `approach_target` starts with "approach" — but `approach target {target}` does match there, missing only that literal, and matching two of its three required elements against one miss makes it admissible. So the orphan run terminates at "target", nothing is charged, and `cease fire` scores `2 / 2` = **1.00**.
2. **Round 2.** The search restarts at token 2, so the leading term re-bases and nothing before it is charged again. `approach target {target}` misses its `approach` literal but matches the rest and consumes to the end: `2 / 3` = **0.67**. It fires too.

```json
{ "intent": "cease_fire", "pattern": "cease fire",
  "score": 1.0, "accepted": true }
{ "intent": "approach_target", "pattern": "approach target {target}",
  "score": 0.67, "accepted": true,
  "slots": [ { "name": "target", "value": "hotel one", "startWord": 3, "endWord": 5 } ] }
```

**Reading those entries:** both commands fire, and the damaged one carries the lower score — which is the shape to expect. The command spoken cleanly is not charged for the other's words, because the token the second command *would* be matched from is the token the first one's orphan run stops at. Say the second command in full and both fire at `1.00`.

This is the case the start test has to ask the matcher to get right. Testing only what patterns *start with* charged `cease_fire` for all three trailing tokens — `2 / (2 + 3)` = `0.40`, below the gate — so the command that lost a word fired and the one spoken perfectly did not. Measured over 699 utterances, that shape accounted for 11 intent changes and 1 count change before it was closed.

---

## Reading a session log

Each log entry is one **utterance**. Its `attempts` array holds one entry per *decision the recogniser logged* for that utterance. On the ordinary parse path that is one entry per extraction round — the winner of that round, accepted or rejected. Losing candidates are never logged, so a pattern's absence means it lost selection, not that it was never tried.

Four paths short-circuit before the parse and publish a **single synthetic attempt** instead. All of them leave `pattern` empty, so an empty `pattern` is how you tell them apart:

| `rejectReason` | What happened |
|----------------|---------------|
| `no match` | The parser extracted nothing. `intent` is empty too, and `aggregateConfidence` is `0` — *not* the `-1` sentinel, which only ever comes from a real matched span. |
| `cancelled via vocabulary` | Follow-up speech cancelled a pending command. The confirm case is the same entry with `accepted: true` and an empty `rejectReason`. |
| *(empty, `accepted: true`)* — or `still pending (partial: unfilled [...])` | Follow-up speech filled a pending command's missing slot. Empty reason with `accepted: true` means no required slot is left and the command fired. When the utterance filled some but not all of what was still missing, the same entry carries `accepted: false` and `still pending (partial: unfilled [...])` instead: the fill was kept, the command did not fire, and the pending is still live. |
| `timeout — cancelled` | A pending command timed out and was discarded. `inputText` is the *original* command's transcript, and `words` is empty — this entry is not an utterance at all. Under `FireAsIs` the same entry carries `accepted: true` and an empty `rejectReason`. |

| Field | What it is | Section |
|-------|-----------|---------|
| `inputText` | The buffered transcript that was parsed | — |
| `words[].confidence` | Per-word VOSK confidence — the inputs to the minimum | §5 |
| `attempts[].pattern` | The winning pattern, space-joined | §3 |
| `attempts[].score` | The full score — fidelity **and** coverage, the same number selection ranked on | §1, §2 |
| `attempts[].minScore` | The gate it was compared against | §5 |
| `attempts[].aggregateConfidence` | Minimum per-word confidence over the matched span; `-1` = no data | §5 |
| `attempts[].rejectReason` | Empty when accepted; otherwise what stopped it — a gate, a post-gate filter, or one of the pipeline events below | §5 |
| `attempts[].slots[].startWord/endWord` | Half-open token range into the whitespace-split `inputText` | — |

The diagnoses below cover most of what sends you to the log. The first question to ask of any surprising `score` is whether the pattern's own elements account for it: work out `matched / elements` for the pattern that won, and if the reported number is lower, the difference is coverage — count the recognised tokens lying outside the match.

| Symptom | Diagnosis |
|---------|-----------|
| `score` ≈ 0.67 on a three-element pattern, slots extracted | One dropped required literal, nothing left unexplained (§1) — above the gate, so it fires. |
| `score` ≈ 0.5 on a **two**-element pattern | One dropped required literal, and two elements is the floor: half the evidence stays rejected (§1). |
| `score` well below `matched / elements`, on a short pattern | Coverage (§2). The match left recognised tokens unexplained before or after it — most often natural trailing words the grammar does not contain. |
| A command that fired before the upgrade and no longer does | Same cause, and the expected shape of the #65 change (§7 B2). Register the fuller phrasing, mark the trailing words optional, or lower `coverageWeight`. |
| The command that *lost* a word fired; the one spoken cleanly was rejected at ≈0.40 | Two commands in one utterance, the second missing its leading word (§7 D). |
| no result at all for a pattern that clearly part-matched | The candidate missed more required elements than it matched and was refused [admission](#admission-what-counts-as-a-candidate-at-all) (§3). |
| `score` = 1.0 but `aggregateConfidence` below the gate | One acoustically weak word (§5). Check `words`; consider a slot alias. |
| Accepted with an empty `slots` array where you expected a value | A bare sibling pattern out-ranked the slot-filled one. Coverage closes the common case (§7 B). If you still see it, the stranded value's first word probably begins another pattern, so coverage charged the bare form nothing — or `coverageWeight` is `0`. |

---

## See Also

- [Command Recognition](command-recognition.md) — the surrounding pipeline: patterns, slots, buffering, eager flush, pending commands
- [VoxrCommandRecogniser](api/command-recogniser.md) — every tuning field with its default
- [Editor Testing](editor-testing.md) — the debug window and the session debug log
- [Batch Test Runner](api/batch-test-runner.md) — regression-test a threshold change
- [Troubleshooting](troubleshooting.md) — symptom-first index of common problems
- [Known Limitations](../KNOWN_LIMITATIONS.md) — the acoustic-model quirks behind low-confidence words
