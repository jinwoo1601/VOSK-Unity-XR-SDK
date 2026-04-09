# v3 Phase 1 — Text Injection (Editor-Only) Test Matrix

v3 Phase 1 (shipped as 0.10.0) adds a text injection API so command
iteration no longer requires a Quest deploy/logcat cycle. The whole
point of this version is **no device** — every test below runs in the
Unity Editor with no native bridge, no microphone, and no Android build.

The matrix has two halves:

1. **Phases 0–1: API surface verification.** Bulk of this is covered by
   Play Mode tests under `Tests/Runtime/`. Phase 0 just runs them; Phase 1
   spot-checks anything not exercised by an automated test.
2. **Phases 2–7: Workflow verification.** A developer adds a small driver
   to a scene, hits Play, and confirms each v2.0–v2.5 feature can be
   iterated on without ever putting the headset on. This is what the
   version is actually for.

## Prerequisites

1. **Consuming project**: `D:\Game Development\voxsdk` (per
   `reference_unity_test_project.md`) — the package is referenced via
   `file:` and `manifest.json` already has
   `"testables": ["com.jinwoo1601.vosk-xr"]`.
2. **No device required.** Quest does not need to be connected. Native
   bridge does not need to be built. The package can even run on a
   machine that has never seen `libvosk-bridge`.
3. **Unity 6000.3.7f1** (matches the Android toolchain pinned in
   `CLAUDE.md`, but the Editor pieces work on any 2021+ Unity).

## Driver Snippet (used by Phases 2–7)

The CommandDemo sample doesn't expose injection in the Inspector, so
manual phases use this small driver. Drop it into the consuming
project's `Assets/` folder, attach to the same GameObject as
`VoskCommandRecogniser`, and use the right-click context menu items to
fire each test phrase:

```csharp
using UnityEngine;
using VoskXR;
using VoskXR.Commands;

public class V3InjectionDriver : MonoBehaviour
{
    [SerializeField] VoskCommandRecogniser commandRecogniser;
    [SerializeField] VoskSpeechRecogniser speechRecogniser;
    [SerializeField, TextArea] string text = "launch all missiles target hotel one";
    [SerializeField, Range(0f, 1f)] float wordConfidence = 1.0f;

    [ContextMenu("Inject Text (command layer)")]
    void InjectText()
    {
        var words = VoskSpeechRecogniser.CreateSimulatedWords(text, wordConfidence);
        commandRecogniser.InjectText(text, words);
    }

    [ContextMenu("Inject Result (speech layer)")]
    void InjectResult()
    {
        var words = VoskSpeechRecogniser.CreateSimulatedWords(text, wordConfidence);
        speechRecogniser.InjectResult(text, words);
    }

    [ContextMenu("Flush Pending Buffer")]
    void Flush() => commandRecogniser.FlushPendingBuffer();
}
```

For Phases 2–7, edit the `text` field in the Inspector, then right-click
the component header → **Inject Text (command layer)**. Watch the
Console for the same log lines `CommandDemo` already prints
(`[CommandDemo] Command: ...`, `Batch: ...`, `Unrecognised: ...`).

The driver does **not** need recognition to be running, the model to be
loaded, or the bridge to be available. That is the v3 promise; verifying
it is part of Phase 0.

### Scene wiring — required fields

`VoskCommandRecogniser` has its own `Speech Recogniser` SerializeField
(separate from the one on the driver and `CommandDemo`). **This field
must be wired** — its `OnEnable()` bails out silently when it is null,
which skips the `speechRecogniser.OnResult += HandleResult` subscription.
If you miss this, `Inject Text (command layer)` still works (it calls
`HandleResult` directly), but `Inject Result (speech layer)` fires into
the void and every Phase 1 row 1.2 / Phase 6 row looks broken.

Minimum wiring checklist before starting Phase 1:
- `V3InjectionDriver`: `commandRecogniser` and `speechRecogniser` both set
- `VoskCommandRecogniser`: `Speech Recogniser` set to the same component
- `CommandDemo`: `Recogniser` (speech) and `Command Recogniser` both set
- All four components on the same GameObject

---

## Phase 0: Test Runner — Automated Coverage

Open **Window → General → Test Runner** in the consuming project. Both
suites should appear. Run all and confirm zero failures.

| #   | Suite                                              | Mode      | Tests | Result |
|-----|----------------------------------------------------|-----------|-------|--------|
| 0.1 | `VoskSpeechRecogniserInjectionTests`               | Play Mode | 16    | PASS   |
| 0.2 | `VoskCommandRecogniserInjectionTests`              | Play Mode | 14    | PASS   |
| 0.3 | `VoskCommandParserTests` (regression — must still pass) | Play Mode | 60    | PASS   |
| 0.4 | `VoskAssetConversionTests` (regression — must still pass) | Play Mode | 15 | PASS   |
| 0.5 | `VoskCommandSetTests` (regression — must still pass)| Play Mode | 5     | PASS   |
| 0.6 | `ParseWordsFromJsonTests` (7) + `ParseAlternativesFromJsonTests` (6) | Play Mode | 13 | PASS |
| 0.7 | `VoskSpeechRecogniserLifecycleTests`               | Play Mode | 6     | PASS   |
| 0.8 | `VoskNumberParserTests` (regression — must still pass) | Play Mode | 16 | PASS   |
| 0.9 | `ModelExtractorValidationTests` (5) + `VoskBridgeErrorCodeTests` (10) | Edit Mode | 15 | PASS |

The counts under `0.1` and `0.2` include `[TestCase]` parameterised rows
(`CreateSimulatedWords_TokenCountAndConfidence`,
`CreateSimulatedWords_EmptyOrWhitespace_ReturnsEmpty`,
`InjectText_NullOrWhitespace_NoOps`). If Test Runner shows different
totals, investigate before continuing — counts drifting upward is usually
fine (new tests added), counts drifting downward means something was
removed and should be explained.

---

## Phase 1: API Surface Spot-Checks (Editor)

Things the unit tests don't (and probably shouldn't) cover. All run in
Editor Play Mode using the driver script.

| #   | Setup                                                                 | Action                                                           | Expected                                                                                       | Result |
|-----|-----------------------------------------------------------------------|------------------------------------------------------------------|------------------------------------------------------------------------------------------------|--------|
| 1.1 | Scene with `VoskSpeechRecogniser` + `VoskCommandRecogniser`, neither initialised, no native plugin present. Verify the "Scene wiring" checklist above is complete. | Press Play, run driver `Inject Text` with `"cease fire"` (after Configure) | No `DllNotFoundException`, no native bridge calls, command event fires                         | PASS   |
| 1.2 | Same scene, `VoskSpeechRecogniser.IsModelReady == false`              | Driver `Inject Result` with `"hello world"`                      | `OnFinalResult` and `OnResult` both fire even though no model is loaded. `Unrecognised: "hello world"` appears in Console after the buffer window elapses. | PASS |
| 1.3 | Driver script with default `wordConfidence = 1.0`                     | `Inject Text` `"cease fire"`                                     | Console shows `[CommandDemo] Command: cease_fire (confidence=1.00, ...)` — *already covered by 1.1 output, no re-test needed* | PASS (by 1.1) |
| 1.4 | Driver script with `wordConfidence = 0.2` (below default `minConfidence=0.4`) | `Inject Text` `"cease fire"`                             | No command event fires; no `Unrecognised` event either (silently filtered)                     | PASS   |
| 1.5 | Same as 1.4 but `wordConfidence = 0.4`                                | `Inject Text` `"cease fire"`                                     | Command event fires (boundary inclusive — code uses `<`, not `<=`)                             | PASS   |
| 1.6 | `VoskCommandRecogniser` with no `Configure()` call (disable `CommandDemo` component; assets unset) | `Inject Text` `"cease fire"`                         | `LogWarning: "InjectText called before parser is ready..."`, no exception, no event            | PASS   |

---

## Phase 2: Command-Layer Injection — Slot Conversion (Code Path)

Re-runs the core slot-extraction phrases from
`v2.5-test-matrix.md` Phases 1–3 via injection. Use `CommandDemo` with
`useInspectorAuthoring = false` so the code-based `Configure()` runs.
For each row, set `text` on the driver and trigger
`Inject Text (command layer)`.

| #   | Inject text                                       | Expected Intent    | Expected Slots                                       | Why                                                | Result |
|-----|---------------------------------------------------|--------------------|------------------------------------------------------|----------------------------------------------------|--------|
| 2.1 | `cease fire`                                      | cease_fire         | (none)                                               | Baseline literal command                           | PASS (score=1.00) |
| 2.2 | `launch missiles target hotel one`                | launch_weapon      | weapon=missiles, target=hotel one                    | `{?quantity}` optional slot omitted                | PASS (score=0.80 — optional slot absent) |
| 2.3 | `launch all missiles target hotel one`            | launch_weapon      | quantity=all, weapon=missiles, target=hotel one      | `{?quantity}` optional slot present                | PASS (score=1.00) |
| 2.4 | `fire torpedoes at bravo two`                     | launch_weapon      | weapon=torpedoes, target=bravo two                   | Alternate pattern, multi-word target               | PASS (score=0.80 — optional slot absent) |
| 2.5 | `shoot jackals`                                   | launch_weapon      | weapon=jackal (alias resolved)                       | Alias `jackals` → `jackal`                         | PASS (score=1.00) |
| 2.6 | `fire a torpedoes at alpha three`                 | launch_weapon      | quantity=one (alias), weapon=torpedoes, target=alpha three | Quantity alias `a` → `one`                   | PASS (score=1.00) |
| 2.7 | `close distance torpedo range target hotel two`   | set_distance_named | range=torpedo range, target=hotel two                | Multi-word enumerated value                        | PASS (score=1.00) |

---

## Phase 3: NumberSequence Slots via Injection

Mirrors `v2.5-test-matrix.md` Phase 3. Verifies `VoskSlotType.NumberSequence`
runs identically when fed text instead of audio.

| #   | Inject text                                  | Expected Intent | Expected Slots                            | Why                                            | Result |
|-----|----------------------------------------------|-----------------|-------------------------------------------|------------------------------------------------|--------|
| 3.1 | `orient heading two seven zero`              | set_heading     | heading=270                               | 3-word number sequence (maxWords)              | PASS   |
| 3.2 | `orient heading nine`                        | set_heading     | heading=9                                 | 1-word number sequence (minWords)              | PASS   |
| 3.3 | `orient heading three five mark two`         | set_heading     | heading=35, elevation=2                   | Two NumberSequence slots in one pattern        | PASS   |
| 3.4 | `set heading one eight zero`                 | set_heading     | heading=180                               | Alternate pattern, same slot                   | PASS   |

---

## Phase 4: Buffered Path & FlushPendingBuffer

Verifies the v2.3 utterance buffer works under injection and that the
new `FlushPendingBuffer()` API releases queued speech immediately. Set
`bufferWindow = 1.5` (default) on the recogniser.

| #   | Setup                                           | Action                                                                                                    | Expected                                                                                          | Result |
|-----|-------------------------------------------------|-----------------------------------------------------------------------------------------------------------|---------------------------------------------------------------------------------------------------|--------|
| 4.1 | `bufferWindow = 1.5`                            | `Inject Text` `"cease fire"`                                                                              | No command fires for ~1.5 s, then `cease_fire` event appears in Console (Update flushes buffer)   | PASS   |
| 4.2 | `bufferWindow = 1.5`                            | `Inject Text` `"cease fire"`, then immediately `Flush Pending Buffer`                                     | `cease_fire` fires synchronously, before the 1.5 s window elapses                                 | PASS   |
| 4.3 | `bufferWindow = 30` *(widened from 1.5 — three Inspector edits in <1.5s is not manually achievable; the row verifies batching + sequential extraction, not timing)* | `Inject Text` `"cease fire"`, then `Inject Text` `"launch missiles target hotel one"`, then `Flush Pending Buffer` | Two events fire from one batch (`cease_fire` then `launch_weapon`) — sequential extraction (v2.3). `Batch: 2 command(s) from single utterance` log line is the marker. | PASS   |
| 4.4 | `bufferWindow = 1.5`, no prior injection        | `Flush Pending Buffer`                                                                                    | No-op, no exception, no event                                                                     | PASS   |
| 4.5 | `bufferWindow = 0` (disabled)                   | `Inject Text` `"cease fire"`                                                                              | `cease_fire` fires synchronously (v2.2 unbuffered behaviour)                                      | PASS   |

> After Phase 4, restore `bufferWindow = 1.5` before continuing.

---

## Phase 5: Cooldown / Debounce & Threshold Filtering

Verifies the v2.3 per-intent debounce behaves identically when fed via
injection. Set `commandCooldown = 1.0`, `bufferWindow = 0` for these
rows so events are synchronous.

| #   | Setup                                | Action                                                                              | Expected                                                                                       | Result |
|-----|--------------------------------------|-------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------|--------|
| 5.1 | `commandCooldown = 1.0`              | `Inject Text` `"cease fire"` twice within 1 s                                       | First fires, second is suppressed by per-intent debounce (one event total)                     | PASS   |
| 5.2 | `commandCooldown = 1.0`              | `Inject Text` `"cease fire"`, wait >1 s in Play mode, `Inject Text` `"cease fire"` again | Both fire (cooldown expired)                                                              | PASS   |
| 5.3 | `commandCooldown = 0`, `wordConfidence = 0.2` | `Inject Text` `"cease fire"`                                              | Below `minConfidence=0.4` → silently filtered, no event                                        | PASS   |
| 5.4 | `commandCooldown = 0`, `wordConfidence = 1.0`, recogniser `minScore = 0.95` | `Inject Text` `"cease"` (partial pattern, missing "fire")                                                       | Silently filtered — no event. Parser scores the partial match at `0.25` (1 matched literal + 1 missed, normalised by pattern length 2), which is below `minScore=0.95`. `ProcessParsedResults` takes the "filtered" branch (`accepted.Count == 0`) and returns silently; the `Unrecognised` branch only fires when `results.Length == 0` from the parser. See `VoskCommandRecogniser.cs:421` vs `:391`. | PASS |

---

## Phase 6: Cross-Component Pipeline (Speech → Command)

Verifies that calling `InjectResult` on `VoskSpeechRecogniser` propagates
through the live `OnResult` subscription on `VoskCommandRecogniser`. This
is the path that catches future regressions where someone renames
`OnResult` or breaks the `OnEnable` subscription — the per-component
tests would still pass but the integration would silently break.

| #   | Setup                                                                         | Action                                                                | Expected                                                                  | Result |
|-----|-------------------------------------------------------------------------------|-----------------------------------------------------------------------|---------------------------------------------------------------------------|--------|
| 6.1 | CommandDemo scene, `useInspectorAuthoring = false`, both components wired     | Driver `Inject Result` with `"cease fire"`                            | `cease_fire` command event fires in `CommandDemo` Console output          | PASS   |
| 6.2 | Same                                                                          | Driver `Inject Result` with `"launch all missiles target hotel one"`  | `launch_weapon` event with quantity=all, weapon=missiles, target=hotel one| PASS   |
| 6.3 | Same                                                                          | Driver `Inject Result` with `"hello world"`                           | `Unrecognised: "hello world"` log line (after buffer window)              | PASS   |
| 6.4 | Disable the `VoskCommandRecogniser` **component** (uncheck its checkbox in the Inspector — not the GameObject, which would also disable the Speech Recogniser and driver), then driver `Inject Result` `"cease fire"` | Speech-layer events fire but `CommandDemo` logs nothing      | `OnDisable` correctly unsubscribed; re-enable and try again to confirm subscription path is restored | PASS   |

---

## Phase 7: Asset-Driven Authoring (v2.5 Path) via Injection

Repeats the gating cases from `v2.5-test-matrix.md` Phase 1 but driven
through injection in the Editor. Confirms ScriptableObject authoring +
text injection compose correctly. Set
`useInspectorAuthoring = true` on `CommandDemo` and import the
**Command Recognition** sample so the 6 slot assets / 11 command assets /
3 command set assets are wired on `VoskCommandRecogniser`.

| #   | Inject text                                         | Expected Intent    | Expected Slots                                | Asset feature exercised                            | Result |
|-----|-----------------------------------------------------|--------------------|-----------------------------------------------|----------------------------------------------------|--------|
| 7.1 | (Play start, no injection)                          | —                  | —                                             | Console: `Active sets: [weapons, navigation, common]` from `initialActiveSetNames`. No exceptions on `Awake()`. | PASS |
| 7.2 | `cease fire`                                        | cease_fire         | (none)                                        | Asset-driven literal command                       | PASS   |
| 7.3 | `launch missiles target hotel one`                  | launch_weapon      | weapon=missiles, target=hotel one             | Asset-driven slotted command                       | PASS   |
| 7.4 | `orient heading two seven zero`                     | set_heading        | heading=270                                   | Asset-driven NumberSequence                        | PASS   |
| 7.5 | `weapons mode` then `cease fire` then `approach target alpha one` | mode_weapons; cease_fire; (unrecognised) | —                       | Runtime set switching still works through injection (navigation set inactive after mode switch) | PASS |
| 7.6 | `all modes` then `approach target alpha one`        | mode_all; approach_target | target=alpha one                       | Restoring all sets via injection re-activates navigation | PASS |

> Note: 7.5 line 3 should produce an `Unrecognised` event because
> `approach_target` is in the navigation set, and `weapons mode` deactivated
> it. Unlike the on-Quest run, there is no acoustic-level filtering — VOSK
> isn't running — so the parser sees the full text `"approach target alpha one"`
> and reports it as unrecognised. This is expected and matches the v2.4/v2.5
> "no match in active grammar" semantics.

---

## Results Summary

| Phase | Tests | Pass | Fail | Skip |
|-------|-------|------|------|------|
| 0 — Test Runner (automated, 9 suites / 145 tests) |  9 |  9 | 0 | 0 |
| 1 — API Surface Spot-Checks             |  6 |  6 | 0 | 0 |
| 2 — Slot Conversion via Injection       |  7 |  7 | 0 | 0 |
| 3 — NumberSequence via Injection        |  4 |  4 | 0 | 0 |
| 4 — Buffered Path & Flush               |  5 |  5 | 0 | 0 |
| 5 — Cooldown & Threshold Filtering      |  4 |  4 | 0 | 0 |
| 6 — Cross-Component Pipeline            |  4 |  4 | 0 | 0 |
| 7 — Asset-Driven Authoring (v2.5 path)  |  6 |  6 | 0 | 0 |
| **Total**                               | **45** | **45** | **0** | **0** |

### What this matrix proves

- The injection API surface compiles, runs, and fires the documented
  events on a machine that has **no** Quest device, **no** Android build,
  and **no** native bridge library.
- Every v2.0–v2.5 feature category (literals, enumerated slots, aliases,
  optional slots, NumberSequence, buffer, sequential extraction,
  debounce, threshold filtering, command sets, asset authoring) is
  reachable via injection, so command-iteration changes can be validated
  without a deploy cycle.
- The code-based and asset-based configuration paths both work under
  injection, so v2.5 Inspector authoring users get the same v3 benefit.
- The cross-component subscription path (`VoskSpeechRecogniser.OnResult` →
  `VoskCommandRecogniser.HandleResult`) is exercised end-to-end. Future
  refactors that break this connection will be caught by Phase 6 even if
  the per-component injection tests still pass.

### What this matrix does NOT cover

- **Live audio input in the Editor.** That is v3 Phase 2 (desktop VOSK +
  Unity Microphone), tracked in `v3-and-beyond-analysis.md`. When that
  ships it gets its own matrix.
- **Acoustic-model behaviour.** Injection bypasses VOSK entirely, so any
  test that depends on what VOSK actually hears (e.g. the
  `switch to weapons` → `switch two weapons` known issue from v2.5
  Phase 4.5) is out of scope. Those still need a Quest run.
- **Audio capture timing / grammar reload latency.** The 50 ms gap noted
  in v2.5 Phase 5.4 is an audio-capture restart artefact and only
  reproduces on-device.

If you're iterating on **command definitions, slot combinations,
patterns, scoring, thresholds, debounce, command sets, or asset
authoring**, this matrix is sufficient before tagging a release. If
you're iterating on **what VOSK actually hears**, you still need to
deploy.
