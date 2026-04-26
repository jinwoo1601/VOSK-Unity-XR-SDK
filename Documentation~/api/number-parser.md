# VoxrNumberParser

`public static class VoxrNumberParser` -- Namespace: `VoXR.Commands`

Converts spoken digit words into integers. Used internally by `NumberSequence` slots and available for direct use in command handlers.

## Members

| Member | Description |
|--------|-------------|
| `DigitVocabulary` | `static readonly HashSet<string>`. All number words the parser recognises: zero through nineteen, the tens (twenty, thirty, …, ninety), and the cardinals hundred and thousand. This is the set `NumberSequence` slots greedily consume from. |
| `ParseDigitSequence(string words)` | Parses digit-per-word sequences by concatenating single digits. `"two seven zero"` -> `270`. Returns `0` for null/empty. Throws `FormatException` for any token outside `zero`–`nine` (including `ten`+ and the cardinals — use `ParseCardinal` for those). |
| `ParseCardinal(string words)` | Parses cardinal number phrases. `"fifteen"` -> `15`, `"two hundred"` -> `200`. Accepts the full `DigitVocabulary`. Returns `0` for null/empty. Throws `FormatException` for unrecognised words. |

## Example

```csharp
void OnCommand(VoxrCommand cmd)
{
    if (cmd.Intent == "set_heading")
    {
        string raw = cmd.GetSlot("heading");
        int heading = VoxrNumberParser.ParseDigitSequence(raw);
        Debug.Log($"Setting heading to {heading}");
    }
}
```

## See Also

- [Command Recognition](../command-recognition.md) -- NumberSequence slot type
- [Command Definitions](command-definitions.md) -- `VoxrSlotDefinition.NumberSequence()` factory method
- [Data Types](data-types.md) -- `VoxrCommand.GetSlot()`
