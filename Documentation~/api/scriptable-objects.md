# ScriptableObject Assets

Inspector-friendly assets for zero-code command authoring. Create via **Assets > Create > VOSK XR**.

## VoskSlotAsset

`public class VoskSlotAsset : ScriptableObject` -- Namespace: `VoskXR.Commands`

### Fields

| Field | Type | Description |
|-------|------|-------------|
| `slotName` | `string` | Slot name used in pattern references |
| `slotType` | `VoskSlotType` | `Enumerated` or `NumberSequence` |
| `values` | `string[]` | Allowed values (Enumerated only) |
| `aliases` | `AliasEntry[]` | Variant-to-canonical mappings |
| `minWords` | `int` | Minimum digit words (NumberSequence, default 1) |
| `maxWords` | `int` | Maximum digit words (NumberSequence, default 3) |

### Methods

| Method | Description |
|--------|-------------|
| `ToDefinition()` | Converts to runtime `VoskSlotDefinition` struct |

### AliasEntry

Nested serializable struct for variant-to-canonical mappings.

| Field | Type | Description |
|-------|------|-------------|
| `variant` | `string` | The variant word/phrase (e.g. `"jackals"`) |
| `canonical` | `string` | The canonical value it maps to (e.g. `"jackal"`) |

## VoskCommandAsset

`public class VoskCommandAsset : ScriptableObject` -- Namespace: `VoskXR.Commands`

### Fields

| Field | Type | Description |
|-------|------|-------------|
| `intent` | `string` | The intent name |
| `patterns` | `string[]` | Pattern strings with space-separated tokens (e.g. `"launch {?quantity} {weapon} target {target}"`) |

Each pattern string is split on whitespace into a token array at runtime. Tokens can be literal words, `{slotName}`, or `{?slotName}` (optional).

### Methods

| Method | Description |
|--------|-------------|
| `ToDefinition()` | Converts to runtime `VoskCommandDefinition` struct. Splits pattern strings on whitespace into token arrays. |

## VoskCommandSetAsset

`public class VoskCommandSetAsset : ScriptableObject` -- Namespace: `VoskXR.Commands`

### Fields

| Field | Type | Description |
|-------|------|-------------|
| `setName` | `string` | The command set name |
| `commands` | `VoskCommandAsset[]` | Command assets in this set |

### Methods

| Method | Description |
|--------|-------------|
| `ToSet()` | Converts to runtime `VoskCommandSet` struct. Skips null entries with a warning. |

## VoskTestSuiteAsset

`public class VoskTestSuiteAsset : ScriptableObject` -- Namespace: `VoskXR.Testing`

Create via **Assets > Create > VOSK XR > Test Suite**.

A collection of test cases for regression-testing command definitions with the [Batch Test Runner](batch-test-runner.md).

### Fields

| Field | Type | Description |
|-------|------|-------------|
| `suiteName` | `string` | Human-readable name for this test suite |
| `cases` | `List<VoskTestCase>` | Test cases to run |

### Methods

| Method | Description |
|--------|-------------|
| `ToArray()` | Returns `cases` as a `VoskTestCase[]` for `VoskBatchTestRunner.RunAll()`. |
| `ToJson()` | Serializes all cases to a JSON string for portability and version control. |
| `FromJson(string json)` | Replaces `cases` from a JSON string. |

## VoskTestCase

`[Serializable] public class VoskTestCase` -- Namespace: `VoskXR.Testing`

Serializable struct representing a single test case.

| Field | Type | Description |
|-------|------|-------------|
| `input` | `string` | Text to feed through the command parser |
| `expectedIntent` | `string` | Expected intent name. Empty = expect rejection. |
| `expectedSlots` | `ExpectedSlot[]` | Array of `{name, value}` pairs. Omit to skip slot verification. |
| `wordConfidence` | `float` | Simulated uniform word confidence (0--1). `-1` = omit word data. |
| `description` | `string` | Human-readable description for the results table |

## See Also

- [Inspector Authoring](../inspector-authoring.md) -- workflow guide for zero-code setup
- [Command Definitions](command-definitions.md) -- runtime equivalents: `VoskCommandDefinition`, `VoskSlotDefinition`
- [Batch Test Runner](batch-test-runner.md) -- regression testing with `VoskTestSuiteAsset`
- [Editor Testing](../editor-testing.md) -- visual test UI guide
