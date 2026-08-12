# Command Recognition Sample

Voice-driven command parsing with slots, command sets, and runtime mode
switching. Demonstrates the `VoxrCommandRecogniser` pipeline on top of
`VoxrSpeechRecogniser`.

## Setup

1. **Import the sample** via Package Manager > VoXR Speech Recognition > Samples > Command Recognition > Import.

2. **Download a VOSK model:**
   - Get [vosk-model-small-en-us-0.15](https://alphacephei.com/vosk/models) (~50 MB).
   - Place the `.zip` in `Assets/StreamingAssets/vosk-model-small-en-us-0.15.zip`.

3. **Open the scene** at `Assets/Samples/VoXR Speech Recognition/<version>/Command Recognition/CommandRecognition_Tactical.unity`.

4. **Run:**
   - **Windows Editor:** press Play. Speak commands into your PC mic and watch the cubes flash, the mode chip change, and the command log update.
   - **Quest:** switch platform to Android (arm64), enable `RECORD_AUDIO` in Player Settings, build, deploy.

## What's in the scene

The scene is a tiny "tactical console" with four labelled target cubes
(`hotel one`, `hotel two`, `alpha one`, `bravo two`) and four UI panels:

| Panel | Role |
|---|---|
| Title | `VoXR — Tactical Command Recognition` |
| ModeChip (top-right) | Live read-out of `VoxrCommandRecogniser.ActiveSetNames` |
| HelpText (top-left) | Targets and a list of try-saying commands |
| CommandLog (bottom-left) | Rolling log of recognised intents and unrecognised speech |
| LastCommand (bottom-right) | Intent, score, and slot values from the most recent match |

GameObjects:

| GameObject | Role |
|---|---|
| `Recogniser` | `VoxrSpeechRecogniser` |
| `CommandRecogniser` | `VoxrCommandRecogniser` (`bufferWindow = 2.0` for Quest 3 latency) |
| `TacticalDemo` | Holds two scripts: `CommandDemo` (defines slots/sets via code in `Start`) and `TacticalSceneController` (drives the UI panels and flashes the cubes) |
| `Target_HotelOne` … `Target_BravoTwo` | Cubes whose `MeshRenderer` is wired into `TacticalSceneController.targets` so the colour flashes resolve to the right cube via the `target` slot value |

### Try saying

| Phrase | Effect |
|---|---|
| `fire missiles at hotel one` | `launch_weapon` — Hotel One flashes red |
| `cease fire` | `cease_fire` — logged |
| `approach target alpha one` | `approach_target` — Alpha One flashes green |
| `open distance from target bravo two` | `retreat_from_target` — Bravo Two flashes blue |
| `set heading two seven zero` | `set_heading` with `heading = "two seven zero"` — `CommandDemo.LogHeading` converts it to `270` via `VoxrNumberParser` |
| `weapons mode` / `navigation mode` | Mode chip updates; only that set's grammar is active |
| `all modes` | All three sets active |
| `disable all` | Grammar fully disabled for 5 s, then auto-restored by `CommandDemo` |

## What the sample covers

- **Slots**: targets, weapons, quantity, named ranges, plus digit-sequence slots
  for heading/elevation. A `NumberSequence` slot value is the words as spoken
  (`"two seven zero"`, not `"270"`); `CommandDemo.TryParseNumberSlot` shows the
  canonical `VoxrNumberParser` conversion — digit sequence first, cardinal as
  fallback — that every consumer of such a slot needs.
- **Command sets**: `weapons`, `navigation`, and `common`, each with multiple
  phrasings per intent.
- **Runtime mode switching**: the mode commands call `SetActiveSets(...)` to
  swap the active grammar live; the mode chip reflects the change every frame.
- **Inspector authoring** (optional): toggle `Use Inspector Authoring` on
  `CommandDemo` and wire the ScriptableObjects under `AssetAuthoring/` to drive
  `Configure()` from assets instead of code. See `AssetAuthoring/README.md`.

## URP / HDRP note

The scene uses Unity's built-in default material. On URP/HDRP the cubes appear
pink because the legacy material's shader isn't in the pipeline. Either assign
a URP/HDRP material to each cube's `MeshRenderer`, or run the scene on the
Built-in Render Pipeline. Recognition itself is unaffected.

## Iterating in the Editor without deploying

There are two complementary ways to iterate on this sample without a Quest
build cycle.

### Live microphone in the Windows Editor

On the Windows Unity Editor, `VoxrSpeechRecogniser.StartRecognition()`
transparently routes audio through `UnityEngine.Microphone` and a desktop
build of `libvosk.dll` — the sample runs end-to-end in Play Mode with zero
code changes. The Windows DLLs ship inside the package, so just press
Play and speak into your PC microphone.

### Text injection API

For unit tests, CI, replay scenarios, and threshold-tuning without a mic:

- `VoxrCommandRecogniser.InjectText(text, words)` — pushes a string through
  the same parser → threshold → buffer → debounce path as real audio.
- `VoxrCommandRecogniser.FlushPendingBuffer()` — forces buffered text to
  parse immediately.
- `VoxrSpeechRecogniser.InjectResult(...)` / `InjectPartialResult(...)` —
  fire raw recogniser events directly, bypassing the command pipeline.
- `VoxrSpeechRecogniser.CreateSimulatedWords(text, confidence)` — synthesise
  a `VoxrWord[]` for confidence-threshold tests.

All injection methods are main-thread only.

## See Also

- [Command Recognition](../../Documentation~/command-recognition.md) -- pipeline concepts, patterns, slots, scoring
- [Command Sets](../../Documentation~/command-sets.md) -- named sets, runtime mode switching
- [Editor Testing](../../Documentation~/editor-testing.md) -- debug window, live mic, text injection, batch runner
- [CommandRecogniser API](../../Documentation~/api/command-recogniser.md) -- full event and method reference
