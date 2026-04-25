# Basic Transcription Sample

Live speech-to-text display using VOSK XR.

## Setup

1. **Import the sample** via Package Manager > VOSK XR Speech Recognition > Samples > Basic Transcription > Import.

2. **Download a VOSK model:**
   - Get [vosk-model-small-en-us-0.15](https://alphacephei.com/vosk/models) (~50 MB).
   - Place the `.zip` in `Assets/StreamingAssets/vosk-model-small-en-us-0.15.zip`.

3. **Create the scene:**
   - Create a new scene.
   - Add an empty GameObject and attach `VoskSpeechRecogniser`.
   - Add a Canvas with a `TextMeshPro - Text (UI)` element.
   - Add the `VoiceDemo` script to any GameObject.
   - Wire the `recogniser` and `displayText` references in the Inspector.

4. **Build and deploy:**
   - Switch platform to Android (arm64).
   - Ensure `RECORD_AUDIO` permission is enabled in Player Settings.
   - Build and run on a Meta Quest headset.

5. **Speak** — you should see live transcription text updating on screen.

## Notes

- The model extracts on first launch (a few seconds). Subsequent launches use the cached model.
- Partial results update in real time while you speak. Final results appear at utterance boundaries.

## See Also

- [Getting Started](../../Documentation~/getting-started.md) -- installation, model setup, lifecycle
- [SpeechRecogniser API](../../Documentation~/api/speech-recogniser.md) -- full event and method reference
