# Known Limitations

This document collects known limitations of the VOSK Unity XR SDK that aren't
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

- **Repro**: Say "switch to weapons". VOSK transcribes `switch two weapons`.
- **Where seen**: v2.5 test matrix Phase 4.5.
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
    and similar short homophones inside required tokens.

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
  `[VoskCommandParser] Slot 'quantity' has single-character alias "a"...`
  warning fires on every switch, not just at initial Configure.
- **Where seen**: v2.5 test matrix Phases 5–8. Visible in logcat after every
  mode-switch command.
- **Root cause**: `SetActiveSets()` constructs a fresh `VoskCommandParser` via
  `RebuildParserAndGrammar()`, and the parser ctor unconditionally re-runs
  `RunValidationWarnings()`. The warnings are correct, just noisier than they
  should be.
- **Workaround**: None at user level. This is a candidate for cleanup —
  validation should run once per `Configure()` call, not per parser rebuild.
  Filed as a low-priority follow-up.

---

## Notes

This file is meant to grow as we discover more limitations. When adding a new
entry, follow the existing structure: short repro, where seen (test matrix
reference if applicable), root cause, workaround. Group entries by category —
the categories above are a starting point but feel free to add more (e.g.
"Hardware Audio", "Threading", "Build/Deploy") as needed.
