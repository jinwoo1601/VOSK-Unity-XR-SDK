# Command Recognition Sample

Voice-driven command parsing with slots, command sets, and runtime mode
switching. Demonstrates the `VoskCommandRecogniser` pipeline on top of
`VoskSpeechRecogniser`.

## Setup

1. **Import the sample** via Package Manager > VOSK XR Speech Recognition > Samples > Command Recognition > Import.

2. **Download a VOSK model:**
   - Get [vosk-model-small-en-us-0.15](https://alphacephei.com/vosk/models) (~50 MB).
   - Place the `.zip` in `Assets/StreamingAssets/vosk-model-small-en-us-0.15.zip`.

3. **Create the scene:**
   - Create a new scene or open your own.
   - Add an empty GameObject and attach `VoskSpeechRecogniser`.
   - Add a second GameObject and attach `VoskCommandRecogniser`. Wire its
     `speechRecogniser` field to the first GameObject.
   - Add the `CommandDemo` script to any GameObject and wire `recogniser`
     and `commandRecogniser` in the Inspector.

4. **Build and deploy:**
   - Switch platform to Android (arm64).
   - Ensure `RECORD_AUDIO` permission is enabled in Player Settings.
   - Build and run on a Meta Quest headset.

5. **Speak a command** — try `"fire two missiles at hotel one"`,
   `"cease fire"`, or `"switch to navigation"`. Recognised commands are
   logged to the Android logcat under the `CommandDemo` tag.

## What the sample covers

- **Slots**: targets, weapons, quantity, named ranges, plus digit-sequence
  slots for heading/elevation.
- **Command sets**: `weapons`, `navigation`, and `common`, each with
  multiple phrasings per intent.
- **Runtime mode switching**: the `weapons mode`, `navigation mode`,
  `all modes`, and `disable all` commands call `SetActiveSets(...)` to
  swap the active grammar live.
- **Inspector authoring** (v2.5, optional): toggle `Use Inspector
  Authoring` on `CommandDemo` and wire the ScriptableObjects under
  `AssetAuthoring/` to drive `Configure()` from assets instead of code.
  See `AssetAuthoring/README.md` for details.

## Iterating in the Editor without deploying

The native bridge only runs on-device, so the sample won't parse live
audio in the Editor. To iterate on your command definitions without a
Quest build cycle, use the **text injection API** added in v0.10.0:

- `VoskCommandRecogniser.InjectText(text, words)` — pushes a string
  through the same parser → threshold → buffer → debounce path as real
  audio, firing `OnCommandRecognised` / `OnCommandsRecognised` /
  `OnUnrecognisedSpeech` exactly as VOSK would.
- `VoskCommandRecogniser.FlushPendingBuffer()` — forces any buffered
  text to parse immediately instead of waiting for `bufferWindow`.
- `VoskSpeechRecogniser.InjectResult(text, words, alternatives)` and
  `InjectPartialResult(text)` — fire raw recogniser events directly,
  bypassing the command pipeline.
- `VoskSpeechRecogniser.CreateSimulatedWords(text, confidence)` —
  synthesises a `VoskWord[]` with uniform confidence when you want to
  exercise the confidence-threshold filter.

All injection methods are main-thread only. See
`Tests/Runtime/VoskCommandRecogniserInjectionTests.cs` and
`VoskSpeechRecogniserInjectionTests.cs` in the package for executable
usage examples.

## Notes

- The model extracts on first launch (a few seconds). Subsequent launches
  use the cached model.
- `CommandDemo` activates all three command sets on start. In a real
  game you would call `SetActiveSets(...)` per game state to scope the
  active grammar to what the player can currently do.
