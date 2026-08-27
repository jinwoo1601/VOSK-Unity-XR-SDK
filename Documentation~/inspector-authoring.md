# Inspector Authoring

The SDK supports zero-code command setup using ScriptableObject assets. Instead of writing `Configure()` calls, you create slot, command, and command set assets in the Unity Inspector, then drag them onto your `VoxrCommandRecogniser` component.

---

## Overview

Inspector authoring provides:

- **Visual editing** of slot values, aliases, patterns, and command sets directly in the Unity Inspector
- **No code required** for basic setups -- ideal for designers or rapid prototyping
- **Reusable assets** that can be shared across scenes and prefabs
- **Version-control-friendly** ScriptableObjects serialised as `.asset` files

At runtime, the assets are automatically converted to the same `VoxrSlotDefinition`, `VoxrCommandDefinition`, and `VoxrCommandSet` structs that the code-based API uses. There is no performance difference.

---

## Step-by-Step Setup

### 1. Create Slot Assets

Right-click in the Project window and select **Assets > Create > VoXR > Slot Definition**.

Configure each slot asset in the Inspector:
- **Slot Name** -- the name referenced in patterns (e.g. `target`, `weapon`, `heading`)
- **Slot Type** -- `Enumerated` for fixed values, `NumberSequence` for digit words
- **Values** (Enumerated only) -- the allowed slot values (e.g. `alpha one`, `bravo two`, `hotel one`)
- **Aliases** -- variant-to-canonical mappings (e.g. `jackals` -> `jackal`)
- **Min/Max Words** (NumberSequence only) -- range of digit words to consume. **Min Words** must be at least 1 and **Max Words** at least Min Words. Neither field is range-limited in the Inspector, so a 0 left in Min Words throws `ArgumentOutOfRangeException` from `Awake()` when the asset is converted.

### 2. Create Command Assets

Select **Assets > Create > VoXR > Command Definition**.

Configure each command asset:
- **Intent** -- the intent name that fires in `OnCommandRecognised` (e.g. `launch_weapon`)
- **Patterns** -- one or more pattern strings with space-separated tokens. Use the same syntax as the code API: `launch {?quantity} {weapon} target {target}`
- **Allow Partial Match** -- when enabled, a match that left required slots unfilled enters pending state instead of being rejected, so follow-up speech can fill them. A winner that missed its own **first required element** is [barred](scoring.md#the-leading-required-miss-bar) before this is consulted, so it enters no pending at any score. It is also the precondition for [both ways an incomplete command still fires](command-recognition.md#the-two-ways-an-incomplete-command-still-fires), so the handler must tolerate every required slot being absent.
- **Requires Confirmation** -- when enabled, even a fully-matched command enters pending state and waits for an [explicit confirmation](command-recognition.md#explicit-confirmation) phrase before firing.

Each string entry represents one alternative pattern for the same intent. A token takes one of four forms:

| Token | Meaning |
|-------|---------|
| `word` | A required literal word |
| `{slot}` | A required slot |
| `{?slot}` | An optional slot |
| `?word` | An optional literal -- the word may be spoken or dropped without costing the match |

The optional literal is the prescribed fix for a droppable function word, but it is not free: read [the two costs](command-recognition.md#never-leave-a-required-function-word-between-a-bare-pattern-and-its-slot) before applying it wholesale.

Note where the `?` sits. For a slot it goes **inside** the braces: `?{slot}` is not an optional slot but a required literal spelled `?{slot}`, which no utterance can produce, so a pattern containing it never matches -- and nothing warns about the transposition.

Pattern strings are split into token arrays at runtime on the **space character only**, not on whitespace in general, so a tab pasted into a pattern stays inside its token and yields a token that matches nothing.

### 3. Create Command Set Assets

Select **Assets > Create > VoXR > Command Set**.

Configure each set asset:
- **Set Name** -- the name used with `SetActiveSets()` (e.g. `weapons`, `navigation`, `common`)
- **Commands** -- drag in the command assets that belong to this set

### 4. Wire Assets onto VoxrCommandRecogniser

Select the GameObject with your `VoxrCommandRecogniser` component and assign:

- **Speech Recogniser** -- required. Drag in the `VoxrSpeechRecogniser` this component listens to. Left empty, `OnEnable()` returns without subscribing to any speech event, so no transcript ever reaches the parser and no command ever fires. Nothing is logged.
- **Slot Assets** -- drag in all slot assets used by your commands. Leave the list empty for an all-literal grammar that declares no slots.
- **Command Set Assets** -- drag in your command set assets
- **Initial Active Set Names** -- enter the names of sets to activate on startup (e.g. `weapons`, `common`)

`VoxrCommandRecogniser.Awake()` converts the assets to runtime structs and calls `Configure()` + `SetActiveSets()` automatically.

**Command Set Assets must be non-empty; Slot Assets need not be.** An empty **Slot Assets** list converts normally with zero slots, so an all-literal grammar (`cease fire`, `weapons mode`) can be authored entirely in the Inspector. **Command Set Assets** is what carries the commands, so an empty list leaves `Awake()` with nothing to convert: neither `Configure()` nor `SetActiveSets()` runs, and the symptom is a recogniser that hears speech and recognises no commands at all. That is the one asset-list skip, and it logs a `Debug.LogWarning` naming the empty list whenever **Slot Assets** carries assets. With *neither* list assigned nothing is logged -- that is the code-driven case, where `Configure()` is expected to follow.

One further skip is silent by design and is not an asset-list problem at all: a `Configure()` call that lands *before* this component's `Awake()` -- from a script earlier in Script Execution Order, or on an inactive GameObject before `SetActive(true)` -- claims the component, and `Awake()` then ignores both lists however they are filled in. See [Code vs Inspector Priority](#code-vs-inspector-priority).

---

## Authoring warnings

The parser inspects the grammar as it is built -- for Inspector authoring, during `Awake()` -- and logs `Debug.LogWarning` for the shapes below. Nothing is rejected; the recogniser runs either way.

Three checks read slot values and alias variants alike, and they run **in player builds as well as the Editor**, so they show up in device logs too:

- **Uppercase in a value or alias variant** -- VOSK only ever emits lowercase, so it can never match.
- **Punctuation in a value or alias variant** -- VOSK strips punctuation, so it may not match as written. Write the stripped form (`oclock`, not `o'clock`).
- **Single-character value or alias variant** -- one character is too little for reliable recognition; for a value, declare an alias to a longer canonical instead (`a` -> `one`). The one-character *variant* that remedy produces warns in turn, and that is expected -- it is the shape the shipped sample uses, because the alias resolves when VOSK does hear the word and is harmless when it is dropped.

Three more scan the patterns:

- **Droppable required literal** -- a bare pattern plus a longer one that extends it with a required literal in front of a slot. Naming the literal and the slot at risk, it prescribes marking the literal optional (`?by`). Editor-only.
- **Two intents separated by one word** -- two *different* intents differing at exactly one required word, which tie when that word is dropped, leaving registration order to pick the intent. Editor-only, and withheld in three cases where the tie is not a hazard -- covered under the limits linked below.
- **More than 12 optional elements in one pattern** -- the eager-flush analysis cannot expand it, and is then abandoned for the **whole** command set: with `eagerFlushOnCompleteMatch` on, no command commits early and every complete match is held for the full hold or buffer window. Unlike the two above, this one fires in player builds as well.

The two scanned grammar-shape hazards, with their remedies and the limits of each scan, are covered in full under [Authoring hazards](command-recognition.md#authoring-hazards) — which also documents a shape nothing scans for.

---

## Common errors

These are thrown rather than logged, and all three surface from `VoxrCommandRecogniser.Awake()` -- one typo in an asset stops the component initialising:

- **`ArgumentException: Pattern for intent 'X' references undefined slot 'Y'`** -- a pattern writes `{Y}` but no asset in **Slot Assets** carries that **Slot Name**. Names are matched exactly and case-sensitively.
- **`ArgumentException: Unknown command set name: 'X'`** -- an entry in **Initial Active Set Names** matches no **Set Name** among the assigned **Command Set Assets**.
- **`ArgumentException: Duplicate command set name: 'X'`** -- two assets in **Command Set Assets** declare the same **Set Name**. Set names must be unique within one recogniser.

---

## Code vs Inspector Priority

If both Inspector assets and a code-based `Configure()` call are present, **the code call takes priority** -- it overwrites the asset-driven configuration. This lets you use assets for the baseline setup and override programmatically when needed.

The typical pattern is:
- **Assets only** -- for simple or designer-maintained setups
- **Code only** -- for fully dynamic configurations
- **Assets + code** -- assets provide the default, code overrides at runtime for specific scenarios

---

## Sample Assets

The Command Recognition sample includes a complete set of 20 ScriptableObject assets demonstrating every slot type and pattern form:

- 6 slot assets (enumerated, number sequence, with aliases)
- 11 command assets (single pattern, multi-pattern, optional slots, number slots)
- 3 command set assets (weapons, navigation, common)

Import the sample via **Package Manager > VoXR Speech Recognition > Samples > Command Recognition**, then look under `Samples~/CommandRecognition/AssetAuthoring/` for the full working example.

---

## See Also

- [Command Recognition](command-recognition.md) -- Patterns, slots, scoring, and the full parsing pipeline
- [Command Sets](command-sets.md) -- Runtime mode switching and the SetActiveSets vs single-set decision
- [Getting Started](getting-started.md) -- Code-based quick start examples
