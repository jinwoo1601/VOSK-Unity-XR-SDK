# Inspector Authoring

The SDK supports zero-code command setup using ScriptableObject assets. Instead of writing `Configure()` calls, you create slot, command, and command set assets in the Unity Inspector, then drag them onto your `VoskCommandRecogniser` component.

---

## Overview

Inspector authoring provides:

- **Visual editing** of slot values, aliases, patterns, and command sets directly in the Unity Inspector
- **No code required** for basic setups -- ideal for designers or rapid prototyping
- **Reusable assets** that can be shared across scenes and prefabs
- **Version-control-friendly** ScriptableObjects serialised as `.asset` files

At runtime, the assets are automatically converted to the same `VoskSlotDefinition`, `VoskCommandDefinition`, and `VoskCommandSet` structs that the code-based API uses. There is no performance difference.

---

## Step-by-Step Setup

### 1. Create Slot Assets

Right-click in the Project window and select **Assets > Create > VOSK XR > Slot Definition**.

Configure each slot asset in the Inspector:
- **Slot Name** -- the name referenced in patterns (e.g. `target`, `weapon`, `heading`)
- **Slot Type** -- `Enumerated` for fixed values, `NumberSequence` for digit words
- **Values** (Enumerated only) -- the allowed slot values (e.g. `alpha one`, `bravo two`, `hotel one`)
- **Aliases** -- variant-to-canonical mappings (e.g. `jackals` -> `jackal`)
- **Min/Max Words** (NumberSequence only) -- range of digit words to consume

### 2. Create Command Assets

Select **Assets > Create > VOSK XR > Command Definition**.

Configure each command asset:
- **Intent** -- the intent name that fires in `OnCommandRecognised` (e.g. `launch_weapon`)
- **Patterns** -- one or more pattern strings with space-separated tokens. Use the same syntax as the code API: `launch {?quantity} {weapon} target {target}`

Pattern strings are split on whitespace into token arrays at runtime. Each string entry represents one alternative pattern for the same intent.

### 3. Create Command Set Assets

Select **Assets > Create > VOSK XR > Command Set**.

Configure each set asset:
- **Set Name** -- the name used with `SetActiveSets()` (e.g. `weapons`, `navigation`, `common`)
- **Commands** -- drag in the command assets that belong to this set

### 4. Wire Assets onto VoskCommandRecogniser

Select the GameObject with your `VoskCommandRecogniser` component and assign:

- **Slot Assets** -- drag in all slot assets used by your commands
- **Command Set Assets** -- drag in your command set assets
- **Initial Active Set Names** -- enter the names of sets to activate on startup (e.g. `weapons`, `common`)

`VoskCommandRecogniser.Awake()` converts the assets to runtime structs and calls `Configure()` + `SetActiveSets()` automatically.

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

Import the sample via **Package Manager > VOSK XR Speech Recognition > Samples > Command Recognition**, then look under `Samples~/CommandRecognition/AssetAuthoring/` for the full working example.

---

## See Also

- [Command Recognition](command-recognition.md) -- Patterns, slots, scoring, and the full parsing pipeline
- [Command Sets](command-sets.md) -- Runtime mode switching and the SetActiveSets vs single-set decision
- [Getting Started](getting-started.md) -- Code-based quick start examples
