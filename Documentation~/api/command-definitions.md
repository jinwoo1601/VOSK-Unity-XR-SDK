# Command Definitions

Types used to declare commands, slots, and command sets.

## VoxrCommandDefinition

`public readonly struct VoxrCommandDefinition` -- Namespace: `VoXR.Commands`

Declares a command pattern with an intent name and one or more token arrays.

### Fields

| Field | Type | Description |
|-------|------|-------------|
| `Intent` | `string` | The intent name (e.g. `"launch_weapon"`) |
| `Patterns` | `string[][]` | One or more token arrays. Each token is a literal word, `{slotName}`, or `{?slotName}` (optional). |
| `AllowPartialMatch` | `bool` | When true, a match with unfilled required slots enters pending state instead of being rejected, allowing follow-up speech to fill the gaps. Default: `false`. |
| `RequiresConfirmation` | `bool` | When true, a fully-matched command enters pending state awaiting explicit confirmation before firing. Default: `false`. |

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
| `Name` | `string` | Slot name referenced in patterns as `{name}` or `{?name}` |
| `Type` | `VoxrSlotType` | `Enumerated` (fixed values) or `NumberSequence` (number words) |
| `Values` | `string[]` | Allowed values (Enumerated only) |
| `Aliases` | `Dictionary<string, string>` | Maps variant words to canonical values |
| `MinWords` | `int` | Minimum number words to consume (NumberSequence only). Counts words from `DigitVocabulary`, not digits — `"seventeen"` is one word. |
| `MaxWords` | `int` | Maximum number words to consume (NumberSequence only). Counts words from `DigitVocabulary`, not digits — `"seventeen"` is one word. |

### Factory Methods

```csharp
// Enumerated slot with fixed values
var targets = VoxrSlotDefinition.OneOf("target", "alpha one", "bravo two", "hotel one");

// Enumerated slot with aliases (constructor)
var quantity = new VoxrSlotDefinition("quantity",
    new[] { "one", "two", "three", "all" },
    new Dictionary<string, string> { { "a", "one" } });

// NumberSequence slot for number words
var heading = VoxrSlotDefinition.NumberSequence("heading", minWords: 1, maxWords: 3);
// Matches: "two seven zero" -> heading="two seven zero", "one eight" -> heading="one eight"
// The slot value is the spoken words; convert to an int with VoxrNumberParser.
```

## VoxrSlotType

`public enum VoxrSlotType` -- Namespace: `VoXR.Commands`

| Value | Description |
|-------|-------------|
| `Enumerated` | Matches against a fixed set of allowed values and aliases |
| `NumberSequence` | Greedily consumes consecutive number-word tokens from `VoxrNumberParser.DigitVocabulary` (zero–nineteen, the tens, plus "hundred" and "thousand"). The matched value is those words as spoken, not a number — convert with [`VoxrNumberParser`](number-parser.md). |

## VoxrCommandSet

`public readonly struct VoxrCommandSet` -- Namespace: `VoXR.Commands`

A named group of commands for mode-specific grammar.

### Fields

| Field | Type | Description |
|-------|------|-------------|
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
