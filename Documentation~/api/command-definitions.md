# Command Definitions

Types used to declare commands, slots, and command sets.

## VoskCommandDefinition

`public readonly struct VoskCommandDefinition` -- Namespace: `VoskXR.Commands`

Declares a command pattern with an intent name and one or more token arrays.

### Fields

| Field | Type | Description |
|-------|------|-------------|
| `Intent` | `string` | The intent name (e.g. `"launch_weapon"`) |
| `Patterns` | `string[][]` | One or more token arrays. Each token is a literal word, `{slotName}`, or `{?slotName}` (optional). |

### Examples

```csharp
// Single pattern
new VoskCommandDefinition("cease_fire", new[] { new[] { "cease", "fire" } })

// Multiple patterns (alternative phrasings)
new VoskCommandDefinition("launch_weapon", new[] {
    new[] { "launch", "{?quantity}", "{weapon}", "target", "{target}" },
    new[] { "fire", "{weapon}", "at", "{target}" },
})
```

## VoskSlotDefinition

`public readonly struct VoskSlotDefinition` -- Namespace: `VoskXR.Commands`

Declares a named slot with allowed values or number-sequence behaviour.

### Fields

| Field | Type | Description |
|-------|------|-------------|
| `Name` | `string` | Slot name referenced in patterns as `{name}` or `{?name}` |
| `Type` | `VoskSlotType` | `Enumerated` (fixed values) or `NumberSequence` (digit words) |
| `Values` | `string[]` | Allowed values (Enumerated only) |
| `Aliases` | `Dictionary<string, string>` | Maps variant words to canonical values |
| `MinWords` | `int` | Minimum digit words to consume (NumberSequence only) |
| `MaxWords` | `int` | Maximum digit words to consume (NumberSequence only) |

### Factory Methods

```csharp
// Enumerated slot with fixed values
var targets = VoskSlotDefinition.OneOf("target", "alpha one", "bravo two", "hotel one");

// Enumerated slot with aliases (constructor)
var quantity = new VoskSlotDefinition("quantity",
    new[] { "one", "two", "three", "all" },
    new Dictionary<string, string> { { "a", "one" } });

// NumberSequence slot for digit words
var heading = VoskSlotDefinition.NumberSequence("heading", minWords: 1, maxWords: 3);
// Matches: "two seven zero" -> 270, "one eight" -> 18
```

## VoskSlotType

`public enum VoskSlotType` -- Namespace: `VoskXR.Commands`

| Value | Description |
|-------|-------------|
| `Enumerated` | Matches against a fixed set of allowed values and aliases |
| `NumberSequence` | Greedily consumes consecutive digit-word tokens ("zero" through "nine") |

## VoskCommandSet

`public readonly struct VoskCommandSet` -- Namespace: `VoskXR.Commands`

A named group of commands for mode-specific grammar.

### Fields

| Field | Type | Description |
|-------|------|-------------|
| `Name` | `string` | Set name (e.g. `"weapons"`, `"navigation"`) |
| `Commands` | `VoskCommandDefinition[]` | Commands in this set |

### Example

```csharp
var weaponsSet = new VoskCommandSet("weapons", new[] {
    new VoskCommandDefinition("launch_weapon", ...),
    new VoskCommandDefinition("cease_fire", ...),
});
```

## See Also

- [Command Recognition](../command-recognition.md) -- how patterns and slots are matched
- [Command Sets](../command-sets.md) -- mode-specific grammar switching
- [Inspector Authoring](../inspector-authoring.md) -- zero-code alternative via ScriptableObjects
- [ScriptableObject Assets](scriptable-objects.md) -- `VoskCommandAsset`, `VoskSlotAsset`, `VoskCommandSetAsset`
- [Data Types](data-types.md) -- `VoskCommand`, `VoskSlotMatch` result types
- [Number Parser](number-parser.md) -- parsing `NumberSequence` slot values
