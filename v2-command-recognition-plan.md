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

# v2.2 — Numeric Commands

Unlocks heading, distance, and coordinate commands that use variable-length digit sequences.

## v2.2 Scope

**Includes:**
- `VoskSlotType` enum: `Enumerated`, `NumberSequence`
- `VoskSlotDefinition.NumberSequence()` static factory
- `VoskNumberParser` — digit sequence + cardinal number word conversion
- Digit vocabulary auto-added to grammar when NumberSequence slots present

**Commands this additionally handles:**
- "Orient to heading two seven zero mark plus one five"
- "Orient to heading two seven zero"
- "Close distance ten klicks target hotel one"
- "Set distance fifteen klicks target bravo two"
- Any numeric quantity without enumerating every possible value

## v2.2 Changes to Data Model

### `VoskSlotType` enum (new file)
```csharp
public enum VoskSlotType { Enumerated, NumberSequence }
```

### `VoskSlotDefinition` gains NumberSequence mode

```csharp
// Static factory — no value enumeration needed
var heading = VoskSlotDefinition.NumberSequence("heading", minWords: 1, maxWords: 3);
var elevation = VoskSlotDefinition.NumberSequence("elevation", minWords: 1, maxWords: 2);
var distance = VoskSlotDefinition.NumberSequence("distance", minWords: 1, maxWords: 3);
```

- `int MinWords`, `int MaxWords` — bounds on digit word consumption
- Matches consecutive tokens from digit word vocabulary: "zero"–"nine", "ten"–"nineteen", "twenty"–"ninety", "hundred", "thousand" (~30 static entries)
- Returns raw word sequence as slot value string (e.g., "two seven zero")

### `VoskNumberParser` — static utility (new file)

Pure C# utility, ~60 lines:
- `int ParseDigitSequence(string words)` — "two seven zero" → 270 (each word = one digit, concatenated)
- `int ParseCardinal(string words)` — "fifteen" → 15, "two hundred" → 200

Static `Dictionary<string, int>` mapping ~30 entries.

## v2.2 Changes to Matching Algorithm

When the pattern cursor hits a `NumberSequence` slot:
1. Greedily consume consecutive tokens that are in the digit word HashSet
2. Stop when a non-digit word is encountered or `maxWords` is reached
3. If consumed count < `minWords`, slot fails to match (required/optional per `{slot}` vs `{?slot}` syntax)
4. Return the consumed words joined by space as the slot value

## v2.2 Grammar Generation Changes

When any `NumberSequence` slot exists, add the ~30 digit vocabulary words to the grammar. This is a fixed set regardless of how many NumberSequence slots are defined.

## v2.2 New Files

| File | Purpose |
|------|---------|
| `Runtime/Commands/VoskSlotType.cs` | Slot type enum |
| `Runtime/Commands/VoskNumberParser.cs` | Digit sequence + cardinal number word conversion |
| `Tests/Runtime/VoskNumberParserTests.cs` | Number parser unit tests |

## v2.2 Modified Files

| File | Change |
|------|--------|
| `Runtime/Commands/VoskSlotDefinition.cs` | Add `NumberSequence` factory, `MinWords`/`MaxWords`, `Type` field |
| `Runtime/Commands/VoskCommandParser.cs` | NumberSequence matching logic, digit vocab in grammar |

## v2.2 Test Plan

**NumberSequence slots:**
- "two seven zero" matches heading slot (3 digit words) → value "two seven zero"
- "one five" matches elevation slot (2 digit words) → value "one five"
- "fifteen" matches distance slot (1 word) → value "fifteen"
- Stops at non-digit word: "two seven zero mark" → heading consumes 3, stops before "mark"
- Respects maxWords: slot with maxWords=2 stops after 2 even if more digits follow
- Below minWords: slot with minWords=2, input has 1 digit word → no match

**Number parser:**
- "two seven zero" → 270 (digit sequence)
- "one five" → 15 (digit sequence)
- "zero" → 0
- "nine" → 9
- "fifteen" → 15 (cardinal)
- "twenty" → 20 (cardinal)
- "two hundred" → 200 (cardinal)
- Empty string → 0

**Grammar generation:**
- Digit vocabulary words included when at least one NumberSequence slot exists
- Not included when no NumberSequence slots exist

---

# v2.3 — Continuity (Shipped)

Solves the two biggest real-world command failures: mid-utterance pauses splitting a command across two VOSK results, and multiple commands spoken in a single breath where only the first is recognised.

## v2.3 Scope

**Includes:**
- Utterance buffer that concatenates consecutive VOSK results within a time window before parsing
- Sequential command extraction — after the best match, try matching again from the next token
- Per-intent debounce to suppress duplicate firings from rapid repeated results

**Problems this solves:**
- "Launch all missiles" [pause] "target hotel one" — two VOSK results, neither matches alone. Buffer concatenates them → single parse succeeds.
- "Cease fire launch all missiles target hotel one" — single VOSK result. Sequential extraction finds `cease_fire` at tokens 0–1, then `launch_weapon` at tokens 2+.
- Player says "fire" and VOSK emits two rapid final results → debounce suppresses the duplicate.

## v2.3 Utterance Buffer

`VoskUtteranceBuffer` — pure C# class, sits between `VoskCommandRecogniser` and the parser. Not a MonoBehaviour — owned and ticked by `VoskCommandRecogniser`.

### Behaviour

1. When a final result arrives from VOSK, append its tokens to the buffer. Record the arrival timestamp.
2. Start (or reset) a flush timer: `bufferWindow` seconds (default 1.5s, configurable via `[SerializeField]`).
3. If another final result arrives before the timer expires, append its tokens and reset the timer.
4. When the timer expires (no new results within the window), flush: concatenate all buffered tokens into a single string and pass to the parser.
5. Per-word confidence: buffer also accumulates the per-word confidence arrays. When flushing, the concatenated confidence array is passed through so `VoskCommand.Confidence` (min across matched tokens) remains accurate.

### Why a time window, not immediate parse

If we parse immediately on each result, we're back to the current behaviour — the first half of a split command fails and is discarded before the second half arrives. The window gives the player time to finish the full command across a natural pause. 1.5s is long enough for a mid-sentence breath but short enough to feel responsive.

### Tradeoff: latency

The buffer adds up to `bufferWindow` latency to every command. In the worst case (single complete command, no pause), the player waits 1.5s after finishing speech before the command fires. This is acceptable for a military sim (commands are deliberate, not twitch), but the field is tunable per game.

### Edge case: rapid distinct commands

Player says "cease fire" [0.5s pause] "launch missiles target hotel one". Both arrive within the buffer window and get concatenated: "cease fire launch missiles target hotel one". Sequential command extraction (below) handles this — it finds both commands in the merged string.

### Fields on `VoskCommandRecogniser`

```csharp
[Tooltip("Time in seconds to wait for additional speech before parsing. " +
         "Longer values recover split commands but add latency.")]
[SerializeField] float bufferWindow = 1.5f;
```

### Implementation notes

- Buffer is flushed in `Update()` by checking `Time.time - lastResultTime >= bufferWindow` when the buffer is non-empty.
- `VoskCommandRecogniser` already subscribes to `OnResult`. The buffer sits in the handler before the parse call.
- If `bufferWindow` is set to 0, buffering is disabled — results are parsed immediately as in v2.2 (backwards compatible).

## v2.3 Sequential Command Extraction

Changes `VoskCommandParser.Parse()` return type from `VoskCommandResult` to `VoskCommandResult[]`.

### Algorithm

1. Run the existing scored matching + sliding start algorithm. Find the best match.
2. If a match is found, record it. Identify the token span it consumed (start position through last consumed token).
3. Take the remaining tokens *after* the consumed span. If non-empty, run the parser again on the remainder.
4. Repeat until no more matches are found or tokens are exhausted.
5. Return all matches as an array, ordered by their position in the input.

### Why ordered by position, not score

The player spoke commands in a specific order: "cease fire, launch missiles". The game should process them in that order. Reordering by score would produce unpredictable command sequences.

### Event changes

`OnCommandRecognised` fires once per extracted command, in order. Existing subscribers that handle one command at a time work unchanged. New `OnCommandsRecognised` event (plural) fires once with the full `VoskCommand[]` array for subscribers that want batch processing.

```csharp
public event Action<VoskCommand> OnCommandRecognised;       // fires per command, in order
public event Action<VoskCommand[]> OnCommandsRecognised;    // fires once with all commands
```

### Edge case: overlapping matches

Two commands share vocabulary (e.g., "fire" appears in both `launch_weapon` and `cease_fire`). The first match consumes its tokens; the remainder is what's left. If the remainder doesn't form a valid second command, it's reported via `OnUnrecognisedSpeech`. No backtracking — greedy left-to-right extraction.

## v2.3 Per-Intent Debounce

Simple per-intent cooldown timer on `VoskCommandRecogniser`.

```csharp
[Tooltip("Minimum seconds between firing the same intent. " +
         "Prevents duplicate commands from rapid VOSK results.")]
[SerializeField] float commandCooldown = 0.3f;
```

- Tracks `Dictionary<string, float>` of intent → last fire time.
- Before firing `OnCommandRecognised`, checks if `Time.time - lastFireTime[intent] >= commandCooldown`. If not, the command is suppressed.
- Cooldown of 0 disables debounce.
- Different intents have independent cooldowns — "cease fire" doesn't block "launch missiles".

## v2.3 Modified Files

| File | Change |
|------|--------|
| `Runtime/Commands/VoskCommandParser.cs` | `Parse()` returns `VoskCommandResult[]`, sequential extraction loop |
| `Runtime/Commands/VoskCommandRecogniser.cs` | Utterance buffer, `bufferWindow` field, `OnCommandsRecognised` event, debounce logic, `commandCooldown` field |
| `Runtime/Commands/VoskCommand.cs` | No changes (result type unchanged) |

## v2.3 New Files

None. All changes are to existing files.

## v2.3 Test Plan

**Utterance buffer:**
- Two results arriving within window → concatenated and parsed as one
- Single result, timer expires → parsed normally
- Three rapid results → all concatenated
- `bufferWindow = 0` → immediate parse (v2.2 behaviour)
- Per-word confidence arrays correctly concatenated across buffered results

**Sequential command extraction:**
- "cease fire launch all missiles target hotel one" → two commands: `cease_fire`, `launch_weapon`
- "launch missiles target hotel one" → one command (no remainder after extraction)
- "cease fire resume fire" → two commands in order
- "hello world cease fire" → sliding start finds `cease_fire`, no second command, "hello world" is noise
- Overlapping vocabulary: first match wins its span, remainder parsed independently

**Debounce:**
- Same intent fired twice within cooldown → second suppressed
- Same intent fired after cooldown expires → both fire
- Different intents within cooldown → both fire (independent cooldowns)
- `commandCooldown = 0` → no suppression

**Integration:**
- Buffer + sequential extraction: "cease fire" [pause] "launch missiles target hotel one" → buffer merges, sequential extraction finds both
- Buffer + debounce: VOSK emits "fire" twice rapidly → buffer merges to "fire fire", parser matches one `launch_weapon`, debounce irrelevant (only one match). Alternatively if both parse separately, debounce catches the duplicate.

---

# v2.4 — Command Sets

Lets the game activate different command groups for different game states. Reduces grammar size per mode for better VOSK accuracy. Adds push-to-talk for scenarios where always-on recognition is undesirable.

## v2.4 Scope

**Includes:**
- `VoskCommandSet` — named group of command definitions
- Runtime switching between active command sets (regenerates grammar)
- Additive sets — multiple sets active simultaneously (e.g., "common" + "weapons")
- Push-to-talk mode — recognition starts/stops on input action

**Problems this solves:**
- Grammar contains all command words across all game modes → VOSK confuses acoustically similar words from unrelated modes. Smaller per-mode grammar = higher accuracy.
- Player in the navigation screen hears weapon commands recognised from ambient speech. With command sets, weapon commands aren't active during navigation.
- Some scenarios need recognition only when the player is actively commanding (push-to-talk).

## v2.4 `VoskCommandSet`

```csharp
public readonly struct VoskCommandSet
{
    public string Name { get; }
    public VoskCommandDefinition[] Commands { get; }
}
```

Lightweight grouping — does not own slots. Slots remain globally registered (a target designation like "hotel one" is shared across all modes).

### API on `VoskCommandRecogniser`

```csharp
// Register all sets and shared slots up front
void Configure(VoskSlotDefinition[] slots, VoskCommandSet[] sets);

// Activate one or more sets by name — regenerates grammar from active sets only
void SetActiveSets(params string[] setNames);

// Convenience: activate a single set
void SetActiveSet(string setName);

// Query
string[] ActiveSetNames { get; }
```

### Behaviour

- `Configure()` stores all sets but does not activate any. The developer must call `SetActiveSets()` to activate.
- `SetActiveSets()` collects command definitions from the named sets, rebuilds the parser with only those commands, regenerates grammar from only the active vocabulary + shared slot values, and calls `SetGrammar()`.
- If recognition is running, `SetActiveSets()` internally does stop → set grammar → start. The brief gap (~100ms) is acceptable for a mode switch which is an explicit player/game action.
- Calling `SetActiveSets()` with an unknown set name throws `ArgumentException`.
- An empty set name array is valid — disables all commands, grammar becomes `["[unk]"]` only.

### Backwards compatibility

The existing `Configure(slots, commands)` overload (no sets) continues to work. Internally it creates a single unnamed set containing all commands and activates it immediately. Existing code is unaffected.

### Example

```csharp
var weaponsSet = new VoskCommandSet("weapons", new[] {
    new VoskCommandDefinition("launch_weapon", ...),
    new VoskCommandDefinition("cease_fire", ...),
});

var navigationSet = new VoskCommandSet("navigation", new[] {
    new VoskCommandDefinition("evasive_maneuvers", ...),
    new VoskCommandDefinition("set_heading", ...),
});

var commonSet = new VoskCommandSet("common", new[] {
    new VoskCommandDefinition("status_report", ...),
});

commandRecogniser.Configure(slots, new[] { weaponsSet, navigationSet, commonSet });

// Player enters weapons station
commandRecogniser.SetActiveSets("weapons", "common");

// Player moves to helm
commandRecogniser.SetActiveSets("navigation", "common");
```

## v2.4 Push-to-Talk

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
- When `pushToTalkEnabled` is false (default): recognition runs continuously as in v2.0–v2.3.
- Push-to-talk composes with the utterance buffer: the buffer flushes either on timer expiry or on push-to-talk release (whichever comes first), ensuring commands are processed promptly when the player lets go.

### Input System dependency

Uses Unity's Input System (`UnityEngine.InputSystem`). The `InputActionReference` field is nullable — if push-to-talk is enabled but no action is assigned, a warning is logged and push-to-talk is disabled at runtime.

Assembly definition gains optional reference to `Unity.InputSystem`. Push-to-talk code is behind `#if ENABLE_INPUT_SYSTEM` so the package compiles without Input System installed (push-to-talk simply isn't available).

## v2.4 Modified Files

| File | Change |
|------|--------|
| `Runtime/Commands/VoskCommandRecogniser.cs` | Command sets API, `SetActiveSets()`, push-to-talk fields and logic |
| `Runtime/Commands/VoskCommandParser.cs` | Accept filtered command list at construction (already does — no parser change needed, just constructed with fewer commands) |

## v2.4 New Files

| File | Purpose |
|------|---------|
| `Runtime/Commands/VoskCommandSet.cs` | Command set readonly struct |

## v2.4 Test Plan

**Command sets:**
- Activate "weapons" set → only weapon commands match, navigation commands don't
- Activate "weapons" + "common" → both sets' commands match
- Switch from "weapons" to "navigation" → weapon commands stop matching, navigation commands start
- Activate empty set list → no commands match, `OnUnrecognisedSpeech` fires for all input
- Unknown set name → exception
- Grammar regenerated on switch: only active vocabulary words present
- `Configure(slots, commands)` (no sets) → all commands active, backwards compatible

**Push-to-talk:**
- Action pressed → recognition starts
- Action released → recognition stops after tail delay
- Speech during release tail → final result captured
- Push-to-talk disabled → continuous recognition (v2.3 behaviour)
- No action assigned + push-to-talk enabled → warning, falls back to continuous
- `#if ENABLE_INPUT_SYSTEM` absent → push-to-talk fields hidden, continuous only

---

# v2.5 — Inspector Authoring

Adds ScriptableObject-based command and slot authoring for designers who prefer the Inspector over code. Does not replace the code API — provides a parallel authoring path.

## v2.5 Scope

**Includes:**
- `VoskSlotAsset` ScriptableObject for slot definitions
- `VoskCommandAsset` ScriptableObject for command definitions with pattern editing
- `VoskCommandSetAsset` ScriptableObject for command set grouping
- Inspector-driven `Configure()` overloads on `VoskCommandRecogniser`
- Serialized asset references on `VoskCommandRecogniser` for zero-code setup

**Problems this solves:**
- Designers can't author commands without writing C# — they depend on a programmer for every vocabulary change.
- Iteration is slow: change a slot value → recompile → test. With ScriptableObjects: change a value in Inspector → enter play mode → test.
- No visual overview of all commands and their patterns.

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
| `Runtime/Commands/VoskCommandRecogniser.cs` | Asset reference fields, auto-configure from assets in `Awake()` |

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

---

# Version Summary

| Version | Theme | Key Additions | Commands Unlocked |
|---------|-------|---------------|-------------------|
| **v2.0** | Foundation | Pattern matching, grammar, shared slots, free-speech toggle | Weapons, targets, named ranges, state commands |
| **v2.1** | Robustness | Scored matching, sliding start, aliases, optional literals, thresholds | Same commands, reliable with real speech |
| **v2.2** | Numeric | NumberSequence slots, VoskNumberParser | Headings, numeric distances, coordinates |
| **v2.3** | Continuity | Utterance buffer, sequential command extraction, debounce | Split commands recovered, chained commands extracted |
| **v2.4** | Command Sets | Named command groups, runtime switching, push-to-talk | Mode-specific commands, reduced grammar per mode |
| **v2.5** | Inspector Authoring | ScriptableObject slots/commands/sets, zero-code setup | Same commands, designer-friendly authoring |

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

## Leftover tokens in v2.0 (free-speech mode)

In v2.0, the parser does not require full input consumption. With grammar active this is fine (VOSK only outputs command vocabulary). In **free-speech mode**, input like "launch all missiles target hotel one please thank you" will match `launch_weapon` despite the trailing words "please thank you". This is a known v2.0 limitation — v2.1's scored matching penalizes leftover tokens and the `minScore` threshold can reject low-quality matches.

## Mid-utterance pauses

VOSK's endpointer splits utterances at silence boundaries. If a player says "launch all missiles" (long pause) "target hotel one", VOSK emits two separate final results: `"launch all missiles"` and `"target hotel one"`. Neither matches the full command pattern. The command is lost.

**Mitigation (v2.0–v2.2)**: Players should speak compound commands without long pauses. This is a VOSK endpointer behavior, not a parser limitation.

**Fixed in v2.3**: The utterance buffer concatenates consecutive results within a configurable time window before parsing, recovering split commands.

## Short utterances and phantom commands

With grammar active, very short sounds (coughs, "uh") may be mapped to the acoustically closest grammar word (e.g., a cough → "fire"). In v2.0, there is no confidence-based filtering — the parser will match if the word fits a pattern. **v2.1 adds `minConfidence` to filter these.** For v2.0, single-word commands like "disengage" are more susceptible than multi-word commands.

## Multiple commands in one breath

"Cease fire launch all missiles target hotel one" spoken without pause arrives as a single VOSK result. The parser matches **one** command per result (the best-scoring match). The second command is lost.

**Fixed in v2.3**: Sequential command extraction matches from the token after the first match's span, extracting all commands from a single utterance.

## Grammar is a flat word list, not phrase constraints

VOSK grammar mode accepts a JSON array of valid words. It constrains which words VOSK can output, but **not** their order. VOSK can produce `"missiles launch target fire"` — any permutation of grammar words. The parser's pattern-order matching prevents this from producing a valid command, but developers should understand that grammar does not guarantee valid command structure.

## Runtime command updates

`Configure()` regenerates grammar and (if not in free-speech mode) calls `SetGrammar()`, which requires stopping and restarting recognition. This creates a brief recognition gap. For dynamic vocabularies that change mid-mission (e.g., new targets appearing), the developer must stop recognition, reconfigure, and restart. The gap is typically < 100ms (recognizer recreation is fast; model stays loaded).

## `vosk_bridge_set_grammar` thread safety

The native function returns `VOSK_BRIDGE_ERR_ALREADY_RUNNING` if called while recognition is active. It must be called after `vosk_bridge_stop()` returns (which joins the recognition thread). The safe C# sequence is: `StopRecognition()` → `SetGrammar()` → `StartRecognition()`. Calling `SetGrammar()` while running returns an error but does not crash or corrupt state.
