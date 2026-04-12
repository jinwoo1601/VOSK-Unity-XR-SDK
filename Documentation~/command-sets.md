# Command Sets

Command sets let you group commands into named collections and swap the active grammar at runtime. This is essential for applications with distinct interaction modes -- weapons targeting, navigation, inventory management -- where you want the recogniser to only listen for contextually relevant commands.

---

## What Are Command Sets and Why Use Them

A `VoskCommandSet` is a named group of `VoskCommandDefinition` entries. When you register multiple sets with `Configure()`, none are active by default. You then call `SetActiveSets()` to choose which sets are live.

The key benefit: **inactive commands are excluded from the VOSK grammar entirely.** This means VOSK's constrained decoder only considers words from active commands, which:

- **Prevents out-of-mode matches** -- the user cannot accidentally trigger a weapons command while in navigation mode
- **Reduces the grammar search space** -- fewer candidate words means VOSK can be more decisive
- **Eliminates phonetic collisions** between modes -- words like "fire" (weapons) and "five" (navigation heading) no longer compete

---

## Creating and Registering Sets

Define your commands, group them into named sets, and register everything with `Configure()`:

```csharp
// Define commands for each mode
var weaponCommands = new[]
{
    new VoskCommandDefinition("launch_weapon",
        new[] { new[] { "launch", "{?quantity}", "{weapon}", "target", "{target}" } }),
    new VoskCommandDefinition("cease_fire",
        new[] { new[] { "cease", "fire" } }),
};

var navCommands = new[]
{
    new VoskCommandDefinition("set_heading",
        new[] { new[] { "heading", "{heading}" } }),
    new VoskCommandDefinition("approach_target",
        new[] { new[] { "approach", "target", "{target}" } }),
};

var modeCommands = new[]
{
    new VoskCommandDefinition("mode_weapons",
        new[] { new[] { "weapons", "mode" } }),
    new VoskCommandDefinition("mode_navigation",
        new[] { new[] { "navigation", "mode" } }),
};

// Create named sets
var weaponsSet = new VoskCommandSet("weapons", weaponCommands);
var navigationSet = new VoskCommandSet("navigation", navCommands);
var commonSet = new VoskCommandSet("common", modeCommands);

// Register all sets with shared slots (none active yet)
commandRecogniser.Configure(slots, new[] { weaponsSet, navigationSet, commonSet });
```

---

## Activating Sets at Runtime

After registration, activate one or more sets. Only their commands are included in the grammar:

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

Swap the active grammar when modes change. Only active-mode words exist in the VOSK vocabulary.

```csharp
// On mode switch:
commandRecogniser.SetActiveSets("navigation", "common");
```

**Pros:**
- Smaller grammar = fewer false matches from phonetically similar words across modes
- VOSK cannot produce words from inactive commands, eliminating cross-mode confusion
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
- Larger grammar = more phonetic collisions between modes (e.g. "fire" vs "five")
- VOSK may produce false matches from words that happen to be in an inactive mode's vocabulary
- Your application code must maintain and enforce mode state

### Recommendation

Use **SetActiveSets** when your modes have phonetically overlapping vocabulary (e.g. "fire" in weapons, "five" in navigation headings) and you can tolerate the brief audio gap. Use **single-set gating** when seamless audio is critical or you need to provide "wrong mode" feedback to the user.

---

## The Audio Gap

When you call `SetActiveSets()`, the SDK internally:

1. Stops audio capture
2. Rebuilds the parser with only the active sets' commands
3. Generates a new VOSK grammar JSON
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
