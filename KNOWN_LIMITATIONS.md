# Known Limitations

This document collects known limitations of the VoXR that aren't
bugs to fix but rather constraints rooted in the underlying VOSK acoustic model,
voice recognition in general, or deliberate architectural choices. The goal is
to give consumers (and our future selves) a single place to look when something
"weird" happens, before assuming it's a regression.

Each entry includes a short repro, the root cause, and a workaround (if any).

---

## VOSK Acoustic Model

Limitations rooted in the small English VOSK model
(`vosk-model-small-en-us-0.15`) we ship with. Switching to a larger model would
mitigate some of these but at the cost of memory and download size.

### "to" misrecognised as "two"

- **Status**: still open. One TTS fixture stopped reproducing it after
  phrase-chunked grammar emission
  ([#45](https://github.com/jinwoo1601/VoXR-Speech-Recognition/issues/45)):
  `switch to navigation` is now a single grammar entry, that fixture decodes
  correctly and fires `mode_navigation`, and it has been re-baselined from a
  negative pin to a positive one. That is the whole of the evidence — one
  synthesised-speech fixture. The limitation was originally observed in **human
  speech on device**, and has **not** been re-tested there since; the in-headset
  A/B against this and the other documented confusion pairs has not been run.
  Treat #45 as a plausible mitigation, not a fix.
- **Repro**: Say "switch to weapons". VOSK transcribes `switch two weapons`.
  Observed in human speech on Quest (v2.5 test matrix Phase 4.5); not re-tested
  since #45. The TTS fixture for the same phrase has always been recognised
  correctly, and before #45 the "switch to navigation" fixture was transcribed
  `switch two navigation` and matched no command — so the substitution is phrase-
  and delivery-dependent, not uniform.
- **Where seen**: v2.5 test matrix Phase 4.5; WAV-replay acoustic suite (v1.5 dev).
- **Root cause**: The small English model is acoustically biased toward "two"
  in this context, especially when the speaker emphasises the vowel slightly
  or says it quickly. The grammar token splitter is correct — it produces
  `["switch", "to", "weapons"]` — but VOSK never feeds it the right phonemes
  to match.
- **Workaround**:
  - Prefer alternate patterns that avoid short function words. The sample's
    `mode_weapons` command uses both `["switch", "to", "weapons"]` and
    `["weapons", "mode"]`; the latter recognises reliably.
  - When designing your own commands, avoid `to`, `for`, `four`, `or`, `are`
    and similar short homophones inside required tokens — and where you must
    use one, prefer keeping it inside a run of required literals rather than
    adjacent to a slot boundary, so that phrase chunking has a chance to help.
    A slot or optional literal ends a run, so a function word stranded beside
    one gets no phrase entry spanning it at all.

### "all" misrecognised as "fall" when navigation words dominate grammar

- **Repro**: While the `navigation` set is active (contains the
  `fall back from target {target}` pattern), say "all modes". VOSK frequently
  transcribes `fall modes`, and the command fails to match.
- **Where seen**: v2.4 test matrix Phase 4.5 — took 3+ attempts to trigger
  `mode_all` from navigation mode. Documented in v2.4 Known Bugs.
- **Root cause**: When a phonetically similar word ("fall") is prominent in
  the active grammar, VOSK's constrained decoder prefers it over less frequent
  words ("all"). This is a general property of grammar-mode decoding: words
  that appear in more patterns are effectively weighted higher.
- **Workaround**:
  - The sample's `mode_all` command uses both `all modes` and `enable all`.
    `enable all` is the reliable trigger when navigation words are active.
  - For your own commands, offer a phonetically distinct alias for any short
    keyword that clashes with another grammar word.

### "cease fire" misrecognised as "safe five" when weapons set inactive

- **Repro**: Switch to navigation-only mode, then say "cease fire". VOSK
  transcribes `safe five` (or similar) because "cease" and "fire" are no
  longer in the active grammar.
- **Where seen**: v2.4 test matrix Phases 3.5, 4.4 (notes).
- **Root cause**: Same constrained-decoder behaviour as the "all"/"fall"
  issue. With "cease" and "fire" removed from the grammar, VOSK maps the
  phonemes to the nearest in-grammar words. This is actually *working as
  intended* for set restriction — the command is correctly rejected — but it
  means you cannot rely on the raw transcript to explain why recognition
  failed when the user is in the "wrong" mode.
- **Workaround**: None needed for correctness. If you surface raw transcripts
  for debugging, expect surprising substitutions when the user speaks out of
  the active grammar.

### Abbreviations and letter sequences map to [unk]

- **Repro**: Say "close distance cqb target alpha three". VOSK produces
  `close distance [unk] target alpha three`; the `range=cqb` slot fails.
- **Where seen**: v2.0 test matrix Phase 3.2.
- **Root cause**: The small English model has no entries for military/radio
  abbreviations like "cqb", "pdc", etc. Grammar mode forces VOSK to choose
  something in-vocabulary, so abbreviations become `[unk]`.
- **Workaround**:
  - Spell out the phrase in the slot value (e.g. `close quarters` instead of
    `cqb`).
  - Alternatively, add phonetic aliases (`see queue bee` → `cqb`) so VOSK can
    match the phoneme sequence.

### Single-character literals ("a") unreliable

- **Repro**: Say "launch a missiles target hotel one" with `?a` as an
  optional literal in the pattern. VOSK transcribes "a" as "on" or drops it
  entirely.
- **Where seen**: v2.1 test matrix Phase 3.1 (and Phases 3.3–3.4, 6A.2 where
  VOSK silently dropped "a").
- **Root cause**: Very short function words carry almost no acoustic
  information. In grammar mode, any phonetically similar word that exists in
  the active grammar ("on" from `close on target`) outcompetes "a".
- **Workaround**:
  - Don't use single-character literals or single-character alias keys inside
    patterns. The parser now emits a validation warning for single-character
    slot values and alias keys.
  - If you need quantity "a" (as in "fire a torpedo"), declare it as an alias
    to a longer canonical value (`a` → `one`). The alias resolves correctly
    when VOSK *does* hear it (see v2.1 Phase 4.2), and is harmless when
    dropped.

### "two" consistently scores low confidence

- **Repro**: Say any number-sequence command containing "two" (e.g.
  "orient heading two seven zero"). The per-word confidence for "two" is
  almost always 0.50 regardless of grammar size or pronunciation clarity.
- **Where seen**: v2.4 test matrix Phase 9 notes, v2.2 Phase 10.1.
- **Root cause**: Quirk of the small English model — "two" shares phonemes
  with "to"/"too" and the acoustic posterior for this phoneme cluster is flat.
  The grammar constraint only narrows the candidate list; it doesn't sharpen
  the posterior.
- **Workaround**: None. If you set `minConfidence` too strictly, NumberSequence
  commands containing "two" will be rejected. The default `minConfidence=0.4`
  accommodates this; don't push it above 0.5 unless you've verified your
  vocabulary avoids "two".

### Free speech mode is unreliable for numeric and literal commands

- **Repro**: Set `freeSpeechMode=true`, rebuild, say "orient heading two seven
  zero". VOSK transcribes `korean heading to seven zero` ("orient" → "korean",
  "two" → "to" homophone). Pattern fails to match.
- **Where seen**: v2.2 test matrix Phase 10.1 (FAIL), Phase 10.4 notes; v2.0
  Phase 6.2 (PARTIAL — "launch" heard as "lunch or").
- **Root cause**: Without grammar constraint, VOSK's small model has a much
  larger candidate vocabulary and commits to phonetically plausible but
  wrong transcriptions. Homophones ("to"/"two", "four"/"for") and uncommon
  words ("orient") are the first to break.
- **Workaround**:
  - Keep `freeSpeechMode=false` (the default) for command-driven UX.
  - Only enable free speech when you *need* arbitrary dictation (e.g. a
    note-taking feature) and accept that command matching will be best-effort.

---

## Voice Recognition (General)

Limitations inherent to streaming speech recognition, independent of which
model you use.

### Cough, hum, and noise can trigger false matches in grammar mode

- **Repro**: Cough, hum, or tap the microphone while recognition is active.
  VOSK occasionally transcribes the noise as a short in-grammar word ("on",
  "from", "four"), which may match a pattern prefix.
- **Where seen**: v2.0 test matrix Phase 5.2 (PARTIAL — deferred to voice
  activation); v2.1 Phase 5A.2 (cough heard as "on", "from").
- **Root cause**: Grammar-constrained VOSK *must* choose an in-vocabulary
  word; it has no "silence" output. Short noises that have any voicing will
  be mapped to whichever grammar word their phoneme vector is closest to.
  Since short words ("on", "to", "four") sit closest to low-energy noise in
  phoneme space, they are the most frequent false triggers.
- **Workaround**:
  - Gate recognition with a push-to-talk button: call
    `VoxrSpeechRecogniser.StopRecognition()` / `StartRecognition()` around
    the button press so the parser only looks at intentional speech. This
    is the recommended approach for noisy environments.
  - Tune `minConfidence` upward — noise-derived matches usually have
    confidence well below 0.5. Don't push it past ~0.5 or you will reject
    NumberSequence commands (see "'two' consistently scores low" above).
  - Prefer longer, multi-token commands; false triggers rarely produce
    more than one in-grammar word in a row.

### VOSK's VAD splits mid-command pauses into separate utterances

- **Repro**: Say "orient heading" *pause ~0.8s* "two seven zero". VOSK
  emits two independent final results; neither matches a pattern alone
  ("orient heading" is missing the digit slot, "two seven zero" is missing
  the command prefix).
- **Where seen**: v2.2 test matrix Phase 9.2 (KNOWN LIMITATION).
- **Root cause**: VOSK's voice activity detector treats pauses as utterance
  boundaries and flushes an interim final. The parser sees two disconnected
  transcripts, not one.
- **Workaround**:
  - `bufferWindow` (default 0.5s) is exactly for this case — it merges
    consecutive VOSK results before parsing. See the *Architecture* section
    below for tuning notes.
  - If the pause is longer than `bufferWindow`, the command is lost.
    Re-prompt the user or widen the window (up to ~2.0s on Quest 3 — see
    below).
  - A cross-utterance buffer that merges arbitrarily distant utterances
    was evaluated and deferred: the false-positive risk from concatenating
    genuinely unrelated speech outweighs the benefit for this edge case.

### Set restriction cannot produce meaningful rejection transcripts

- **Repro**: In weapons-only mode, say "approach target alpha one". The
  recogniser correctly rejects the command, but the logged transcript is
  `[unk] target alpha one`, not `approach target alpha one`.
- **Where seen**: v2.4 test matrix Phases 2.4–2.6, 3.5–3.7, 4.2, 4.4.
- **Root cause**: Grammar is rebuilt to contain only the active sets' words,
  so "approach" is no longer in the vocabulary and maps to `[unk]` at the
  acoustic level. This is *correct behaviour* for set restriction — the
  smaller grammar is the whole point — but you cannot tell the user
  "you said X, but X isn't available in the current mode" because the SDK
  never saw the word "X".
- **Workaround**: If you need "wrong-mode" UX (e.g. a hint that says "you
  asked for a navigation command while in weapons mode"), use the single-set
  + superset grammar approach and do mode gating in your `OnCommand` handler
  rather than via `SetActiveSets()`. See the *Active set switching* entry
  below for the same trade-off.

### Smaller grammars don't always yield measurably higher recognition scores

- **Repro**: Run the same phrase with all three sets active, then with only
  the one set it belongs to. Compare scores and confidences.
- **Where seen**: v2.4 test matrix Phase 9 (all four sub-tests passed but
  showed *no measurable difference*).
- **Root cause**: VOSK's decoder is already very confident on commands built
  from distinctive multi-word phrases. Restricting the grammar further does
  reduce the search space, but if the full grammar was already producing
  `score=1.00` / `conf=1.00`, there is no headroom for the restriction to
  improve. The benefit of set restriction is primarily *rejecting out-of-set
  commands* (which Phases 2, 3, 5 confirmed), not boosting in-set accuracy.
- **Workaround**: None needed. Don't expect confidence gains from set
  switching alone — its value is correctness (blocking wrong-mode commands),
  not accuracy.

---

## Architecture and Design

Limitations that come from how the SDK is structured. Most of these are
deliberate trade-offs rather than oversights.

### Active set switching has a brief audio gap

- **Repro**: Trigger a `SetActiveSets()` call (e.g. via a `mode_*` command),
  then immediately try to speak the next command. The first one or two words
  of the second utterance are dropped.
- **Where seen**: v2.5 test matrix Phase 5.4. After saying "navigation mode"
  the user said "fall back from target hotel two" too quickly, and VOSK only
  heard `target hotel two` — the leading three words were lost.
- **Root cause**: `SetActiveSets()` calls `RebuildParserAndGrammar()` which
  stops AudioCapture, applies the new grammar to the VOSK recogniser, and
  restarts AudioCapture. The full sequence takes ~50ms minimum on Quest 3,
  during which the microphone isn't being read. Any speech in that window is
  dropped at the audio layer, before VOSK ever sees it.
- **Workaround**:
  - After triggering a mode switch, pause for ~500ms before speaking the next
    command. In a game UI you can gate user input via the
    `[CommandDemo] Switched to <X> mode` log marker (or wire a callback off
    `OnCommandRecognised` for the `mode_*` intents).
  - If you need seamless switching, prefer the **single-set + grammar
    superset** approach: configure all your commands in one set and gate them
    in your `OnCommand` handler instead of swapping active sets at runtime.

### Validation warnings re-emit on every active-set switch

- **Repro**: Wire any slot with a single-character alias (e.g. `a` → `one`)
  and call `SetActiveSets()` repeatedly. The
  `[VoxrCommandParser] Slot 'quantity' has single-character alias "a"...`
  warning fires on every switch, not just at initial Configure.
- **Where seen**: v2.5 test matrix Phases 5–8. Visible in logcat after every
  mode-switch command.
- **Root cause**: `SetActiveSets()` constructs a fresh `VoxrCommandParser` via
  `RebuildParserAndGrammar()`, and the parser ctor unconditionally re-runs its
  validation passes — `RunValidationWarnings()` for slot values and aliases, and
  the pattern-shape check that flags a droppable required literal before a slot.
  The warnings are correct, just noisier than they should be.
- **Workaround**: None at user level. This is a candidate for cleanup —
  validation should run once per `Configure()` call, not per parser rebuild.
  Filed as a low-priority follow-up.

### Default `bufferWindow` is too short for split commands on Quest 3

- **Repro**: Speak a two-part command with a deliberate mid-command pause:
  "launch missiles" ... "target hotel one". On Quest 3 the gap measured
  between VOSK results is often ~1.9–2.1s, so any window shorter than that
  flushes before the second half arrives and the command is lost. The current
  0.5s default is well short of this; even the former 1.5s default (v2.3) was
  marginal — just under the typical gap.
- **Where seen**: v2.3 test matrix Phase 4.1 (pass-with-note), Phase 8.2
  retry (v2.4), and general notes — 2.0s is a more reliable value on Quest 3.
- **Root cause**: VOSK on Quest 3 emits final results with ~0.5–1.0s latency
  after speech ends, compounding any mid-command pause the speaker takes.
  The default matches typical PC latency (1.5s in v2.3, later lowered to 0.5s
  for a snappier PC baseline); on Quest hardware the buffer needs more slack.
- **Workaround**:
  - Set `bufferWindow=2.0` in the inspector for Quest builds.
  - Do not push beyond ~2.5–3.0s: the test matrices found that long windows
    start merging genuinely unrelated utterances ("cross-command bleed").

### Utterance buffer drops split commands if the pause exceeds `bufferWindow`

- **Repro**: Pause longer than `bufferWindow` mid-command. The first half
  flushes before the second half arrives, and neither matches a pattern.
- **Where seen**: v2.3 Phase 5.3, v2.3 Phase 4.1 note.
- **Root cause**: By design — the buffer has to flush eventually or it would
  merge unrelated speech. There is no retry.
- **Workaround**:
  - Tell users to speak commands in one breath, or provide a visible "hold
    to talk" affordance that gates recognition to a single burst.
  - For conversational UX, prefer shorter patterns that complete within one
    VOSK final result.

### A command that is also a prefix of a longer one can't be both instant and split-safe

- **Repro**: Register `["fire"]` and `["fire", "at", "{target}"]`, enable
  `eagerFlushOnCompleteMatch`, then say "fire" and pause longer than
  `bufferWindow` before "at hotel one". Eager flush deliberately does *not* fire
  "fire" early (it is a prefix of the longer command), so it waits the full
  window — and if the pause exceeds it, only "fire" is recognised.
- **Root cause**: Inherent, not a bug. A complete command that is also a prefix
  of a longer command is genuinely ambiguous until either more speech arrives or
  the window expires. No time/parse strategy makes it both zero-latency and
  correct: firing instantly would drop the longer command; waiting adds latency.
  Eager flush resolves this conservatively by waiting (correctness over latency);
  non-prefix commands are unaffected and fire instantly.
- **Workaround**:
  - Set `prefixHoldSeconds` (e.g. 0.5–0.8) to bound the wait. The ambiguity is
    only over a continuation, which a continuing speaker begins almost
    immediately, so the prefix command fires after that much silence instead of
    the full `bufferWindow`. It shortens the wait rather than removing it — the
    tradeoff above is unchanged, just cheaper.
  - Use push-to-talk (`VoxrPushToTalkController`); `ReleaseTalk` calls
    `FlushPendingBuffer()`, giving the prefix command a deterministic,
    zero-latency endpoint.
  - Avoid registering commands that are exact prefixes of others when low latency
    matters — e.g. give the shorter command a distinct extra keyword.

### A dropped required literal hands the utterance to the bare sibling pattern

- **Repro**: Register `["decelerate"]` and `["decelerate", "by", "{burn_level}"]` on
  one command, then say "decelerate hard burn" (the "by" elided by the speaker, or
  dropped by VOSK). The bare pattern fires at score 1.0 and `burn_level` is empty —
  the command runs at its default level, with nothing reporting that a burn level
  was heard and thrown away. Observed in-headset.
- **Root cause**: Inherent to comparing siblings by score. The slot-filled pattern
  is charged `RequiredLiteralMissPenalty` for the missing "by" (1.5/3 = 0.5) while
  the bare pattern matches perfectly (1/1 = 1.0), so the bare pattern wins on score
  and sequential extraction finds nothing to do with the stranded "hard burn". No
  threshold or penalty tuning reaches this case: a score normalised to 1.0 is the
  ceiling, so nothing can outrank the bare pattern while it matches exactly. Short
  unstressed function words are the most-dropped tokens in practice, which is what
  makes the shape worth avoiding rather than tolerating.
- **Workaround**: Mark the droppable literal optional — `["decelerate", "?by",
  "{burn_level}"]`. An omitted optional leaves both sides of the score ratio, so the
  slot-filled pattern also scores 1.0 with or without the word, and wins as the
  candidate covering more of the utterance. Removing the literal outright
  (`["decelerate", "{burn_level}"]`) works too, at the cost of the phrasing.
  `VoxrCommandParser` logs a validation warning at construction naming the literal
  and the slot at risk. The scan follows what the parser itself compares, so it
  covers the hazard across *different intents* as well as within one command, behind
  a run of two or more literals, and after expanding a bare pattern's own optional
  elements; only patterns with more than six optionals are compared unexpanded.
  **The optional-literal swap has two costs**, neither of them a no-op: a matched
  optional literal scores 0.5 on both sides of the ratio where a required one scores
  1.0, so any *imperfect* match scores strictly lower than before and may now fall
  under `minScore`; and an optional literal no longer anchors the element after it,
  so a following slot can claim adjacent tokens the literal never introduced (with
  `orient heading {heading} ?mark {?elevation}`, a stray fourth digit is absorbed as
  `elevation` and wins on span, where the required form scored 0.7 and dropped it).
  Prefer the swap where the trailing slot's vocabulary is distinct from its
  neighbours'; be careful where it is a `NumberSequence`.

### Confidence of `-1.00` means "no data", not "zero confidence"

- **Repro**: Say a command with leading filler or out-of-grammar words
  ("okay cease fire"). VOSK transcribes `[unk] cease fire`; the sliding start
  skips `[unk]`, and the logged per-command confidence is `-1.00`.
- **Where seen**: v2.1 Phase 2.3 (triggered the bug that introduced the
  sentinel), v2.2 Phase 6.2, v2.3 Phase 3.2–3.3, v2.4 Phase 3.8.
- **Root cause**: When the matched span of the transcript contains only
  `[unk]` tokens (or no VOSK confidence data at all), the parser cannot
  compute a meaningful average. It returns `-1.0` as a sentinel meaning
  "no data" and `VoxrCommandRecogniser` treats this as *not subject to*
  `minConfidence`, so the command still fires based on pattern-match score.
  Without this sentinel (v2.0 used raw `0.0`), genuine noise that drove
  confidence to 0 was indistinguishable from "no data" and either bypassed
  the threshold or was falsely rejected.
- **Workaround**: None needed. If you display confidence in a debug UI,
  treat `-1.00` specially ("no data" / "n/a"), not as "zero".

---

## Hardware Audio

Limitations specific to capturing audio on target hardware. These are
pre-solved inside `NativeBridge~` but are worth documenting because they
constrain how the native layer is structured and will matter again on any
future device port.

### AAudio input delivers silence on Quest 3 — must use Java AudioRecord

- **Repro**: Build the SDK with `audio_capture_aaudio.cpp` as the active
  capture backend. `AAudioStream` opens and starts without error and
  callbacks fire on schedule, but the audio buffer is near-zero regardless
  of input preset (`GENERIC`, `VOICE_RECOGNITION`, `UNPROCESSED`).
- **Where seen**: Quest 3 bring-up (2026-03-31).
- **Root cause**: Platform-specific AAudio input bug on Quest 3 firmware.
  The native layer switched to the Java `AudioRecord` API via JNI
  (`audio_capture_audiorecord.cpp`), which routes correctly to the headset
  microphone. The old AAudio implementation is retained for reference.
- **Workaround**: Already applied — the shipped build uses AudioRecord. If
  porting to another Android device, test AAudio first; it may work outside
  Quest 3. Do not remove the AAudio files; they are a fallback seed.

### `vosk_recognizer_accept_waveform_f` is broken on prebuilt arm64 `libvosk.so`

- **Repro**: Feed float samples directly to VOSK via
  `vosk_recognizer_accept_waveform_f`. Audio levels reach the library
  correctly, but every recognition result is empty.
- **Where seen**: Quest 3 bring-up.
- **Root cause**: Bug in the prebuilt arm64 `libvosk.so` — the int16 entry
  point (`vosk_recognizer_accept_waveform_s`) works, but the float entry
  point does not produce usable output. Not investigated upstream; likely a
  build-config issue in Alphacephei's prebuilt.
- **Workaround**: Already applied — `vosk_bridge.cpp` converts float samples
  to int16 before feeding VOSK. If you rebuild `libvosk.so` yourself, retest
  the float path before switching to it — the workaround cost is one extra
  copy per buffer, which is negligible.

### Quest 3 microphone gain is low; AGC is applied before VOSK

- **Repro**: Capture raw samples from Quest 3's `VOICE_RECOGNITION` source
  while speaking at normal volume. Peak levels sit around 0.04–0.4 on a
  `[-1, 1]` scale.
- **Where seen**: Quest 3 bring-up.
- **Root cause**: Quest 3's mic pipeline outputs conservatively low levels
  — likely intentional headroom for louder-than-expected input — but this
  leaves VOSK's acoustic frontend operating near its noise floor for quiet
  speech.
- **Workaround**: Already applied — an AGC stage in `vosk_bridge.cpp`
  targets a configurable dB level before converting to int16. The target is
  exposed on `VoxrSpeechRecogniser` as the `micGainTargetDb` inspector field
  (default `-18 dB`, calibrated for Quest 3). Tune it if you observe clipping
  or under-gain on a different device.

---

## Notes

This file is meant to grow as we discover more limitations. When adding a new
entry, follow the existing structure: short repro, where seen (test matrix
reference if applicable), root cause, workaround. Group entries by category —
the categories above are a starting point but feel free to add more (e.g.
"Threading", "Build/Deploy") as needed.
