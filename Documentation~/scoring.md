# Matching and Scoring

The reference for the model that decides whether a spoken command fires: how a candidate match is scored, which candidate wins when several match, and what the bar and the two acceptance gates do with the winner.

Read this when you are tuning `minScore` / `minConfidence`, diagnosing why a command was rejected, interpreting a [session debug log](editor-testing.md#session-debug-log), or authoring patterns that must not shadow each other. For the surrounding pipeline — buffering, pending commands, grammar mode — see [Command Recognition](command-recognition.md).

Everything here describes `VoxrCommandParser`, which is deterministic: the same transcript against the same command set always produces the same result.

---

## Vocabulary

| Term | Meaning |
|------|---------|
| **Token** | One whitespace-separated word of the transcript. `[unk]` is VOSK's token for audio it could not resolve to a grammar word. |
| **Element** | One entry of a pattern array: a required literal (`"target"`), an optional literal (`"?by"`), a required slot (`"{weapon}"`), or an optional slot (`"{?quantity}"`). |
| **Candidate** | One (command, pattern, start token) triple that the parser scored. Every pattern of every active command is tried at every non-`[unk]` start position. |
| **Winner** | The single candidate selection picks per extraction round. Only winners reach the gates, and only winners are logged as a scored attempt — losing candidates are not logged. A winner that is [barred](#the-leading-required-miss-bar) does neither, and is the one exception to both halves. One exception: a rival that *tied* the winner exactly is named on the attempt — as `Tied with:` in the Editor's last-match panel and in batch-runner diagnostics, and as [`tiedRival` / `tiedRivalIsSibling`](#reading-a-session-log) in the exported session log. Even then only winners are logged: the rival is named *on* the winner's attempt, never as an attempt of its own. |
| **Barred winner** | A winner whose **first required element** matched nothing. It competes, wins and consumes its span like any other, but is refused before the gates and is logged nowhere — see [the leading-required-miss bar](#the-leading-required-miss-bar). |

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

The visible consequence is that a very sparse partial match now produces *no* result at all rather than a low-scoring one. If you are debugging a command that reports nothing whatsoever, check whether the pattern matched at least half its required elements before assuming a score problem. If the count is healthy, check *which* element missed — a pattern that wins its round having matched nothing of its **first** required element is [barred](#the-leading-required-miss-bar) and also reports nothing, at any score.

### Short patterns are disproportionately fragile

A miss costs a fixed `1.0` of credit but the denominator is not fixed, so the same dropped word still costs a short pattern more than a long one — just no longer enough to sink it:

| Pattern | Utterance | Raw / denominator | Score | At `minScore = 0.6` |
|---------|-----------|-------------------|-------|---------------------|
| `decelerate by {burn_level}` (3 elements) | "decelerate hard burn" | `(1 + 0 + 1) / 3` = `2 / 3` | **0.67** | accepted |
| `launch {weapon} target {target} on my mark` (7 elements) | "launch missiles hotel one on my mark" | `(6 × 1 + 0) / 7` = `6 / 7` | **0.86** | accepted |
| `cease fire` (2 elements) | "fire" | `(0 + 1) / 2` = `1 / 2` | **0.50** | rejected — and [barred](#the-leading-required-miss-bar) besides |

In all three the pattern accounts for the whole utterance, so [§2](#2-coverage) adds nothing and the ratio above *is* the score. Both of the first two dropped exactly one required literal, and in both the slots were recognised and extracted. Both now clear the gate — a single dropped function word no longer silences a pattern of three or more elements, **provided the word that went missing was not the pattern's first required element** ([the bar](#the-leading-required-miss-bar)).

**Two elements is the floor.** `cease fire` heard as "fire" is half the evidence, and half the evidence is genuinely ambiguous — with a `fire` command registered it is a different command entirely — so the cost stays proportional to length rather than being abolished.

**And in this particular row the score is no longer what refuses it.** The element that went missing is `cease`, the pattern's *first required* one, so the candidate is [barred from firing](#the-leading-required-miss-bar) whatever it scores. Lowering `minScore` will not recover it, and neither will lengthening the pattern: `cease fire now` heard as "fire now" scores `0.67`, clears the default gate, and is still refused.

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

**"Can begin a match" means what the matcher does, not what the pattern starts with.** Selection tries every pattern at every token, so a pattern whose leading elements the decoder dropped begins wherever its surviving ones do — and a token some pattern explains that way was never an orphan. The test therefore runs the matcher at each token and asks whether any pattern, matched from there, ends up having matched **strictly more** of its required elements than it missed.

That threshold is what keeps the test from degenerating, and it is deliberately one notch stronger than [admission](#admission-what-counts-as-a-candidate-at-all). Admission asks whether something is a candidate at all — a question answered for a candidate that may still lose its round and never fire. Terminating another command's orphan run is a larger claim, because it moves score off a command that *is* firing, so it takes strictly more evidence for than against.

Without that margin the run would terminate on the slot value a bare pattern strands — `decelerate ?by {burn_level}` reaches `{burn_level}` from "hard" having missed one required element and matched one — and the protection in [worked example B](#b-coverage-picks-the-pattern-that-explains-more) would be gone for every grammar at once. Like admission itself this is a **count**, not a threshold: nothing to configure, and independent of `minScore` and of `coverageWeight`.

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

The **trailing** side works differently, and the difference is worth knowing. It has no notion of a round at all — the orphan run is a property of the utterance and the grammar, measured forward from the candidate's consumed span. It usually lands in the same place, because a token a later round will explain by missing its way into it terminates the run now. The two are not the same question, though: the start test asks only whether a pattern *could* be matched from a token with more evidence for than against, while "a later round explains it" also requires that candidate to win its round, clear [the bar](#the-leading-required-miss-bar) and clear the gate. Where it would not, the token is left uncharged and still unexplained — the erring direction is a score left higher than ideal, never a command charged for words that were someone else's. [Worked example D](#d-two-commands-in-one-breath-and-one-of-them-loses-its-first-word) is exactly that divergence: the orphan run terminates because a pattern *could* start there, and the round that starts there then yields nothing.

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
5. **Then registration order** — the first-declared command wins, and within a command the first-listed pattern. This is a deterministic fallback, not a design surface; do not build behaviour on it. The one sanctioned use is defensive: where two commands are genuinely indistinguishable and you cannot prompt, declaring the *safer* one first decides which way the coin lands. That is choosing your loss, not designing on the key — and `disambiguateSiblingTies` removes the need for it.

Keys 2 and 3 both express "this candidate explains more of the utterance", and since #65 the score carries most of that load. With `intercept track {track}` declared *before* `intercept track {track} {burn_level}`, "intercept track hotel one hard burn" is now settled on **key 2**: the bare pattern leaves `hard burn` unexplained and scores `3 / (3 + 2)` = `0.60`, against the longer form's `4 / 4` = `1.00`. Before coverage entered selection both scored a flat `1.0` with equal literal counts, and **key 3** was the only thing separating them — the longer one consumed 6 tokens against the bare one's 4.

Key 3 therefore matters where coverage cannot see a difference: at `coverageWeight = 0`, or where the trailing tokens *could* begin another match and so are not charged to either candidate. In both, it still prevents the outcome it was added for — the bare pattern winning on declaration order, after which sequential extraction matches the leftover `hard burn` as a *second* command and splits one order in two. Note it sits **above** literal count, so it also settles equal-score candidates whose literal counts differ.

**Key 1 bounds what coverage can do.** Because earliest start outranks score outright, coverage can only reorder candidates that *begin at the same token*; a better-scoring candidate starting later is never promoted over a demoted one starting earlier. Sequential extraction normally recovers it on the next round, and fails only when the winner's consumed span covers the start the better candidate needed. Swept over 699 utterances: 28 candidates were blocked this way and 27 were recovered by a later round — see [Known Limitations](../KNOWN_LIMITATIONS.md) for the one that was not.

**`TryEagerCommit` uses this same ordering**, so an eager verdict never names a command the subsequent flush would not fire. It may name nothing at all — the gate refuses on several conditions (§6), and a refusal simply defers to the flush.

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

**A round can also end without stopping extraction and without producing a command.** Where the round's winner missed its first required element it is [barred](#the-leading-required-miss-bar): it still consumes the span it matched, the search still restarts after it, and no result is written for that round. So the number of commands an utterance yields is not the number of rounds it took.

Two consequences for pattern authoring:

- **A pattern that is a prefix of another can steal its head.** If the shorter one wins a round, the remainder of the utterance is offered to the next round — where it may match a *different* command instead of being read as the tail it was meant to be. Two keys guard against it, over complementary cases. Coverage (key 2) charges the shorter pattern for the tail it abandons — but only while no active pattern could begin a match there. Where one could, coverage charges nothing and the span tie-break (key 3) settles it instead, for candidates that score equally. Neither helps when the longer form is scoring lower for some other reason.
- **Each command is scored and gated independently.** One command in an utterance can fire while another from the same utterance is rejected.

---

## 5. The bar and the two gates

The winner of each round faces one positional bar, then two independent thresholds, and then a completeness check that no score can override. The thresholds live on `VoxrCommandRecogniser`; the bar has nothing to configure.

### The leading-required-miss bar

**A winner whose *first required element* matched nothing does not fire, whatever it scored.** It still competed, still won its round, and still consumes the tokens it matched — but it produces no result, and the round yields nothing.

The rule is **positional, not arithmetic**, which is why no threshold reaches it. In a command grammar the first required element is the **verb**: what to *do*. The elements after it are arguments: what to do it *to*. Losing an argument still leaves the action identified, and the score reports the damage while the gate decides. Losing the verb leaves no evidence that any action was requested at all — only that some words happened to match a pattern's tail. `minScore` sees `2/3` and cannot ask *which* third went missing.

```
Grammar: query_time_to_target : ["time", "to", "target"]
         intercept_target     : ["intercept", "track", "{track}"]

"time to target track one two four four"
  before : query_time_to_target 1.00   +  intercept_target 0.67  <-- "intercept" was never spoken
  now    : query_time_to_target 1.00
```

Optional elements are skipped when locating the first required one, so an unspoken `?please` or `{?quantity}` never triggers the bar. The rule is the same whether that first required element is a literal or a slot.

**What the bar does not change.** No score moves, and no scoring constant moves: every command that fires carries exactly the score it carried before. What changes is whether an already-computed score is allowed to produce a result.

**That the barred round still consumes is the point, not an oversight.** The leading coverage term charges unexplained tokens to whichever candidate wins the round, so a barred candidate winning and consuming its span is what keeps that debris off the *next* command:

```
"target hotel one cease fire"
  -> approach_target wins round 1, is barred, and consumes "target hotel one"
  -> cease_fire      then scores 2 / 2 = 1.00, exactly as if the debris were not there
```

Had the barred candidate been excluded from selection instead, `cease_fire` would have been charged for those three tokens — `2 / (2 + 3)` = `0.40`, below the gate — and a cleanly spoken command would have been lost. Measured over 699 utterances, refusing to *compete* destroys 11 commands scoring a clean `1.00`; refusing to *fire* destroys none.

**Everything downstream follows from the round yielding nothing.** A barred winner opens no pending state of any kind — `allowPartialMatch` does not route it to slot-fill, `requiresConfirmation` does not ask you to confirm it, and `disambiguateSiblingTies` does not raise a "which did you mean?" about it. It records no session-log attempt (see [Reading a session log](#reading-a-session-log)), and where nothing else in the utterance fired *and* no other candidate was diverted to a pending, filtered by `minConfidence`, or debounced, the utterance reports through `OnUnrecognisedSpeech` — those suppressions are utterance-wide, so a second round that opens a pending silences the report for the barred one too (see [What `OnUnrecognisedSpeech` actually means](#what-onunrecognisedspeech-actually-means)). It even reaches back to construction: where two intents differ at exactly one required word and that word is **every** member pattern's first required element, the [sibling warning](command-recognition.md#do-not-separate-two-commands-by-a-single-word) is withheld too, because the claim it makes would no longer be true of that set. It takes **every** member — where only some of the patterns are anchored on the discriminator they still tie exactly, the round can be handed to a candidate the bar does not touch, and the warning fires as before. At runtime the reach is narrower still, and it is not confined to that set: wherever the discriminating word is not **every** member's first required element, the member the bar would refuse need not be the one that wins the round. It is recorded as a tied rival *before* the bar runs, and — with `disambiguateSiblingTies` on — it is offered as a choice and fires if the speaker picks it. The bar governs which candidate may **win a round**, not which may be offered as an alternative to one that did; see [Known Limitations](../KNOWN_LIMITATIONS.md).

**What it costs.** Where the leading word genuinely *was* spoken and the decoder dropped it, the command used to be recovered at a reduced score and is now silent. Nothing in the transcript distinguishes "never spoken" from "spoken and lost", so the only remedy is to say the command again — see [Known Limitations](../KNOWN_LIMITATIONS.md). Measured on the same 699 utterances: 9 rows lose a genuine command this way, against 39 invented ones suppressed.

**Grammar-side mitigations** are in [Command Recognition](command-recognition.md#do-not-leave-a-bare-patterns-tail-readable-as-another-command) for a tail readable as some other intent's command, and in [the one-word hazard](command-recognition.md#do-not-separate-two-commands-by-a-single-word) for two intents a single required word apart — which is where to go if the bar is why your sibling warning vanished.

### `minScore` (default `0.6`)

Compared against the full score — [fidelity](#1-the-score-formula) and [coverage](#2-coverage) together. Rejects partial and garbled matches, and matches that account for too little of what was said.

If the command definition sets `allowPartialMatch`, a match with unfilled required slots enters the [pending state](command-recognition.md#pending-commands) instead of being rejected outright — at *any* score, not only below this gate; see [Completeness](#completeness-independent-of-score) below.

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

### Completeness (independent of score)

A winner missing a **required slot** does not fire, whatever it scored. The check is deliberately not arithmetic: the `−1.0` slot-miss penalty makes a missing argument score *lower*, but the score is not what stops the command firing — a five-element pattern with one missed required slot scores exactly `3/5` = `0.60`, clears the default `minScore`, and is still refused. Session-log `rejectReason`: `required slot unfilled`.

With `allowPartialMatch` on the command, an incomplete winner — above the gate or below it — enters the [pending state](command-recognition.md#pending-commands) for slot-fill instead (`entered pending (partial: unfilled [...])`). **[The bar](#the-leading-required-miss-bar) is consulted first**, so a winner that missed its first required element opens no pending at any score, and this flag does not divert it. Without it, the winner is rejected, and the utterance is reported through `OnUnrecognisedSpeech` unless some other candidate in the same utterance was diverted to a pending, filtered, or debounced. Missed required *literals* are not part of this check: a command with every argument present still fires over a dropped function word, which is what §1's reduced miss cost exists to allow.

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
| Sibling tie, with `disambiguateSiblingTies` on | Enters pending, fires on the speaker's answer | `entered pending (awaiting disambiguation, N choices)` |
| `requiresConfirmation` | Enters pending, fires on confirmation | `entered pending (awaiting confirmation)` |

That order is deliberate: a command already on cooldown should not raise a question the speaker then answers into a cooldown, and a disambiguation has to precede a confirmation — you cannot confirm an intent you have not identified. The two-question exchange that produces is worked through in [Ambiguous commands](command-recognition.md#ambiguous-commands-ask-instead-of-guessing).

And below `minScore`, `allowPartialMatch` diverts to pending rather than rejecting: `entered pending (partial: unfilled [...])` — unless the winner was [barred](#the-leading-required-miss-bar), which precedes every filter in this table.

> The numbers inside a `rejectReason` are formatted with the **Editor's current culture**, so on a comma-decimal locale the field reads `score 0,50 < minScore 0,60`. If you grep a session log, match on the surrounding words rather than the whole literal. The numeric `score` / `aggregateConfidence` *fields* are unaffected — JSON numbers are written invariantly.

### What `OnUnrecognisedSpeech` actually means

It does **not** mean "nothing matched". It fires whenever an utterance produced no accepted command, *except* when some candidate was dropped by `minConfidence`, suppressed by debounce, or diverted to a pending — those three are the only filters that suppress it:

| Outcome | `OnUnrecognisedSpeech` |
|---------|------------------------|
| No pattern matched at all | fires |
| Every candidate fell under `minScore` | **fires** |
| The winning candidate's first required element was never heard ([the bar](#the-leading-required-miss-bar)) | **fires** |
| The winner was missing a required slot (command without `allowPartialMatch`) | **fires** |
| A follow-up fill completed a pending command but re-scored at or below zero | **fires** — the fill is refused and the pending is left standing |
| A candidate was diverted to a pending — partial match, `requiresConfirmation`, or **disambiguation** | silent; `OnCommandPending` fires instead |
| A candidate was rejected by `minConfidence` | silent |
| A candidate was suppressed by debounce | silent |

Every diversion is silent, and for one reason: being told the speech was not understood, in the same frame you were asked to prompt the speaker about it, is a contradiction — the prompt is the recogniser saying it understood enough to ask ([#133](https://github.com/jinwoo1601/VoXR-Speech-Recognition/issues/133)).

So the event is not a reliable "I heard nothing" signal: the score-rejection rows of §7 raise it too. If you show the player feedback on it, expect it after a half-heard command as well as after noise.

---

## 6. Eager-flush verdicts

When `eagerFlushOnCompleteMatch` is enabled, each VOSK result triggers one speculative parse of the buffer that returns one of three verdicts. (It is skipped while a command is pending, so confirm and follow-up speech stay on the timer path.) The scan reuses the selection order from §3, so its verdict never names a command a flush would not fire.

| Verdict | Meaning | Buffer behaviour |
|---------|---------|------------------|
| `Commit` | Complete, confident, and **unextendable** | Flush and fire now |
| `HoldExtendable` | Complete and confident, but more speech could still extend it | Wait `prefixHoldSeconds` (if set and shorter than `bufferWindow`), else the full window |
| `None` | Not a complete confident match of the whole buffer | Wait the full `bufferWindow` |

A verdict above `None` requires **all** of:

1. **Score ≥ `minScore`** — the same number the flush path would compute, coverage included.
2. **The match starts at the first recognised token** — a leading `[unk]` run is skipped for free.
3. **The match reaches the end of the buffer** — nothing left over, recognised or `[unk]`.
4. **Every required slot in the winning pattern matched.** Required *literals* are exempt here, but see 5 and 6.
5. **No required element sits after the last element that actually matched.**
6. **The winner's first required element matched something** — the same [bar](#the-leading-required-miss-bar) the flush path applies.
7. **Confidence ≥ `minConfidence`, or `-1`.**

None of the seven is implied by its neighbours, which is why each exists:

- **(1)** The scan mirrors an extraction round starting at token 0, so it charges [coverage](#2-coverage) identically to the flush and the two can never disagree about a buffer they both see.
- **(2)** Nothing arriving later extends an utterance leftward, so out-of-grammar preamble ("Helm, ...") does not block a commit. A leading word VOSK *did* resolve does block it, and what that buys is completeness rather than agreement: a run of recognised words the match did not consume means the buffer holds more than this one command, and committing would fire on part of it.
- **(3)** Whichever candidate is checked here has nothing trailing it, so its *own* trailing charge is always zero. That does **not** make the trailing term irrelevant — it still runs in selection, and selection picks the candidate these conditions are then applied to. On the buffer "decelerate hard burn", coverage demotes bare `decelerate` and hands the check to the buffer-spanning `decelerate by {burn_level}`, which commits; at `coverageWeight = 0` the bare form wins selection instead, fails this condition, and the verdict is `None`.
- **(4)** Condition 3 does not imply it: a missed slot consumes no *recognised* token, so it never moves the end of the match past anything the pattern matched — and where it does carry the end over a trailing `[unk]` run, that only makes condition 3 pass more readily. Either way a pattern can appear to span the buffer while still missing an argument.
- **(5)** Condition 3 cannot express this either, for the same reason: a miss consumes nothing, so a pattern whose *trailing* elements were never spoken looks exactly like one that genuinely finished at the buffer end. A **medial** miss satisfies this condition and can still commit — "launch all missiles hotel one" drops the "target" literal but fills every slot and lands its last element on the buffer's final token, so nothing arriving next was owed to it. (Completeness is all this condition asks; a medial miss that leaves two *intents* tied is caught by the ambiguity rule below.) A **terminal** miss is refused: with `["switch", "to", "weapons"]` and `["switch", "to", "navigation"]` registered, the buffer "switch to" matches both at `(1 + 1 + 0) / 3` = `0.67`, and the winner is decided by registration order — committing there fires the *wrong* command, not merely an early one.
- **(6)** Nothing above it asks *which* element went missing. Condition 5 catches a **terminal** miss and condition 4 a missing argument, but a pattern that lost only its **leading** required element satisfies both — `["cease", "fire", "now"]` heard as "fire now" matches its last two elements, fills every slot, spans the buffer, and scores `0.67`, so condition 1 does not refuse it either. This condition is **not** inherited from the shared selection order: it is an explicit refusal in `TryEagerCommit`, and it exists to protect the **buffer** rather than to prevent a fire. Committing consumes and clears the accumulated transcript, so without it a half-spoken command would be flushed early and discarded and its continuation parsed as a separate utterance. The flush path bars such a winner either way, so nothing this condition refuses could have fired.
- **(7)** Acoustic confidence is orthogonal to everything above it: a pattern can match perfectly, span the buffer and still have been heard badly. `-1` means no per-word data was available and the check is bypassed rather than failed.

`Commit` additionally requires **both** of:

- The winning pattern is *terminal*: its last element cannot grow (not a trailing optional, not a variable-width `NumberSequence`, not an enumerated slot with a value that is a word-prefix of another value), and no concrete form of it is a prefix of any concrete form of another pattern.
- **No sibling tie.** No equally-ranked rival of a *different* intent differs from the winner at exactly one required word. Such a pair is indistinguishable on this buffer — same score, same span, same literal count — so the winner would be settled by registration order, and committing would fire a coin flip before the utterance is over. The verdict drops to `None` and the buffer waits out its full window. What happens then depends on one flag: by default the flush fires the same command it always would have, and with `disambiguateSiblingTies` on it **asks the speaker which they meant** instead — which is the whole point of deferring, since the decision then happens once, on a final transcript. See [Ask instead of guessing](command-recognition.md#ambiguous-commands-ask-instead-of-guessing). This is the only one of these rules that gates `Commit` alone: a match that was already going to be *held* is left held, since nothing is being refused when nothing was being offered. See [the one-word hazard](command-recognition.md#do-not-separate-two-commands-by-a-single-word).

With `["fire"]` and `["fire", "at", "{target}"]` registered:

| Buffer | Verdict | Why |
|--------|---------|-----|
| `fire` | `HoldExtendable` | complete, but a prefix of `fire at {target}` |
| `fire at` | `None` | bare `fire` still wins selection (`0.50` vs `0.33`) but leaves `at` unconsumed — and at `0.50` it now fails condition 1 as well |
| `fire at hotel one` | `Commit` | complete, terminal, spans the buffer |
| `[unk] fire at hotel one` | `Commit` | leading `[unk]` is skipped for free |
| `fire at hotel one [unk]` | `None` | trailing leftover = possible in-progress tail |

**The `MaxOptionalExpansion` guard.** Deciding terminality means expanding a pattern over its optional elements, which is exponential (2^optionals). A pattern carrying more than **12** optional elements is refused rather than partially analysed — and because a partial analysis could commit the *wrong* command, the refusal covers the whole command set. Nothing then commits early; every complete match degrades to `HoldExtendable`. The parser names the offending pattern, its intent, and its optional count in a construction-time warning.

A second, lower cap governs the **sibling analysis** the no-tie rule leans on: past **6** optional elements, a pattern's sibling relations are checked only in its required-elements reading. That is enough to *refuse* a `Commit` — the check over-approximates, so nothing wrong commits early — but not enough to *name* the rival, so with `disambiguateSiblingTies` on, a tie visible only through such a pattern fires the winner without asking. Where a question is raised anyway, the unnameable rival is reported through [`PendingAmbiguity.IsTruncated`](command-recognition.md#three-or-more-and-what-does-not-fit). See `KNOWN_LIMITATIONS.md`.

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

This is why the construction-time warning was not narrowed when coverage shipped: `?by` fixes both the common case and this residue, and coverage alone fixes only the first. The same applies wherever [the orphan test](#what-counts-as-orphaned) charges nothing — including a grammar with a slot-initial pattern over a permissive slot. It is logged in the **Editor only**: keeping the trigger wide is what preserves the residue, and keeping it out of player builds is what stops that breadth earning it a blanket suppression.

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

### D. Two commands in one breath, and one of them loses its first word

Grammar: `cease_fire` = `["cease", "fire"]`, `approach_target` = `["approach", "target", "{target}"]`.

Utterance: **"cease fire target hotel one"** — the speaker said both commands; VOSK dropped the second one's "approach".

1. **Round 1.** `cease fire` matches at token 0 and consumes two tokens. What follows is `target hotel one`. No pattern *starts with* "target" — `approach_target` starts with "approach" — but `approach target {target}` does match there, missing only that literal, and matching two of its three required elements against one miss clears the **start test**: strictly more matched than missed, one notch stronger than [admission](#admission-what-counts-as-a-candidate-at-all). So the orphan run terminates at "target", nothing is charged, and `cease fire` scores `2 / 2` = **1.00**.
2. **Round 2 wins, consumes, and produces nothing.** The search restarts at token 2. `approach target {target}` misses its `approach` literal but matches the rest and consumes to the end, scoring `2 / 3` = **0.67** — comfortably above the gate. It is nonetheless [barred](#the-leading-required-miss-bar): `approach` is its first required element and it matched nothing. Nothing in the transcript distinguishes this from an utterance where `approach` was never spoken at all, which is exactly why the refusal cannot be selective — see [what it costs](#the-leading-required-miss-bar).

```json
{ "intent": "cease_fire", "pattern": "cease fire",
  "score": 1.0, "accepted": true }
```

**Reading that entry:** one command fired, and **the log holds one attempt for a two-round parse**. Round 2 left no entry at all — not an accepted one, not a rejected one. If you are looking for `approach_target` and finding nothing, that absence *is* the finding.

Note what round 2 still did: it consumed `target hotel one`, so no third round re-scans those tokens. What holds round 1 at `1.00` is not that consumption but the **start test** — `approach target {target}` being *matchable* at "target", which is settled before any round runs and does not depend on round 2 winning, clearing the bar, or firing. Consumption pays off in the opposite arrangement, where the barred round comes **first** and its span would otherwise be charged to the command after it ([the bar](#the-leading-required-miss-bar)).

**The contrast trace — say the second command in full.** Utterance: **"cease fire approach target hotel one"**.

```json
{ "intent": "cease_fire", "pattern": "cease fire",
  "score": 1.0, "accepted": true }
{ "intent": "approach_target", "pattern": "approach target {target}",
  "score": 1.0, "accepted": true,
  "slots": [ { "name": "target", "value": "hotel one", "startWord": 4, "endWord": 6 } ] }
```

Both fire at `1.00`. Nothing about two commands in one breath is a problem; the first trace differs in exactly one respect — its second command's verb never reached the transcript.

**Round 1 is the half this example has always been about, and the bar does not touch it.** The start test is what keeps `cease_fire` at `1.00`: testing only what patterns *start with* would charge it for all three trailing tokens — `2 / (2 + 3)` = `0.40`, below the gate — and the cleanly spoken command would be lost. Measured over 699 utterances, that shape accounted for 11 intent changes and 1 count change before it was closed. The orphan run terminates at "target" because `approach target {target}` is *matchable* there — a property of the grammar and the tokens, settled before any round runs and untouched by where the bar sits. What the after-selection placement does buy is the other half: a barred candidate still **consumes** its span, so leading debris never lands on the command that follows it.

---

## Reading a session log

Each log entry is one **utterance**. Its `attempts` array holds one entry per *decision the recogniser logged* for that utterance. On the ordinary parse path that is one entry per *emitting* extraction round — the winner of that round, accepted or rejected. Losing candidates are never logged, and neither is a round whose winner was [barred](#the-leading-required-miss-bar). So a pattern's absence has two readings, not one: it lost selection, or **it won its round and was barred**. Neither means it was never tried — selection tries every pattern at every token. The second leaves no trace for that round, which is what makes it worth knowing about.

Six paths publish a **single synthetic attempt** instead. Three never reach a parse — the confirm/cancel resolution, the answer to a disambiguation prompt, and the pending timeout. The other three are published *after* a full parse: `no match` when it produced no result, and both follow-up entries, whose path is chosen from what the parse returned. All of them leave `pattern` empty, so an empty `pattern` is how you tell them apart:

| `rejectReason` | What happened |
|----------------|---------------|
| `no match` | No round produced a result. `intent` is empty too, and `aggregateConfidence` is `0` — *not* the `-1` sentinel, which only ever comes from a real matched span. **This no longer implies nothing matched:** an utterance whose every round was [barred](#the-leading-required-miss-bar) lands here too, having matched patterns well above the gate. |
| `cancelled via vocabulary` | Follow-up speech cancelled a pending command. The confirm case is the same entry with `accepted: true` and an empty `rejectReason`. |
| `chosen via vocabulary, now awaiting confirmation` | The speaker answered an ambiguity, and the command they chose sets `requiresConfirmation` — so it did not fire, it asked again. `accepted: false`, and the *next* utterance's entry carries the confirmation. |
| *(empty, `accepted: true`)* — or `still pending (partial: unfilled [...])` | Follow-up speech filled a pending command's missing slot. Empty reason with `accepted: true` means no required slot is left and the command fired. When the utterance filled some but not all of what was still missing, the same entry carries `accepted: false` and `still pending (partial: unfilled [...])` instead: the fill was kept, the command did not fire, and the pending is still live. |
| `follow-up re-score <n> <= 0` | Follow-up speech filled a pending command's last missing slot, but the completed command re-scored zero or negative — so it was refused rather than fired (§1's floor, on the follow-up path). The refusal neither resolves nor advances the pending, so it stays subject to the ordinary endings — a confirm word, a cancel word, preemption by a complete new command, `CancelPendingCommand()`, replacement by the next partial match, or `pendingTimeout`. What it can no longer do is progress by further follow-up speech: the same fill re-scores non-positive every time. Reaching this at all means two definitions share one intent: the completeness check and the re-score then read different patterns for the same command. |
| `timeout — cancelled` | A pending command timed out and was discarded. `inputText` is the *original* command's transcript, and `words` is empty — this entry is not an utterance at all. Under `FireAsIs` the same entry carries `accepted: true` and an empty `rejectReason` — **except for an unanswered ambiguity, which cancels under either setting**. |

| Field | What it is | Section |
|-------|-----------|---------|
| `inputText` | The buffered transcript that was parsed | — |
| `words[].confidence` | Per-word VOSK confidence — the inputs to the minimum | §5 |
| `attempts[].pattern` | The winning pattern, space-joined | §3 |
| `attempts[].score` | The full score — fidelity **and** coverage, the same number selection ranked on | §1, §2 |
| `attempts[].minScore` | The gate it was compared against | §5 |
| `attempts[].aggregateConfidence` | Minimum per-word confidence over the matched span; `-1` = no data | §5 |
| `attempts[].rejectReason` | Empty when accepted; otherwise what stopped it — a gate, a post-gate filter, or one of the pipeline events below | §5 |
| `attempts[].tiedRival` | The equally-good rival this attempt beat on registration order alone, as `intent (pattern N)`; empty when nothing tied it | §3 |
| `attempts[].tiedRivalIsSibling` | `true` when that rival was one dropped word apart. `false` covers both a cross-intent duplicate (a defect) and the winner's own second phrasing (harmless) — compare `tiedRival`'s intent against `intent` to tell them apart | §3 |
| `attempts[].slots[].startWord/endWord` | Half-open token range into the whitespace-split `inputText` | — |

The diagnoses below cover most of what sends you to the log. The first question to ask of any surprising `score` is whether the pattern's own elements account for it: work out `matched / elements` for the pattern that won, and if the reported number is lower, the difference is coverage. One caveat before you count: that shortcut assumes every miss was a *literal* — a missed required **slot** subtracts a further `1.0` from the numerator with no coverage involved (4 of 5 matched with a slot missed is `3/5` = `0.60`, not `0.80`), and a matched optional literal weighs `0.5` on both sides (§1). Rule those out first, then count the recognised tokens lying outside the match.

| Symptom | Diagnosis |
|---------|-----------|
| `score` ≈ 0.67 on a three-element pattern, slots extracted | One dropped required literal, nothing left unexplained (§1) — above the gate, so it fires. |
| `score` ≈ 0.5 on a **two**-element pattern | One dropped required literal, and two elements is the floor: half the evidence stays rejected (§1). If the dropped literal was the *first* one, the score is not what refused it — see [the bar](#the-leading-required-miss-bar), which no threshold change reaches. |
| `score` well below `matched / elements`, on a short pattern | Coverage (§2). The match left recognised tokens unexplained before or after it — most often natural trailing words the grammar does not contain. |
| A command that fired before the upgrade and no longer does, **and still logs a scored attempt** | Same cause, and the expected shape of the #65 change (§7 B2). Register the fuller phrasing, mark the trailing words optional, or lower `coverageWeight`. |
| A command that fired before the upgrade and no longer does, with **no logged attempt at all** | Not coverage — its first required element was never heard, so the round was [barred](#the-leading-required-miss-bar). No threshold or pattern length reaches it. |
| A stranded tail produced no second command, and the log holds fewer attempts than the utterance had rounds | The winner of that round missed its first required element and was [barred](#the-leading-required-miss-bar) (§7 D). Nothing is wrong with the parse — the words for a second command were not spoken. If they *were* spoken and the decoder dropped the first one, the only remedy is to say the command again; to stop a *tail* being read as a command at all, register a pattern that claims it ([Command Recognition](command-recognition.md#do-not-leave-a-bare-patterns-tail-readable-as-another-command)). |
| no result at all for a pattern that clearly part-matched | Two different causes, and they are told apart by *which* elements missed rather than how many. **A count:** the candidate missed more required elements than it matched and was refused [admission](#admission-what-counts-as-a-candidate-at-all) (§1) — it never became a candidate. **A position:** the candidate's *first* required element missed, so it competed, won and consumed, and was [barred](#the-leading-required-miss-bar) (§5). Counting matched against missed elements will not distinguish them; look at which element went missing. |
| `rejectReason` = `required slot unfilled`, `score` above the gate | The [completeness check](#completeness-independent-of-score) (§5): a required argument was never heard. Independent of `minScore` — no threshold change reaches it. Set `allowPartialMatch` to route it to slot-fill, or re-prompt off `OnUnrecognisedSpeech`. |
| `score` = 1.0 but `aggregateConfidence` below the gate | One acoustically weak word (§5). Check `words`; consider a slot alias. |
| The wrong one of two **sibling** commands fired (patterns one word apart), and its `score` looks healthy | Neither command did anything wrong: the discriminating word was dropped and selection fell through to registration order (§3). The Editor's last-match panel names the rival on a `Tied with:` line (`— sibling, one dropped word apart`), and the session log records it as `tiedRival` with `tiedRivalIsSibling: true`. Turn on [`disambiguateSiblingTies`](command-recognition.md#ambiguous-commands-ask-instead-of-guessing) to be asked instead of guessed at. |
| One of two commands with **duplicate or overlapping patterns** never fires, at a clean score | A **non-sibling** tie: the patterns score identically on every selection key, so the first-registered intent wins permanently. There is no discriminating word, so `disambiguateSiblingTies` has nothing to ask — the tie line reads `— not a sibling; check for duplicate or overlapping patterns`, and the session log carries a non-empty `tiedRival` naming a *different* intent with `tiedRivalIsSibling: false`. This is a grammar defect: remove or differentiate the duplicate pattern. |
| `rejectReason` = `entered pending (awaiting disambiguation, N choices)` | The **sibling** case above, with the flag already on: nothing fired because the speaker is being asked. Read `PendingAmbiguity` from `OnCommandPending` and prompt with the N choices. |
| Accepted with an empty `slots` array where you expected a value | A bare pattern out-ranked the slot-filled one. Coverage closes the common case (§7 B). If you still see it, the stranded value's first word probably begins another pattern, so coverage charged the bare form nothing — or `coverageWeight` is `0`. |

---

## See Also

- [Command Recognition](command-recognition.md) — the surrounding pipeline: patterns, slots, buffering, eager flush, pending commands
- [VoxrCommandRecogniser](api/command-recogniser.md) — every tuning field with its default
- [Editor Testing](editor-testing.md) — the debug window and the session debug log
- [Batch Test Runner](api/batch-test-runner.md) — regression-test a threshold change
- [Troubleshooting](troubleshooting.md) — symptom-first index of common problems
- [Known Limitations](../KNOWN_LIMITATIONS.md) — the acoustic-model quirks behind low-confidence words
