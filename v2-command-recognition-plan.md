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

# v2.0 — Core Command Parsing

The minimum to go from VOSK text to structured commands for finite-vocabulary commands.

## v2.0 Scope

**Includes:**
- Data model: `VoskSlotDefinition` (Enumerated only), `VoskCommandDefinition`, `VoskCommand`, `VoskSlotMatch`, `VoskCommandResult`
- Shared slot registry (slots defined once, referenced by name across commands)
- Per-pattern slot optionality (`{slot}` = required, `{?slot}` = optional)
- Greedy left-to-right pattern matching (binary pass/fail — reliable because grammar constrains VOSK output)
- Grammar generation + native bridge `vosk_bridge_set_grammar`
- `VoskCommandRecogniser` component with free-speech mode toggle
- `[unk]` skip logic
- Parser unit tests + sample

**Commands this unlocks:**
- Launch/fire weapons at targets (finite weapon types, named target designations)
- State commands (cease fire, resume fire, disengage, reengage)
- Named-range distance commands (CQB, torpedo range, safe range)
- Approach/retreat commands (close on target, fall back from target)

**Cannot handle yet (deferred):**
- "Launch a jackal" (article "a") — workaround: extra pattern
- "Orient to heading two seven zero" (numeric sequences) — no workaround
- False starts / hesitations — fails silently
- Plural normalization ("jackals" → "jackal") — workaround: list both values

## v2.0 Data Model

All types in `VoskXR.Commands` namespace, following existing SDK patterns (readonly structs, arrays).

### `VoskSlotDefinition` — defines a named slot with allowed values
- `string Name` — e.g. "weapon"
- `string[] Values` — e.g. ["missiles", "torpedoes", "jackal"] (lowercase, as VOSK outputs)

Slot optionality is **not** on the definition — it's per-pattern via `{?slot}` syntax (see Matching Algorithm). A slot can be optional in one pattern and required in another.

### `VoskCommandDefinition` — defines one intent
- `string Intent` — e.g. "launch_weapon"
- `string[][] Patterns` — phrase templates:
  - Plain string = literal (must match)
  - `{slotName}` = required slot reference
  - `{?slotName}` = optional slot reference (command matches even if this slot has no match)

Commands do **not** own slot definitions. Slots are registered separately and resolved by name.

### `VoskCommand` — parsed result
- `string Intent`
- `VoskSlotMatch[] Slots` — array of (Name, Value) pairs
- `float Confidence` — minimum word confidence across matched tokens
- `string RawText` — original VOSK output
- `string GetSlot(string name)` — returns value or empty string
- `bool HasSlot(string name)`

### `VoskSlotMatch` — one matched slot
- `string Name`
- `string Value`

### `VoskCommandResult` — match/no-match wrapper
- `bool IsMatch`
- `VoskCommand Command` — `default(VoskCommand)` when `IsMatch` is false; always check `IsMatch` first
- `string RawText`

## v2.0 Matching Algorithm

`VoskCommandParser` — pure C# internal class, no Unity dependencies, independently testable.

**Greedy left-to-right pattern matching (binary pass/fail):**

1. For each command definition, try each pattern against the tokenized input
2. Walk tokens left-to-right against pattern elements:
   - **Literal token**: exact match, advance both cursors. Mismatch → pattern fails.
   - **`{slot}` (required)**: try longest matching slot value first (greedy multi-word). For "hotel one" (2 words), try consuming 2 tokens, check against slot values. Fall back to 1 token. No match → pattern fails.
   - **`{?slot}` (optional)**: same as required, but no match → skip pattern element, don't advance input cursor.
   - **`[unk]` token in input**: skip, don't advance pattern cursor.
3. A pattern matches if all pattern elements are satisfied (required elements matched, optional elements matched or skipped) and the pattern cursor reaches the end. **Leftover input tokens are allowed** — the input cursor does not need to reach the end.
4. Score: tokens consumed − leftover tokens. Ties broken by more literal matches, then definition order.
5. Best-scoring match wins. No match → `VoskCommandResult` with `IsMatch = false`.
6. Empty/whitespace-only input → `VoskCommandResult` with `IsMatch = false`. `VoskCommandRecogniser` does **not** fire any event (no `OnUnrecognisedSpeech` for silence).

**Pre-computation**: At construction, build a lookup per slot: first-word → list of (full value, word count), sorted by word count descending (longest match first).

**Why binary matching is fine for v2.0**: Grammar mode constrains VOSK to only output words from the command vocabulary. With exact vocabulary, exact matching is reliable. Scored matching becomes important in v2.1 when we want tolerance for edge cases grammar doesn't fully solve.

## v2.0 Free-Speech Mode

A toggle on `VoskCommandRecogniser` for on-device testing and debugging. Since the native bridge is Android-only (JNI AudioRecord, arm64 .so), all speech recognition — including free-speech testing — happens on-device, not in the Unity Editor.

```csharp
[Tooltip("Bypasses grammar constraints so VOSK recognises freely (like pre-v2.0). " +
         "Useful for on-device testing to see what VOSK actually hears before " +
         "grammar constrains it. The parser still runs against the output so you " +
         "can see what matches and what doesn't. Disable for release builds.")]
[SerializeField] bool freeSpeechMode = false;
```

**Behaviour:**
- `freeSpeechMode = false` (default): generates grammar from command vocabulary, calls `SetGrammar()`, VOSK is constrained. Production mode.
- `freeSpeechMode = true`: skips `SetGrammar()`, VOSK runs unconstrained like pre-v2.0. The parser still attempts to match the raw output against command patterns.
- When `Debug.isDebugBuild` is false and `freeSpeechMode` is true, logs a warning at startup ("Free-speech mode is active in a release build — grammar constraints are disabled"). Does **not** force it off — the dev may have a reason — but makes it visible.
- Both `OnCommandRecognised` and `OnUnrecognisedSpeech` fire normally in either mode.
- `OnFinalResult` / `OnPartialResult` on the underlying `VoskSpeechRecogniser` continue to fire in both modes, so the developer can see the raw VOSK output via adb logcat while testing commands.

**Why this matters:**
- Lets devs deploy to Quest, say commands, and see what VOSK actually outputs without grammar ("did VOSK hear 'jackal' or 'jacket'?")
- Lets devs test whether their patterns cover real speech variations before grammar locks in the vocabulary
- Lets devs compare recognition accuracy with and without grammar by toggling between dev builds

## v2.0 Grammar Generation

`VoskCommandParser.GenerateGrammarJson()`:

1. Extract all unique words from pattern literals
2. Extract all unique words from enumerated slot values (split multi-word values into individual words)
3. Add `[unk]` for off-vocabulary fallback
4. Deduplicate and produce JSON array: `["launch", "fire", "all", "missiles", ...]`

## v2.0 Native Bridge Changes

### `vosk_bridge.h` — add declaration
```c
VOSK_BRIDGE_EXPORT int vosk_bridge_set_grammar(const char* grammar_json);
```

### `vosk_bridge.cpp` — add implementation
- Add `static std::string g_grammar_json;`
- Add `static int g_max_alternatives = 0;` — store the value passed to `vosk_bridge_init` so it can be restored when grammar is cleared
- `vosk_bridge_set_grammar`:
  - Requires initialised + not running. Returns `VOSK_BRIDGE_ERR_NOT_INITIALISED` or `VOSK_BRIDGE_ERR_ALREADY_RUNNING` if preconditions are violated (never crashes on wrong call order).
  - Frees existing recognizer.
  - If grammar is null/empty: creates recognizer with `vosk_recognizer_new()` and re-applies `g_max_alternatives` if > 0.
  - If grammar is non-empty: creates recognizer with `vosk_recognizer_new_grm()`. Skips `set_max_alternatives` (VOSK grammar + alternatives produces unreliable results).
  - Always re-enables `vosk_recognizer_set_words(1)`.
- Store `max_alternatives` in `g_max_alternatives` during `vosk_bridge_init` (line ~176) so it survives recognizer recreation.

### `BridgeNative.cs` — add P/Invoke
```csharp
[DllImport(LibraryName)] [Preserve]
internal static extern int vosk_bridge_set_grammar(string grammarJson);
```

### `VoskSpeechRecogniser.cs` — add public method
```csharp
public void SetGrammar(string grammarJson)
```
Calls `BridgeNative.vosk_bridge_set_grammar`. Must be called after `InitialiseAsync()` completes, before `StartRecognition()`.

## v2.0 `VoskCommandRecogniser` Component

```csharp
[AddComponentMenu("VOSK XR/Command Recogniser")]
public class VoskCommandRecogniser : MonoBehaviour
{
    [SerializeField] VoskSpeechRecogniser speechRecogniser;

    [Tooltip("Bypasses grammar constraints so VOSK recognises freely (like pre-v2.0). " +
             "For on-device testing. Disable for release builds.")]
    [SerializeField] bool freeSpeechMode = false;

    public event Action<VoskCommand> OnCommandRecognised;
    public event Action<string> OnUnrecognisedSpeech;
}
```

- `Configure(VoskSlotDefinition[] slots, VoskCommandDefinition[] commands)` — builds parser, validates slot references, stores grammar JSON. **If `speechRecogniser.IsModelReady` is already true**, calls `SetGrammar()` immediately (handles the case where the speech recogniser was initialised before the command recogniser).
- On `OnModelReady`: calls `SetGrammar()` unless `freeSpeechMode` is active, then starts recognition. **Skipped if grammar was already set in `Configure()`** (avoids double-set).
- On `OnResult`: runs parser against final results only (not partial results). Fires `OnCommandRecognised` or `OnUnrecognisedSpeech`. **Does not fire any event for empty/whitespace-only input** (silence).

## v2.0 Developer-Facing API

```csharp
// Define shared slots (no IsOptional here — optionality is per-pattern)
var targets = new VoskSlotDefinition("target",
    new[] { "hotel one", "hotel two", "alpha one", "alpha three", "bravo two" });

var weapons = new VoskSlotDefinition("weapon",
    new[] { "missiles", "torpedoes", "jackal", "jackals" });

var quantity = new VoskSlotDefinition("quantity",
    new[] { "all", "one", "two", "three" });

var namedRange = new VoskSlotDefinition("range",
    new[] { "cqb", "safe range", "torpedo range", "pdc range", "railgun range" });

// Define commands — {slot} = required, {?slot} = optional
var commands = new[] {
    new VoskCommandDefinition("launch_weapon",
        patterns: new[] {
            // quantity is optional here, target is required
            new[] { "launch", "{?quantity}", "{weapon}", "target", "{target}" },
            new[] { "launch", "a", "{weapon}", "target", "{target}" },
            new[] { "fire", "{?quantity}", "{weapon}", "at", "{target}" },
            new[] { "shoot", "{weapon}" },  // no target at all in this pattern
        }
    ),
    new VoskCommandDefinition("cease_fire",
        patterns: new[] {
            new[] { "cease", "fire" },
            new[] { "stop", "firing" },
            new[] { "disengage" },
        }
    ),
    new VoskCommandDefinition("resume_fire",
        patterns: new[] {
            new[] { "resume", "fire" },
            new[] { "resume", "firing" },
            new[] { "reengage" },
        }
    ),
    new VoskCommandDefinition("set_distance_named",
        patterns: new[] {
            new[] { "close", "distance", "{range}", "target", "{target}" },
            new[] { "set", "distance", "{range}", "target", "{target}" },
            new[] { "make", "distance", "{range}", "target", "{target}" },
            new[] { "open", "distance", "{range}", "target", "{target}" },
        }
    ),
    new VoskCommandDefinition("approach_target",
        patterns: new[] {
            new[] { "close", "on", "target", "{target}" },
            new[] { "close", "in", "on", "target", "{target}" },
            new[] { "approach", "target", "{target}" },
        }
    ),
    new VoskCommandDefinition("retreat_from_target",
        patterns: new[] {
            new[] { "fall", "back", "from", "target", "{target}" },
            new[] { "pull", "back", "from", "target", "{target}" },
            new[] { "get", "away", "from", "target", "{target}" },
            new[] { "move", "away", "from", "target", "{target}" },
            new[] { "open", "distance", "from", "target", "{target}" },
        }
    ),
};

// Configure
commandRecogniser.Configure(
    slots: new[] { targets, weapons, quantity, namedRange },
    commands: commands
);

commandRecogniser.OnCommandRecognised += cmd => {
    switch (cmd.Intent)
    {
        case Intents.LaunchWeapon:
            LaunchWeapon(cmd.GetSlot("weapon"), cmd.GetSlot("quantity"), cmd.GetSlot("target"));
            break;
        case Intents.CeaseFire:
            CeaseFire();
            break;
    }
};

// Recommended: define intent names as constants to avoid typos
static class Intents
{
    public const string LaunchWeapon = "launch_weapon";
    public const string CeaseFire = "cease_fire";
    public const string ResumeFire = "resume_fire";
    // ...
}
```

## v2.0 New Files

| File | Purpose |
|------|---------|
| `Runtime/Commands/VoskSlotDefinition.cs` | Slot definition readonly struct |
| `Runtime/Commands/VoskCommandDefinition.cs` | Command definition (intent + patterns) |
| `Runtime/Commands/VoskCommand.cs` | VoskCommand + VoskSlotMatch + VoskCommandResult |
| `Runtime/Commands/VoskCommandParser.cs` | Core parser: binary matching, grammar generation |
| `Runtime/Commands/VoskCommandRecogniser.cs` | MonoBehaviour integration + free-speech toggle |
| `Tests/Runtime/VoskCommandParserTests.cs` | Parser unit tests (Runtime so they can run on-device too) |
| `Samples~/CommandRecognition/CommandDemo.cs` | Usage example |

## v2.0 Modified Files

| File | Change |
|------|--------|
| `NativeBridge~/src/vosk_bridge.h` | Add `vosk_bridge_set_grammar` declaration |
| `NativeBridge~/src/vosk_bridge.cpp` | Add `vosk_bridge_set_grammar` implementation + `g_grammar_json` + `g_max_alternatives` statics |
| `Runtime/Native/BridgeNative.cs` | Add `vosk_bridge_set_grammar` P/Invoke |
| `Runtime/VoskSpeechRecogniser.cs` | Add `SetGrammar()` public method |

## v2.0 Implementation Order

**Track A — Pure C# parser** (no native dependency, testable immediately):
1. Data types: `VoskSlotDefinition`, `VoskCommandDefinition`, `VoskCommand`, `VoskSlotMatch`, `VoskCommandResult`
2. `VoskCommandParser` — binary matching algorithm + grammar generation
3. `VoskCommandParserTests` — all parser tests

**Track B — Native bridge grammar** (parallel with A):
1. `vosk_bridge.h` + `vosk_bridge.cpp` — add `vosk_bridge_set_grammar` + `g_max_alternatives`
2. `BridgeNative.cs` — add P/Invoke
3. `VoskSpeechRecogniser.cs` — add `SetGrammar()`
4. Rebuild native library

**Track C — Integration** (depends on A + B):
1. `VoskCommandRecogniser` component (with free-speech toggle + already-ready handling)
2. `CommandDemo` sample
3. Integration test on device

## v2.0 Test Plan

**Parser unit tests** (NUnit, `Tests/Runtime`):
- Exact match: "launch all missiles target hotel one" → intent=launch_weapon, all slots filled
- Per-pattern optionality: "launch missiles target hotel one" → weapon + target filled, quantity absent (optional via `{?quantity}`)
- Same slot required in different pattern: a pattern using `{quantity}` (required) fails when quantity word is missing
- Synonym patterns: "fire all missiles at hotel one" → same result
- Multi-word slot: "hotel one" extracted as single value, not "hotel"
- No match: "hello world" → `IsMatch = false`
- Empty input → `IsMatch = false`, no events fired
- Command with no slots: "cease fire" → intent=cease_fire
- `[unk]` tokens skipped: "launch [unk] missiles target hotel one" → matches
- Ambiguous input: higher-scoring command wins
- Grammar JSON: all words present, includes `[unk]`, valid JSON string array, no duplicates
- Grammar JSON: empty command set → only `[unk]`
- Confidence propagation: `VoskCommand.Confidence` = min of matched word confidences
- Pattern referencing undefined slot → throws at construction
- `VoskCommandResult.Command` is `default` when `IsMatch` is false

**Device verification:**
- Speak commands with grammar active, verify correct command events
- Toggle free-speech mode, speak same commands, compare raw VOSK output vs grammar-constrained output
- Speak off-grammar words, verify `OnUnrecognisedSpeech` fires
- Silence → no events fired

---

# v2.1 — Robustness

Handles real players speaking naturally — hesitations, false starts, background noise, articles, plurals.

## v2.1 Scope

**Includes:**
- Scored matching (replaces binary pass/fail)
- Sliding start position (false starts and preamble)
- `minConfidence` / `minScore` thresholds on `VoskCommandRecogniser`
- Optional literal tokens (`?a`, `?to`, `?the` in patterns)
- Slot value aliases (`"jackals" → "jackal"`, `"a" → "one"`)
- Definition-time validation (warns on uppercase/punctuation, warns on single-char values)

**Commands this additionally handles:**
- "Launch a jackal target hotel one" — `?a` consumed as optional literal
- "Uh... launch all missiles target hotel one" — sliding start skips preamble
- "Launch launch all missiles target hotel one" — false start recovery
- Background noise producing "fire fire target target" — rejected by confidence threshold
- "Launch jackals" — alias resolves to "jackal"

**Backwards compatible**: existing v2.0 command definitions work unchanged. `{?slot}` syntax is unaffected; `?word` is a new addition for optional literals.

## v2.1 Changes to Matching Algorithm

### Scored matching replaces binary pass/fail

| Element | Token matches | Score |
|---------|--------------|-------|
| Literal | Exact match | +1.0 |
| Literal | Mismatch | -0.5 |
| `{slot}` (required) | Slot value found | +1.0 |
| `{slot}` (required) | No match | -1.0 (heavy penalty) |
| `{?slot}` (optional) | Slot value found | +1.0 |
| `{?slot}` (optional) | No match | 0.0 (skip) |
| `?literal` (optional) | Present | +0.5 |
| `?literal` (optional) | Absent | 0.0 |
| `[unk]` token | (any) | 0.0 (skipped) |

Final score = raw score / pattern length → normalized 0.0–1.0.

### Sliding start position

Try matching from every token position, not just position 0. Best-scoring (start, command, pattern) triple wins, provided it exceeds `minScore` threshold.

### New fields on `VoskCommandRecogniser`

```csharp
[Tooltip("Reject commands where the minimum word confidence is below this threshold. " +
         "Prevents phantom commands from background noise.")]
[SerializeField] float minConfidence = 0.4f;

[Tooltip("Reject matches where the pattern score is below this threshold. " +
         "Prevents partial or garbled matches.")]
[SerializeField] float minScore = 0.6f;
```

### `VoskCommand` gains `Score` field

- `float Score` — match quality (0.0–1.0), useful for developer debugging

## v2.1 Changes to Data Model

### `VoskSlotDefinition` gains aliases

```csharp
var weapons = new VoskSlotDefinition("weapon",
    new[] { "missiles", "torpedoes", "jackal" },
    aliases: new Dictionary<string, string> {
        { "jackals", "jackal" },
    });

var quantity = new VoskSlotDefinition("quantity",
    new[] { "all", "one", "two", "three" },
    aliases: new Dictionary<string, string> { { "a", "one" } });
```

- `Dictionary<string, string> Aliases` — maps variant → canonical value. Copied at construction time to preserve immutability of the readonly struct.
- Alias words are included in grammar generation
- `VoskSlotMatch.Value` contains the canonical value after resolution

### Pattern syntax gains optional literals

```csharp
// ?word = optional literal, consumed if present, skipped if absent
// distinct from {?slot} which is an optional slot reference
new[] { "launch", "?a", "{?quantity}", "{weapon}", "target", "{target}" }
new[] { "orient", "?to", "heading", "{heading}" }
```

### Definition-time validation

At `Configure()` time:
- Slot values with uppercase characters → `Debug.LogWarning`
- Slot values with punctuation → `Debug.LogWarning`
- Pattern referencing undefined slot name → throws `ArgumentException`
- Single-character slot values → `Debug.LogWarning` (likely an article, suggest alias instead)

### `GetSlot()` debug warning

In debug builds (`Debug.isDebugBuild`), `GetSlot()` called with a name that doesn't match any registered slot logs `Debug.LogWarning`. Zero cost in release builds.

## v2.1 Grammar Generation Changes

Additionally extract words from:
- Alias keys (split multi-word aliases into individual words)
- Optional literal tokens (strip `?` prefix)

## v2.1 Modified Files

| File | Change |
|------|--------|
| `Runtime/Commands/VoskSlotDefinition.cs` | Add `Aliases` dictionary |
| `Runtime/Commands/VoskCommandParser.cs` | Scored matching, sliding start, optional literals, alias resolution, validation |
| `Runtime/Commands/VoskCommand.cs` | Add `Score` field, `GetSlot` debug warning |
| `Runtime/Commands/VoskCommandRecogniser.cs` | Add `minConfidence`, `minScore` fields + threshold filtering |

## v2.1 Test Plan

**Scored matching:**
- Partial match below threshold → `IsMatch = false`
- Partial match above threshold → `IsMatch = true` with lower Score
- Better-scoring command wins over worse-scoring
- Score normalization: short and long patterns produce comparable scores

**Optional literals:**
- "launch a jackal" matches pattern with `?a` present
- "launch jackal" matches same pattern with `?a` absent

**Sliding start position:**
- "uh launch all missiles target hotel one" → matches starting at token 1
- "launch launch all missiles target hotel one" → matches best from later start

**Aliases:**
- "jackals" → canonical "jackal" in result
- "a" in quantity slot context → canonical "one"
- Alias words appear in generated grammar JSON

**Definition-time validation:**
- Uppercase slot value → warning logged
- Slot value with punctuation → warning logged
- Undefined slot reference → exception thrown
- Single-character slot value → warning logged

**Confidence/score thresholds:**
- Commands below minConfidence → rejected
- Commands below minScore → rejected

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

# Version Summary

| Version | Theme | Key Additions | Commands Unlocked |
|---------|-------|---------------|-------------------|
| **v2.0** | Foundation | Pattern matching, grammar, shared slots, free-speech toggle | Weapons, targets, named ranges, state commands |
| **v2.1** | Robustness | Scored matching, sliding start, aliases, optional literals, thresholds | Same commands, reliable with real speech |
| **v2.2** | Numeric | NumberSequence slots, VoskNumberParser | Headings, numeric distances, coordinates |

Native bridge change (`vosk_bridge_set_grammar`) ships in v2.0. Everything in v2.1 and v2.2 is pure C# changes on top.

---

# Known Limitations & Notes

## Leftover tokens in v2.0 (free-speech mode)

In v2.0, the parser does not require full input consumption. With grammar active this is fine (VOSK only outputs command vocabulary). In **free-speech mode**, input like "launch all missiles target hotel one please thank you" will match `launch_weapon` despite the trailing words "please thank you". This is a known v2.0 limitation — v2.1's scored matching penalizes leftover tokens and the `minScore` threshold can reject low-quality matches.

## Mid-utterance pauses

VOSK's endpointer splits utterances at silence boundaries. If a player says "launch all missiles" (long pause) "target hotel one", VOSK emits two separate final results: `"launch all missiles"` and `"target hotel one"`. Neither matches the full command pattern. The command is lost.

**Mitigation**: Players should speak compound commands without long pauses. This is a VOSK endpointer behavior, not a parser limitation. Future work could add a temporal buffer that concatenates consecutive utterances within N seconds, but this is not planned for v2.0–v2.2.

## Short utterances and phantom commands

With grammar active, very short sounds (coughs, "uh") may be mapped to the acoustically closest grammar word (e.g., a cough → "fire"). In v2.0, there is no confidence-based filtering — the parser will match if the word fits a pattern. **v2.1 adds `minConfidence` to filter these.** For v2.0, single-word commands like "disengage" are more susceptible than multi-word commands.

## Multiple commands in one breath

"Cease fire launch all missiles target hotel one" spoken without pause arrives as a single VOSK result. The parser matches **one** command per result (the best-scoring match). The second command is lost. This is a known limitation across all versions. If needed, a future extension could try matching from the token after the first match ends.

## Grammar is a flat word list, not phrase constraints

VOSK grammar mode accepts a JSON array of valid words. It constrains which words VOSK can output, but **not** their order. VOSK can produce `"missiles launch target fire"` — any permutation of grammar words. The parser's pattern-order matching prevents this from producing a valid command, but developers should understand that grammar does not guarantee valid command structure.

## Runtime command updates

`Configure()` regenerates grammar and (if not in free-speech mode) calls `SetGrammar()`, which requires stopping and restarting recognition. This creates a brief recognition gap. For dynamic vocabularies that change mid-mission (e.g., new targets appearing), the developer must stop recognition, reconfigure, and restart. The gap is typically < 100ms (recognizer recreation is fast; model stays loaded).

## `vosk_bridge_set_grammar` thread safety

The native function returns `VOSK_BRIDGE_ERR_ALREADY_RUNNING` if called while recognition is active. It must be called after `vosk_bridge_stop()` returns (which joins the recognition thread). The safe C# sequence is: `StopRecognition()` → `SetGrammar()` → `StartRecognition()`. Calling `SetGrammar()` while running returns an error but does not crash or corrupt state.
