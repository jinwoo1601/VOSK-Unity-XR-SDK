# VoxrNumberParser

`public static class VoxrNumberParser` -- Namespace: `VoXR.Commands`

Converts spoken number words into integers.

**Every consumer of a `NumberSequence` slot needs this class.** `VoxrCommand.GetSlot()` returns the words as spoken — `"two seven zero"`, never `"270"` — so `int.TryParse` on a `NumberSequence` slot fails on every utterance and yields `0` without throwing. The parser uses `DigitVocabulary` to decide which tokens a slot may consume, but it never converts the value; that conversion is the handler's job.

## Members

| Member | Description |
|--------|-------------|
| `DigitVocabulary` | `static readonly HashSet<string>`. All number words the parser recognises: zero through nineteen, the tens (twenty, thirty, …, ninety), and the cardinals hundred and thousand. This is the set `NumberSequence` slots greedily consume from. |
| `ParseDigitSequence(string words)` | Parses digit-per-word sequences by concatenating single digits. `"two seven zero"` -> `270`. Returns `0` for null/empty. Throws `FormatException` for any token outside `zero`–`nine` (including `ten`+ and the cardinals — use `ParseCardinal` for those). |
| `ParseCardinal(string words)` | Parses cardinal number phrases. `"fifteen"` -> `15`, `"two hundred"` -> `200`. Accepts the full `DigitVocabulary`. Returns `0` for null/empty. Throws `FormatException` for unrecognised words. |

## Example

For a slot that is always dictated digit-by-digit, call `ParseDigitSequence` alone and treat the `FormatException` as a misrecognition:

```csharp
void OnCommand(VoxrCommand cmd)
{
    if (cmd.Intent != "set_heading") return;

    string raw = cmd.GetSlot("heading");   // "two seven zero" — words, not digits
    try
    {
        Debug.Log($"Setting heading to {VoxrNumberParser.ParseDigitSequence(raw)}");
    }
    catch (FormatException)
    {
        Debug.LogWarning($"Heading \"{raw}\" is not a digit sequence.");
    }
}
```

### Accepting both forms

Where the speaker may say either ("two seven zero" or "two hundred seventy"), try the digit path first and fall back to the cardinal one — see [Command Recognition → NumberSequence Slots](../command-recognition.md#numbersequence-slots) for the reusable `TryParseNumberSlot` helper.

The order is load-bearing, because the two read the same words differently: `"two seven zero"` is `270` via `ParseDigitSequence` but `9` via `ParseCardinal`. Words only the cardinal path accepts then get its reading whatever the speaker meant — `"two seventy"` throws on the digit path and parses as `72`, not `270`. Prefer one convention per slot over relying on the fallback to guess intent.

## See Also

- [Command Recognition](../command-recognition.md) -- NumberSequence slot type and the canonical conversion snippet
- [Command Definitions](command-definitions.md) -- `VoxrSlotDefinition.NumberSequence()` factory method
- [Data Types](data-types.md) -- `VoxrCommand.GetSlot()`
