# VoskNumberParser

`public static class VoskNumberParser` -- Namespace: `VoskXR.Commands`

Converts spoken digit words into integers. Used internally by `NumberSequence` slots and available for direct use in command handlers.

## Members

| Member | Description |
|--------|-------------|
| `DigitVocabulary` | `static readonly HashSet<string>`. All number words the parser recognises (zero through nine, plus cardinals like hundred, thousand). |
| `ParseDigitSequence(string words)` | Parses digit-per-word sequences by concatenating single digits. `"two seven zero"` -> `270`. Returns `0` for null/empty. Throws `FormatException` for unrecognised words. |
| `ParseCardinal(string words)` | Parses cardinal number phrases. `"fifteen"` -> `15`, `"two hundred"` -> `200`. Returns `0` for null/empty. Throws `FormatException` for unrecognised words. |

## Example

```csharp
void OnCommand(VoskCommand cmd)
{
    if (cmd.Intent == "set_heading")
    {
        string raw = cmd.GetSlot("heading");
        int heading = VoskNumberParser.ParseDigitSequence(raw);
        Debug.Log($"Setting heading to {heading}");
    }
}
```

## See Also

- [Command Recognition](../command-recognition.md) -- NumberSequence slot type
- [Command Definitions](command-definitions.md) -- `VoskSlotDefinition.NumberSequence()` factory method
- [Data Types](data-types.md) -- `VoskCommand.GetSlot()`
