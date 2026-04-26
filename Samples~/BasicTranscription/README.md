# Basic Transcription Sample

Live speech-to-text display using VoXR. Demonstrates partial/final results,
per-word confidence and timing, n-best alternatives, and error reporting.

## Setup

1. **Import the sample** via Package Manager > VoXR Speech Recognition > Samples > Basic Transcription > Import.

2. **Download a VOSK model:**
   - Get [vosk-model-small-en-us-0.15](https://alphacephei.com/vosk/models) (~50 MB).
   - Place the `.zip` in `Assets/StreamingAssets/vosk-model-small-en-us-0.15.zip`.

3. **Open the scene** at `Assets/Samples/VoXR Speech Recognition/<version>/Basic Transcription/BasicTranscription.unity`.

4. **Run:**
   - **Windows Editor:** press Play. Speak into your PC mic — you'll see transcription on screen and per-word confidence in the right panel. Requires the four `libvosk*.dll` files in `Runtime/Plugins/x86_64/` (see the package README's *Windows Editor Setup*).
   - **Quest:** switch platform to Android (arm64), enable `RECORD_AUDIO` in Player Settings, build, deploy.

## What's in the scene

| GameObject | Role |
|---|---|
| `Recogniser` | `VoxrSpeechRecogniser` with `Max Alternatives = 3` so the n-best panel populates. |
| `VoiceDemo` | Subscribes to `OnPartialResult`, `OnFinalResult`, `OnResult`, `OnError`. Drives the four UI text fields. |
| `Canvas/TranscriptText` | Shows the live transcript (partial during speech, final on utterance boundary). |
| `Canvas/WordsText` | Per-word table: `word | confidence | start–end seconds`. Populated from `VoxrResult.Words`. |
| `Canvas/AlternativesText` | Ranked alternative hypotheses from `VoxrResult.Alternatives`. Hidden message until `Max Alternatives > 0`. |
| `Canvas/ErrorText` | Hidden by default; shown for ~4 s on `OnError`. |

## Notes

- The model extracts on first launch (a few seconds). Subsequent launches use the cached model.
- Partial results update in real time while you speak. Final results appear at utterance boundaries.
- The sample uses legacy `UnityEngine.UI.Text`. To switch to TextMeshPro, replace the `Text` references with `TMP_Text` in `VoiceDemo.cs` and re-wire the inspector.

## See Also

- [Getting Started](../../Documentation~/getting-started.md) -- installation, model setup, lifecycle
- [SpeechRecogniser API](../../Documentation~/api/speech-recogniser.md) -- full event and method reference
