# v3 Phase 1: Text Injection API

## Context

Every command recognition change currently requires deploying to Quest 3 and testing via adb logcat. This is the #1 friction point — a single iteration cycle takes minutes. Phase 1 of v3 adds the ability to test the command parsing pipeline from the Unity Editor without any native bridge, covering ~80% of what developers iterate on (command definitions, slot combinations, scoring, thresholds).

Phase 2 (live mic in Editor via desktop VOSK + Unity Microphone API) is documented in `v3-and-beyond-analysis.md` and will be planned separately once Phase 1 is proven.

---

## What we're adding

Two injection entry points at different layers of the pipeline:

1. **`VoskSpeechRecogniser.InjectResult()`** — fires the same `OnFinalResult` + `OnResult` events that the native bridge would, exercising the full event pipeline including any other subscribers.
2. **`VoskCommandRecogniser.InjectText()`** — calls `HandleResult()` directly, testing only the command layer. Faster and more direct for command iteration.

Plus a convenience helper:

3. **`VoskSpeechRecogniser.CreateSimulatedWords()`** — generates `VoskWord[]` with configurable confidence for threshold testing.

---

## Design Decisions

- **Not `#if UNITY_EDITOR` guarded.** Available in builds too, for replay/testing/demo scenarios. If someone wants Editor-only, they can guard their calling code.
- **Bypasses all native state.** Works regardless of `_bridgeAvailable`, `IsModelReady`, or `_isRecognising`. The injection pathway is intentionally decoupled from the native lifecycle.
- **Fires events synchronously on the calling thread.** Matches the existing `Update()` polling pattern. Must be called from the main thread.
- **`InjectText` requires `Configure()` first.** Logs a warning and no-ops otherwise. The parser must exist to parse.

---

## Files to Modify

### 1. `Runtime/VoskSpeechRecogniser.cs`

Add after `SetGrammar()` (line ~236):

```csharp
/// <summary>
/// Injects a final result as if VOSK recognised it. Fires OnFinalResult and OnResult.
/// Works without native bridge — use for Editor testing and CI.
/// </summary>
public void InjectResult(string text, VoskWord[] words = null, VoskAlternative[] alternatives = null)
{
    if (string.IsNullOrEmpty(text)) return;

    OnFinalResult?.Invoke(text);

    var result = new VoskResult(
        text,
        words ?? Array.Empty<VoskWord>(),
        alternatives ?? Array.Empty<VoskAlternative>());
    OnResult?.Invoke(result);
}

/// <summary>
/// Injects a partial result. Fires OnPartialResult.
/// </summary>
public void InjectPartialResult(string text)
{
    OnPartialResult?.Invoke(text);
}

/// <summary>
/// Creates VoskWord[] from text with uniform confidence and sequential timing.
/// Use for testing confidence thresholds without a real microphone.
/// </summary>
public static VoskWord[] CreateSimulatedWords(string text, float confidence = 1.0f)
{
    if (string.IsNullOrWhiteSpace(text)) return Array.Empty<VoskWord>();

    var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    var words = new VoskWord[tokens.Length];
    for (int i = 0; i < tokens.Length; i++)
        words[i] = new VoskWord(tokens[i], confidence, i * 0.3f, (i + 1) * 0.3f);
    return words;
}
```

### 2. `Runtime/Commands/VoskCommandRecogniser.cs`

Add after `Configure()` (line ~52):

```csharp
/// <summary>
/// Injects text directly into the command parser. Fires OnCommandRecognised
/// or OnUnrecognisedSpeech. Requires Configure() to have been called first.
/// </summary>
public void InjectText(string text, VoskWord[] words = null)
{
    if (_parser == null)
    {
        Debug.LogWarning("[VoskCommandRecogniser] InjectText called before Configure(). " +
            "Call Configure() first to set up the parser.");
        return;
    }

    var result = new VoskResult(
        text,
        words ?? Array.Empty<VoskWord>(),
        Array.Empty<VoskAlternative>());
    HandleResult(result);
}
```

`HandleResult(VoskResult)` is the existing private method at line 87 that runs the parser, applies threshold filtering, and fires the appropriate event. No changes needed to it — `InjectText` calls it directly from within the same class.

---

## New File: `Tests/Runtime/VoskTextInjectionTests.cs`

Unit tests covering:

| Test | What it verifies |
|------|-----------------|
| `InjectResult_FiresOnFinalResult` | `OnFinalResult` receives the injected text |
| `InjectResult_FiresOnResult_WithWords` | `OnResult` receives the full `VoskResult` with provided words |
| `InjectResult_FiresOnResult_WithNullWords` | `OnResult` defaults to empty arrays |
| `InjectPartialResult_FiresOnPartialResult` | `OnPartialResult` receives the text |
| `InjectText_MatchingCommand_FiresOnCommandRecognised` | Configured parser matches and fires command event |
| `InjectText_UnmatchedText_FiresOnUnrecognisedSpeech` | Non-matching text fires unrecognised event |
| `InjectText_WithoutConfigure_LogsWarning` | Logs warning and does not throw |
| `InjectText_BelowMinConfidence_Rejected` | Low confidence words get rejected by threshold |
| `InjectText_BelowMinScore_Rejected` | Partial pattern match gets rejected by score threshold |
| `CreateSimulatedWords_CorrectCount` | Word count matches token count |
| `CreateSimulatedWords_AppliesConfidence` | All words have the given confidence |

Tests involving `VoskSpeechRecogniser` require MonoBehaviour instantiation (Play Mode tests in `Tests/Runtime/`). Pure parser injection tests use the existing `VoskCommandParser` directly.

---

## What This Does NOT Change

- No native C++ code touched
- No new native libraries shipped
- No changes to `BridgeNative.cs`, `ModelExtractor.cs`, `VoskCommandParser.cs`, `VoskResult.cs`, `VoskCommand.cs`
- No changes to `.asmdef` files
- Existing tests unaffected
- Android build unaffected

---

## Existing Code Reused

| What | Where |
|------|-------|
| `HandleResult(VoskResult)` | `VoskCommandRecogniser.cs:87` — the complete parse + threshold pipeline |
| `VoskResult` constructor | `VoskResult.cs:85` — `new VoskResult(text, words, alternatives)` |
| `VoskWord` constructor | `VoskResult.cs:23` — `new VoskWord(text, confidence, startTime, endTime)` |
| `Array.Empty<T>()` | Used throughout existing code for null defaults |

---

## Verification Plan

1. **Existing unit tests pass:** Run `VoskCommandParserTests` — nothing should break.
2. **New injection tests pass:** Run `VoskTextInjectionTests` in Play Mode.
3. **Manual Editor test:** In a scene with configured `VoskCommandRecogniser`:
   - `commandRecogniser.InjectText("launch all missiles target hotel one")` → `OnCommandRecognised` fires with intent=`launch_weapon`
   - `commandRecogniser.InjectText("hello world")` → `OnUnrecognisedSpeech` fires
   - `commandRecogniser.InjectText("cease fire", VoskSpeechRecogniser.CreateSimulatedWords("cease fire", 0.2f))` → rejected (below minConfidence=0.4)
4. **Android build compiles:** No platform dependencies in the new methods.

---

## Version

This would ship as **0.6.0** — new public API, no breaking changes.
