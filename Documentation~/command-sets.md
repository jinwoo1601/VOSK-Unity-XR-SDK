# Command Sets

Command sets let you group commands into named collections and swap the active grammar at runtime. This is essential for applications with distinct interaction modes -- weapons targeting, navigation, inventory management -- where you want the recogniser to only listen for contextually relevant commands.

---

## What Are Command Sets and Why Use Them

A `VoxrCommandSet` is a named group of `VoxrCommandDefinition` entries. When you register multiple sets with `Configure()`, none are active by default. You then call `SetActiveSets()` to choose which sets are live. Until you do, no parser and no grammar exist, and every utterance is dropped without a log entry -- a set-based setup that forgets `SetActiveSets()` produces zero recognition and zero diagnostics.

The key benefit: **inactive commands cannot match, and their pattern literals leave the VOSK grammar.** This:

- **Prevents out-of-mode matches** -- the parser only considers active commands, so the user cannot accidentally trigger a weapons command while in navigation mode
- **Removes inactive pattern literals from the decoder** -- a word that appears only in inactive commands' patterns can no longer be decoded at all, so it cannot substitute for a similar-sounding active word

**What set switching does *not* remove.** The grammar always contains, whichever sets are active: every configured slot's values and alias keys, the full digit vocabulary whenever any configured slot is `NumberSequence`, and the confirm/cancel follow-up vocabulary. Slots are configured on the component as one flat array and are never filtered per set. So deactivating navigation does **not** take "five" out of the decoder -- it is digit vocabulary -- and a phonetic collision like "fire" vs "five" survives every set configuration. Set switching resolves collisions only between *pattern literals*. Nor should you expect a recognition-accuracy gain from the smaller grammar alone: measurement showed none -- the value of set restriction is rejecting out-of-set commands, not boosting in-set confidence (see [Known Limitations](../KNOWN_LIMITATIONS.md)).

---

## Creating and Registering Sets

Define your commands, group them into named sets, and register everything with `Configure()`:

```csharp
// Define commands for each mode
var weaponCommands = new[]
{
    new VoxrCommandDefinition("launch_weapon",
        new[] { new[] { "launch", "{?quantity}", "{weapon}", "target", "{target}" } }),
    new VoxrCommandDefinition("cease_fire",
        new[] { new[] { "cease", "fire" } }),
};

var navCommands = new[]
{
    new VoxrCommandDefinition("set_heading",
        new[] { new[] { "heading", "{heading}" } }),
    new VoxrCommandDefinition("approach_target",
        new[] { new[] { "approach", "target", "{target}" } }),
};

var modeCommands = new[]
{
    new VoxrCommandDefinition("mode_weapons",
        new[] { new[] { "weapons", "mode" } }),
    new VoxrCommandDefinition("mode_navigation",
        new[] { new[] { "navigation", "mode" } }),
};

// Create named sets
var weaponsSet = new VoxrCommandSet("weapons", weaponCommands);
var navigationSet = new VoxrCommandSet("navigation", navCommands);
var commonSet = new VoxrCommandSet("common", modeCommands);

// Register all sets with shared slots (none active yet)
commandRecogniser.Configure(slots, new[] { weaponsSet, navigationSet, commonSet });
```

Set names must be unique -- `Configure()` throws an `ArgumentException` naming the duplicate if two sets share a name. `SetActiveSets()` likewise throws an `ArgumentException` for a set name that was never registered, so a typo surfaces as an exception rather than a silently inactive mode.

---

## Activating Sets at Runtime

After registration, activate one or more sets. Only their commands can match, and only their pattern literals are emitted into the grammar:

```csharp
// Activate weapons + common (mode switching commands)
commandRecogniser.SetActiveSets("weapons", "common");

// Switch to navigation mode when the user says "navigation mode"
commandRecogniser.OnCommandRecognised += cmd =>
{
    if (cmd.Intent == "mode_navigation")
        commandRecogniser.SetActiveSets("navigation", "common");
    else if (cmd.Intent == "mode_weapons")
        commandRecogniser.SetActiveSets("weapons", "common");
};
```

You can also activate a single set with the convenience method:

```csharp
commandRecogniser.SetActiveSet("weapons");
```

Query the currently active sets at any time via the `ActiveSetNames` property.

---

## Decision Guide: SetActiveSets vs Single-Set Gating

There are two architectural approaches to mode-specific commands. Each has meaningful trade-offs.

### Approach A: SetActiveSets (grammar switching)

Swap the active grammar when modes change. Only active-mode *pattern literals* exist in the VOSK vocabulary (slot values, digit vocabulary, and follow-up vocabulary always remain -- see above).

```csharp
// On mode switch:
commandRecogniser.SetActiveSets("navigation", "common");
```

**Pros:**
- The parser cannot fire an out-of-mode command, whatever the decoder hears
- VOSK cannot produce a word that appears only in inactive commands' patterns, removing that class of cross-mode substitution
- Clean separation of concerns -- each mode is self-contained

**Cons:**
- Causes a ~50ms audio gap during grammar rebuild (speech during the gap is lost)
- Users must pause briefly (~500ms) after a mode switch before speaking the next command
- Cannot tell the user "that command isn't available in this mode" because the words were never in the grammar -- VOSK maps them to `[unk]` or phonetically similar in-grammar words

### Approach B: Single-set gating (application-level filtering)

Register all commands in a single set (or activate all sets permanently). Gate commands in your `OnCommandRecognised` handler based on application state.

```csharp
commandRecogniser.OnCommandRecognised += cmd =>
{
    if (!IsCommandAvailableInCurrentMode(cmd.Intent))
    {
        ShowHint($"'{cmd.Intent}' is not available in {currentMode} mode");
        return;
    }
    HandleCommand(cmd);
};
```

**Pros:**
- No audio gap -- recognition is continuous
- You receive the raw transcript even for "wrong mode" commands, enabling UX like "that command is only available in weapons mode"
- Simpler lifecycle -- no grammar rebuild

**Cons:**
- Every mode's pattern literals are live at once, so a word from one mode can be substituted for a similar-sounding word from another
- The parser will match and fire out-of-mode commands unless your handler filters them
- Your application code must maintain and enforce mode state

### Recommendation

Use **SetActiveSets** when your modes' *pattern literals* overlap phonetically and you can tolerate the brief audio gap. Use **single-set gating** when seamless audio is critical or you need to provide "wrong mode" feedback to the user. A collision involving a slot value or a digit word ("fire" vs "five") is not resolved by either approach -- those words are in the grammar whichever sets are active -- so resolve it in the vocabulary itself, or lean on the score and confidence gates.

---

## The Audio Gap

When you call `SetActiveSets()`, the SDK internally:

1. Stops audio capture
2. Rebuilds the parser with only the active sets' commands
3. Generates a new VOSK grammar JSON -- single words plus multi-word phrase entries for each pattern's contiguous literal runs (see [What the grammar contains](command-recognition.md#what-the-grammar-contains))
4. Applies the grammar to the VOSK recogniser
5. Restarts audio capture

This full sequence takes ~50ms minimum on Quest 3. Any speech during that window is dropped at the audio layer before VOSK ever sees it. In practice, the first one or two words spoken immediately after a mode switch may be lost.

### Handling the gap

- **Pause after switching.** After triggering a mode switch command, wait ~500ms before speaking the next command. In a game UI, you can gate user input with a visual indicator ("Mode: Weapons" appearing on screen) or wire a callback off `OnCommandRecognised` for your mode-switch intents.
- **Use audio/visual feedback.** Play a confirmation sound or flash the UI when the mode switch completes, signalling to the user that the system is ready for the next command.
- **Consider the single-set approach** if the gap is unacceptable for your use case (see the decision guide above).

---

## See Also

- [Command Recognition](command-recognition.md) -- The full parsing pipeline, patterns, slots, and scoring
- [Inspector Authoring](inspector-authoring.md) -- Create command sets as ScriptableObject assets without code
- [Known Limitations](../KNOWN_LIMITATIONS.md) -- Details on the audio gap, grammar size trade-offs, and set restriction behaviour
