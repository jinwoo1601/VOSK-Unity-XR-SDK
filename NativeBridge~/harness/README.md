# Tier C WSL Harness — WAV → transcript regression on the desktop bridge

Replays the committed fixture corpus (`Tests~/Fixtures/audio/tts/`) through the **real native bridge** (ring buffer → downsampler → AGC → int16 → Vosk) compiled for desktop Linux, and compares the final transcripts against the committed baseline in `expectations.json`. Runs entirely in WSL — no Unity, no device. Design contract: `Planning~/design-docs/automated-verification.md` §7 (Tier C).

What it verifies: bridge *logic* on a real Linux libvosk. What it is blind to: the arm64 binary, JNI AudioRecord capture, and Quest audio routing (Tier D owns those).

## One-time provisioning

Everything lands in `NativeBridge~/vendor/` (gitignored, never committed).

1. **Toolchain** (Ubuntu WSL): `sudo apt install -y build-essential cmake ninja-build` — needs gcc/g++ ≥ C++17 and CMake ≥ 3.21 (presets; Ubuntu 24.04 ships 3.28).
2. **libvosk** — Alphacephei prebuilt, pinned **0.3.45** (must match `expectations.json`'s `libvosk` pin; a different version is a conscious re-baseline):
   ```bash
   cd NativeBridge~/vendor
   wget https://github.com/alphacep/vosk-api/releases/download/v0.3.45/vosk-linux-x86_64-0.3.45.zip
   unzip vosk-linux-x86_64-0.3.45.zip
   cp vosk-linux-x86_64-0.3.45/libvosk.so .
   ```
3. **Model** — the same small English model the package uses on-device, `vosk-model-small-en-us-0.15`. Either unzip the copy already in the host test project (`VoXR TestGround/Assets/StreamingAssets/vosk-model-small-en-us-0.15.zip`) into `vendor/`, or download it from <https://alphacephei.com/vosk/models>:
   ```bash
   cd NativeBridge~/vendor
   wget https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip
   unzip vosk-model-small-en-us-0.15.zip
   ```

## Build

```bash
cd NativeBridge~
cmake --preset desktop-linux
cmake --build build-desktop
```

The preset selects the **stub** capture backend (`VOSK_BRIDGE_CAPTURE=stub` — no JNI, no Android anywhere) and builds the harness (`VOSK_BRIDGE_BUILD_HARNESS=ON`). The Android build commands in the project CLAUDE.md are untouched by any of this.

## Run

```bash
cd NativeBridge~
./build-desktop/harness/vosk-bridge-harness \
  --model vendor/vosk-model-small-en-us-0.15 \
  --fixtures "../Tests~/Fixtures/audio/tts" \
  --manifest harness/expectations.json
```

Output: a JSON report (per-fixture expected/actual/pass plus a summary). Exit codes: **0** = all fixtures match the baseline; **1** = at least one transcript mismatch; **2** = operational error (unreadable model/manifest/WAV, bridge error, `{"error": ...}` result entries such as ring overflow).

Fixture WAVs must be 48 kHz mono 16-bit PCM (the corpus format) — anything else is rejected naming the actual and required format.

## The baseline (`expectations.json`)

`expectedFinals` per fixture are the **raw bridge-level transcripts** (Vosk `[unk]` tokens included), which deliberately differ from the parser-level expectations in `Tests~/Fixtures/audio/manifest.json` — see the `_comment` in the file. The grammar (verbatim `GenerateGrammarJson` output), AGC gain (−18 dB, the device value), and libvosk version are pinned in-file because the baseline is only meaningful under the exact decode regime that produced it.

**Re-baselining** (after corpus regeneration or a libvosk bump) is a conscious, reviewed step, per the Tier B F7-amendment policy:

```bash
./build-desktop/harness/vosk-bridge-harness --model ... --fixtures ... \
  --manifest harness/expectations.json --write-baseline
```

then review the `expectations.json` diff against the corpus manifest's intents before committing.

## Notes

- The harness pushes faster than real time; verdicts are timing-independent (Vosk endpointing consumes sample time, and full consumption before stop is guaranteed by pushing ring-capacity-plus-one-second of trailing silence).
- `VOSK_BRIDGE_CAPTURE=aaudio` exists as an option value but the AAudio backend is unmaintained legacy (broken on Quest, predates the AudioRecord fix) — selecting it is not verified and may not compile.
- `third_party/nlohmann/json.hpp` is the vendored single-header nlohmann/json v3.11.3 (MIT). `NativeBridge~/` (harness included) is stripped from the published package by `release.yml`.
