# Command Recognition System

## Context

Developers using this SDK get raw text from VOSK (`"launch all missiles target hotel one"`) but have no way to turn that into structured game commands. For the team's games (military/sim scenarios), commands like "launch all missiles, target Hotel 1" need to be parsed into intent + slots (weapon, quantity, target) that game code can act on. This feature adds a command parsing layer to the SDK with VOSK grammar integration for accuracy.

One-shot commands only. Team-internal SDK — practical over over-engineered.

## Architecture

```
[VoskSpeechRecogniser] ──OnResult──▶ [VoskCommandRecogniser] ──OnCommandRecognised──▶ [Game Code]
        ▲                                     │
        └── SetGrammar(json) ◀── Configure() ─┘
```

The command recogniser is a separate MonoBehaviour. It subscribes to `OnResult` (to get word confidence), parses the text against developer-defined command patterns, and emits structured `VoskCommand` events. At startup, it auto-generates a VOSK grammar from the command vocabulary and passes it to the speech recogniser for constrained recognition (unless free-speech mode is enabled for testing).

---

# v2.0 — Core Command Parsing (Shipped v0.4.0)

Pattern matching, grammar-constrained recognition, shared slots, free-speech toggle, `[unk]` skip logic, parser unit tests, and CommandDemo sample. See `CHANGELOG.md` [0.4.0] for details.

---

# v2.1 — Robustness (Shipped v0.5.1)

Scored matching, sliding start, slot aliases, optional literals, `minConfidence`/`minScore` thresholds, definition-time validation. Quest device-tested (35 tests, 33/35 pass). Bug fixes: confidence threshold bypass, single-char optional literal removed in favour of alias path, alias key validation added. See `CHANGELOG.md` [0.5.0] and [0.5.1] for details. Full test results in `v2.1-test-matrix.md`.

---

# v2.2 — Numeric Commands (Shipped v0.6.0)

`NumberSequence` slot type, `VoskNumberParser` (digit sequence + cardinal conversion), greedy digit-word consumption with `minWords`/`maxWords` bounds, auto-generated digit vocabulary in grammar. Quest device-tested (40 tests, 31/35 pass in v2.2 matrix). See `CHANGELOG.md` [0.6.0] for details. Full test results in `v2.2-test-matrix.md`.

---

# v2.3 — Continuity (Shipped v0.7.0)

Utterance buffer (`bufferWindow`) merges split VOSK results before parsing. Sequential command extraction (left-to-right greedy) finds multiple commands per utterance. Per-intent debounce (`commandCooldown`) suppresses duplicates both across results and within a single parse batch. `OnCommandsRecognised` batch event added. Quest device-tested (40 tests, 40/40 pass). See `CHANGELOG.md` [0.7.0] for details. Full test results in `v2.3-test-matrix.md`.

---

# v2.4 — Command Sets (Shipped v0.8.0)

`VoskCommandSet` named groups, `Configure(slots, sets)` overload, `SetActiveSets()`/`SetActiveSet()` runtime switching with grammar regeneration, `ActiveSetNames` query, backwards-compatible `Configure(slots, commands)`. Utterance buffer cleared on set switch. Slot vocabulary remains globally included in grammar. Quest device-tested (58 tests, 57/58 pass). See `CHANGELOG.md` [0.8.0] for details. Full test results in `v2.4-test-matrix.md`.

---

# v2.5 — Inspector Authoring

Adds ScriptableObject-based command and slot authoring for designers who prefer the Inspector over code. Does not replace the code API — provides a parallel authoring path. Also adds push-to-talk mode for scenarios where always-on recognition is undesirable.

## v2.5 Scope

**Includes:**
- `VoskSlotAsset` ScriptableObject for slot definitions
- `VoskCommandAsset` ScriptableObject for command definitions with pattern editing
- `VoskCommandSetAsset` ScriptableObject for command set grouping
- Inspector-driven `Configure()` overloads on `VoskCommandRecogniser`
- Serialized asset references on `VoskCommandRecogniser` for zero-code setup
- Push-to-talk mode — recognition starts/stops on input action

**Problems this solves:**
- Designers can't author commands without writing C# — they depend on a programmer for every vocabulary change.
- Iteration is slow: change a slot value → recompile → test. With ScriptableObjects: change a value in Inspector → enter play mode → test.
- No visual overview of all commands and their patterns.
- Some scenarios need recognition only when the player is actively commanding (push-to-talk).

## v2.5 `VoskSlotAsset`

```csharp
[CreateAssetMenu(menuName = "VOSK XR/Slot Definition")]
public class VoskSlotAsset : ScriptableObject
{
    [Tooltip("Slot name used in pattern references {slotName}")]
    public string slotName;

    public VoskSlotType slotType = VoskSlotType.Enumerated;

    [Tooltip("Allowed values (Enumerated slots only)")]
    public string[] values;

    [Tooltip("Variant → canonical mappings")]
    public AliasEntry[] aliases;

    [Header("NumberSequence Settings")]
    public int minWords = 1;
    public int maxWords = 3;

    [System.Serializable]
    public struct AliasEntry
    {
        public string variant;
        public string canonical;
    }

    // Converts to runtime struct
    public VoskSlotDefinition ToDefinition() { ... }
}
```

## v2.5 `VoskCommandAsset`

```csharp
[CreateAssetMenu(menuName = "VOSK XR/Command Definition")]
public class VoskCommandAsset : ScriptableObject
{
    public string intent;

    [Tooltip("Each element is one pattern. Tokens separated by spaces. " +
             "Use {slot} for required slots, {?slot} for optional, ?word for optional literals.")]
    public string[] patterns;

    // Converts to runtime struct (splits pattern strings into string[][])
    public VoskCommandDefinition ToDefinition() { ... }
}
```

### Pattern string format

In the Inspector, patterns are authored as single strings rather than `string[][]`:

```
"launch ?a {?quantity} {weapon} target {target}"
```

`ToDefinition()` splits on whitespace to produce the `string[]` the parser expects. This is more readable in the Inspector than a nested array.

## v2.5 `VoskCommandSetAsset`

```csharp
[CreateAssetMenu(menuName = "VOSK XR/Command Set")]
public class VoskCommandSetAsset : ScriptableObject
{
    public string setName;
    public VoskCommandAsset[] commands;

    public VoskCommandSet ToSet() { ... }
}
```

## v2.5 Push-to-Talk

A mode on `VoskCommandRecogniser` where recognition is only active while an input is held.

```csharp
[Tooltip("When enabled, recognition only runs while pushToTalkAction is held. " +
         "Eliminates phantom commands when the player is not actively commanding.")]
[SerializeField] bool pushToTalkEnabled = false;

[Tooltip("Input action that activates recognition. Typically a controller grip or trigger.")]
[SerializeField] InputActionReference pushToTalkAction;
```

### Behaviour

- When `pushToTalkEnabled` is true and the action is pressed: `StartRecognition()`.
- When the action is released: waits `pushToTalkTail` seconds (default 0.5s) for the final VOSK result to arrive, then `StopRecognition()`.
- The tail delay prevents cutting off the last word. VOSK needs a moment of silence after speech to emit the final result.
- When `pushToTalkEnabled` is false (default): recognition runs continuously as in v2.0–v2.4.
- Push-to-talk composes with the utterance buffer: the buffer flushes either on timer expiry or on push-to-talk release (whichever comes first), ensuring commands are processed promptly when the player lets go.

### Input System dependency

Uses Unity's Input System (`UnityEngine.InputSystem`). The `InputActionReference` field is nullable — if push-to-talk is enabled but no action is assigned, a warning is logged and push-to-talk is disabled at runtime.

Assembly definition gains optional reference to `Unity.InputSystem`. Push-to-talk code is behind `#if ENABLE_INPUT_SYSTEM` so the package compiles without Input System installed (push-to-talk simply isn't available).

## v2.5 Changes to `VoskCommandRecogniser`

```csharp
[Header("Inspector Authoring (optional — ignored if Configure() is called from code)")]
[SerializeField] VoskSlotAsset[] slotAssets;
[SerializeField] VoskCommandSetAsset[] commandSetAssets;
[SerializeField] string[] activeSetNames;
```

On `Awake()`, if `slotAssets` is non-empty and `Configure()` has not been called from code, auto-configure from the serialized assets. Code-based `Configure()` always takes priority.

## v2.5 New Files

| File | Purpose |
|------|---------|
| `Runtime/Commands/VoskSlotAsset.cs` | Slot ScriptableObject |
| `Runtime/Commands/VoskCommandAsset.cs` | Command ScriptableObject |
| `Runtime/Commands/VoskCommandSetAsset.cs` | Command set ScriptableObject |

## v2.5 Modified Files

| File | Change |
|------|--------|
| `Runtime/Commands/VoskCommandRecogniser.cs` | Asset reference fields, auto-configure from assets in `Awake()`, push-to-talk fields and logic |

## v2.5 Test Plan

**ScriptableObject conversion:**
- `VoskSlotAsset.ToDefinition()` produces correct `VoskSlotDefinition` for Enumerated and NumberSequence types
- `VoskCommandAsset.ToDefinition()` splits pattern strings into correct token arrays
- Alias entries convert to dictionary correctly
- Patterns with `{slot}`, `{?slot}`, `?word` tokens preserved correctly after split

**Inspector authoring:**
- Assign assets in Inspector → enter play mode → commands work without any `Configure()` call
- Code `Configure()` call → Inspector assets ignored
- Missing slot asset referenced by command pattern → validation warning at configure time

**Push-to-talk:**
- Action pressed → recognition starts
- Action released → recognition stops after tail delay
- Speech during release tail → final result captured
- Push-to-talk disabled → continuous recognition (v2.4 behaviour)
- No action assigned + push-to-talk enabled → warning, falls back to continuous
- `#if ENABLE_INPUT_SYSTEM` absent → push-to-talk fields hidden, continuous only

---

# Version Summary

| Version | Theme | Key Additions | Commands Unlocked |
|---------|-------|---------------|-------------------|
| **v2.0** | Foundation | Pattern matching, grammar, shared slots, free-speech toggle | Weapons, targets, named ranges, state commands |
| **v2.1** | Robustness | Scored matching, sliding start, aliases, optional literals, thresholds | Same commands, reliable with real speech |
| **v2.2** | Numeric | NumberSequence slots, VoskNumberParser | Headings, numeric distances, coordinates |
| **v2.3** | Continuity | Utterance buffer, sequential command extraction, debounce | Split commands recovered, chained commands extracted |
| **v2.4** | Command Sets | Named command groups, runtime switching | Mode-specific commands, reduced grammar per mode |
| **v2.5** | Inspector Authoring | ScriptableObject slots/commands/sets, zero-code setup, push-to-talk | Same commands, designer-friendly authoring, input-gated recognition |

Native bridge change (`vosk_bridge_set_grammar`) ships in v2.0. Everything in v2.1–v2.5 is pure C# changes on top.

---

# Future Ideas (Out of Scope for v2.x)

## Pattern prefix routing (wake words)

Use role-specific prefix words to route commands: "Helm, evasive maneuvers", "Weapons, launch missiles". The prefix acts as both a natural speech cue and a command disambiguator. This is a pattern design convention, not a code feature — it works today by adding the prefix as a literal in the pattern. A future version could formalise it with a `prefix` field on `VoskCommandSet` that auto-prepends to all patterns, but this needs more design thought around grammar interaction and is out of scope for v2.x.

## Partial result preview

Parse VOSK partial results to drive a "command preview" UI showing what the system thinks the player is saying before the final result commits. Adds `OnPartialCommand` event. Requires careful UX to avoid distracting flickering.

## Command confirmation callback

`OnCommandPending` fires with a pending command the game can `.Confirm()` or `.Reject()`. Enables "Did you say launch missiles? Confirm." flow for high-stakes commands. Needs design around timeout and what happens when the player doesn't confirm.

## Context-dependent slots

Slot values that change based on game state (e.g., available weapons depend on ship class). Would require a callback or data-binding mechanism for dynamic slot population, plus grammar regeneration.

## Free-text / wildcard slot type

A `VoskSlotType.FreeText` that captures remaining tokens until the next literal. Useful for open-ended commands like "log note [anything]" or "send message [anything]". Requires free-speech mode or a very large grammar.

---

# Known Limitations & Notes

## Grammar is a flat word list, not phrase constraints

VOSK grammar mode accepts a JSON array of valid words. It constrains which words VOSK can output, but **not** their order. VOSK can produce `"missiles launch target fire"` — any permutation of grammar words. The parser's pattern-order matching prevents this from producing a valid command, but developers should understand that grammar does not guarantee valid command structure.

## Runtime command updates

`Configure()` regenerates grammar and (if not in free-speech mode) calls `SetGrammar()`, which requires stopping and restarting recognition. This creates a brief recognition gap. For dynamic vocabularies that change mid-mission (e.g., new targets appearing), the developer must stop recognition, reconfigure, and restart. The gap is typically < 100ms (recognizer recreation is fast; model stays loaded).

## `vosk_bridge_set_grammar` thread safety

The native function returns `VOSK_BRIDGE_ERR_ALREADY_RUNNING` if called while recognition is active. It must be called after `vosk_bridge_stop()` returns (which joins the recognition thread). The safe C# sequence is: `StopRecognition()` → `SetGrammar()` → `StartRecognition()`. Calling `SetGrammar()` while running returns an error but does not crash or corrupt state.
