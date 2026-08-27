# Command Definitions

Types used to declare commands, slots, and command sets.

## VoxrCommandDefinition

`public readonly struct VoxrCommandDefinition` -- Namespace: `VoXR.Commands`

Declares a command pattern with an intent name and one or more token arrays.

### Fields

| Field | Type | Description |
|-------|------|-------------|
| `Intent` | `string` | The intent name (e.g. `"launch_weapon"`) |
| `Patterns` | `string[][]` | One or more token arrays. Each token takes one of four forms: a literal word, `{slotName}` (required slot), `{?slotName}` (optional slot), or `?word` (optional literal — the word may be spoken or dropped without costing the match, which is the prescribed fix for a droppable function word but [carries two costs worth reading first](../command-recognition.md#never-leave-a-required-function-word-between-a-bare-pattern-and-its-slot)). |
| `AllowPartialMatch` | `bool` | When true, a match with unfilled required slots enters pending state instead of being rejected, allowing follow-up speech to fill the gaps — one slot per utterance if that is how they arrive. A winner that missed its own **first required element** is [barred](../scoring.md#the-leading-required-miss-bar) before this is consulted, so it enters no pending at any score. It is also the precondition for [both ways an incomplete command still fires](../command-recognition.md#the-two-ways-an-incomplete-command-still-fires) — a confirm phrase, or `FireAsIs` on timeout — so this command's handler must tolerate every required slot being absent. Default: `false`. |
| `RequiresConfirmation` | `bool` | When true, a fully-matched command enters pending state awaiting explicit confirmation before firing. Default: `false`. |

The `?` marking an optional slot goes **inside** the braces. `?{slotName}` is not an optional slot: nothing reads it as a slot reference, so it is charged as a required literal — the literal text `?{slotName}`, which no utterance can produce, so the pattern containing it can never match. Nothing warns about the transposition.

### Examples

```csharp
// Single pattern
new VoxrCommandDefinition("cease_fire", new[] { new[] { "cease", "fire" } })

// Multiple patterns (alternative phrasings)
new VoxrCommandDefinition("launch_weapon", new[] {
    new[] { "launch", "{?quantity}", "{weapon}", "target", "{target}" },
    new[] { "fire", "{weapon}", "at", "{target}" },
})

// Partial match + confirmation
new VoxrCommandDefinition("launch_weapon", new[] {
    new[] { "launch", "{weapon}", "target", "{target}" },
}, allowPartialMatch: true, requiresConfirmation: true)
```

## VoxrSlotDefinition

`public readonly struct VoxrSlotDefinition` -- Namespace: `VoXR.Commands`

Declares a named slot with allowed values or number-sequence behaviour.

### Fields

| Field | Type | Description |
|-------|------|-------------|
| `Name` | `string` | Slot name referenced in patterns as `{name}` or `{?name}`. Matched exactly and case-sensitively. Note that `?name` — without braces — is not a slot reference at all but an optional *literal* word. |
| `Type` | `VoxrSlotType` | `Enumerated` (fixed values) or `NumberSequence` (number words) |
| `Values` | `string[]` | Allowed values (Enumerated only). Each value should be lowercase, punctuation-free, and longer than one character; the parser logs a warning per violation when the grammar is built, in player builds as well as the Editor. See [Authoring warnings](../inspector-authoring.md#authoring-warnings). |
| `Aliases` | `Dictionary<string, string>` | Maps variant words to canonical values. A slot filled through an alias reports the **canonical** value, never the variant the speaker said. Keys are checked on the same three counts as `Values` -- uppercase, punctuation, single character -- and an uppercase or punctuated key never matches, because VOSK never produces one. See [Authoring warnings](../inspector-authoring.md#authoring-warnings). **`null`** when no aliases were supplied — the constructor stores `null` rather than an empty dictionary, so code that iterates this must null-check first. |
| `MinWords` | `int` | Minimum number words to consume (NumberSequence only). Counts words from `DigitVocabulary`, not digits — `"seventeen"` is one word. Must be at least 1. `0` on an Enumerated slot. |
| `MaxWords` | `int` | Maximum number words to consume (NumberSequence only). Counts words from `DigitVocabulary`, not digits — `"seventeen"` is one word. Must be at least `MinWords`. `0` on an Enumerated slot. |

### Construction

```csharp
// Enumerated slot with fixed values
var targets = VoxrSlotDefinition.OneOf("target", "alpha one", "bravo two", "hotel one");

// Enumerated slot from an existing array (constructor, no aliases)
var weapons = new VoxrSlotDefinition("weapon", new[] { "rocket", "laser", "railgun" });

// Enumerated slot with aliases (constructor)
var quantity = new VoxrSlotDefinition("quantity",
    new[] { "one", "two", "three", "all" },
    new Dictionary<string, string> { { "a", "one" } });

// NumberSequence slot for number words
var heading = VoxrSlotDefinition.NumberSequence("heading", minWords: 1, maxWords: 3);
// Matches: "two seven zero" -> heading="two seven zero", "one eight" -> heading="one eight"
// The slot value is the spoken words; convert to an int with VoxrNumberParser.
```

`NumberSequence` throws `ArgumentOutOfRangeException` if `minWords` is below 1, or if `maxWords` is below `minWords`. Both bounds are checked there and nowhere else, so an out-of-range pair authored in the Inspector surfaces as that exception from `Awake()`.

## VoxrSlotType

`public enum VoxrSlotType` -- Namespace: `VoXR.Commands`

| Value | Description |
|-------|-------------|
| `Enumerated` | Matches against a fixed set of allowed values and aliases. The matched value is the **canonical** value from `Values` — an alias resolves to its canonical form before the slot is filled, so the spoken variant never reaches your handler. |
| `NumberSequence` | Greedily consumes consecutive number-word tokens from `VoxrNumberParser.DigitVocabulary` (zero–nineteen, the tens, plus "hundred" and "thousand"). The matched value is those words as spoken, not a number — convert with [`VoxrNumberParser`](number-parser.md). |

## VoxrCommandSet

`public readonly struct VoxrCommandSet` -- Namespace: `VoXR.Commands`

A named group of commands for mode-specific grammar.

### Properties

Get-only properties, not fields, unlike the two structs above.

| Property | Type | Description |
|----------|------|-------------|
| `Name` | `string` | Set name (e.g. `"weapons"`, `"navigation"`) |
| `Commands` | `VoxrCommandDefinition[]` | Commands in this set |

### Example

```csharp
var weaponsSet = new VoxrCommandSet("weapons", new[] {
    new VoxrCommandDefinition("launch_weapon", ...),
    new VoxrCommandDefinition("cease_fire", ...),
});
```

## See Also

- [Command Recognition](../command-recognition.md) -- how patterns and slots are matched
- [Command Sets](../command-sets.md) -- mode-specific grammar switching
- [Inspector Authoring](../inspector-authoring.md) -- zero-code alternative via ScriptableObjects
- [ScriptableObject Assets](scriptable-objects.md) -- `VoxrCommandAsset`, `VoxrSlotAsset`, `VoxrCommandSetAsset`
- [Data Types](data-types.md) -- `VoxrCommand`, `VoxrSlotMatch` result types
- [Number Parser](number-parser.md) -- parsing `NumberSequence` slot values
