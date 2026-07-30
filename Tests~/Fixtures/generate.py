#!/usr/bin/env python3
# =============================================================================
# Purpose:  Synthesize the TTS fixture corpus from the phrase manifest
# Layer:    Tests.Fixtures
# Owns:     (script, no public types)
# Depends:  piper-tts, soxr, numpy (venv-installed by generate.sh)
# =============================================================================
# Pipeline per utterance entry (design §6.2): piper TTS -> resample to 48 kHz
# (soxr) -> peak-normalize into the Quest 3 mic range (0.04-0.4) -> pad lead/
# tail silence -> 16-bit mono WAV. Split-command entries synthesize segments
# and join them with a silence gap; silence entries emit digital silence.
# Piper synthesis is not bit-deterministic across runs (noise inputs), so
# regeneration is functionally reproducible, not byte-identical; the committed
# WAVs are the regression baseline.

import argparse
import json
import subprocess
import sys
import tempfile
import wave
from pathlib import Path

import numpy as np
import soxr

TARGET_RATE = 48000
LEAD_SILENCE_S = 0.3
TAIL_SILENCE_S = 1.0
DEFAULT_PEAK = 0.2
DEFAULT_GAP_S = 0.7
DEFAULT_SILENCE_S = 3.0
MIN_PEAK, MAX_PEAK = 0.04, 0.4


def synth(piper_bin, voice, data_dir, text, tmp_path):
    subprocess.run(
        [piper_bin, "-m", voice, "--data-dir", str(data_dir), "-f", str(tmp_path)],
        input=text.encode(), check=True, capture_output=True)
    with wave.open(str(tmp_path), "rb") as w:
        if w.getnchannels() != 1 or w.getsampwidth() != 2:
            sys.exit(f"piper produced unexpected format for '{text}'")
        rate = w.getframerate()
        data = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16)
    samples = data.astype(np.float32) / 32768.0
    if rate != TARGET_RATE:
        samples = soxr.resample(samples, rate, TARGET_RATE)
    return samples


def scale_to_peak(samples, peak):
    if not (MIN_PEAK <= peak <= MAX_PEAK):
        sys.exit(f"peak {peak} outside the Quest mic range [{MIN_PEAK}, {MAX_PEAK}]")
    m = float(np.max(np.abs(samples)))
    if m <= 0.0:
        sys.exit("cannot peak-scale silence — check the TTS output")
    return samples * (peak / m)


def silence(seconds):
    return np.zeros(int(seconds * TARGET_RATE), dtype=np.float32)


def build_case(case, piper_bin, voice, data_dir, tmp_path):
    peak = case.get("peak", DEFAULT_PEAK)
    if "segments" in case and case["segments"]:
        gap = case.get("gapSeconds", DEFAULT_GAP_S)
        parts = []
        for i, seg in enumerate(case["segments"]):
            if i > 0:
                parts.append(silence(gap))
            parts.append(scale_to_peak(
                synth(piper_bin, voice, data_dir, seg, tmp_path), peak))
        speech = np.concatenate(parts)
    elif case.get("phrase"):
        speech = scale_to_peak(
            synth(piper_bin, voice, data_dir, case["phrase"], tmp_path), peak)
    else:
        return silence(case.get("silenceSeconds", DEFAULT_SILENCE_S))

    return np.concatenate([silence(LEAD_SILENCE_S), speech, silence(TAIL_SILENCE_S)])


def write_wav(path, samples):
    samples = np.clip(samples, -1.0, 1.0)
    pcm = np.round(samples * 32767.0).astype(np.int16)
    path.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(TARGET_RATE)
        w.writeframes(pcm.tobytes())


def main():
    ap = argparse.ArgumentParser(description="Regenerate the TTS fixture corpus.")
    ap.add_argument("--manifest", required=True)
    ap.add_argument("--fixtures-root", required=True,
                    help="Directory the manifest 'file' paths are relative to")
    ap.add_argument("--voice", default="en_US-lessac-medium")
    ap.add_argument("--data-dir", required=True, help="piper voice data dir")
    ap.add_argument("--piper-bin", default="piper")
    ap.add_argument("--only", default=None,
                    help="generate only entries whose 'file' contains this substring")
    args = ap.parse_args()

    manifest = json.loads(Path(args.manifest).read_text())
    root = Path(args.fixtures_root)
    # Keep transients out of the fixtures tree — Unity imports it during host runs.
    tmp_path = Path(tempfile.gettempdir()) / "voxr_tts_tmp.wav"

    generated = 0
    try:
        for case in manifest["cases"]:
            rel = case["file"]
            if args.only and args.only not in rel:
                continue
            samples = build_case(case, args.piper_bin, args.voice,
                                 Path(args.data_dir), tmp_path)
            write_wav(root / rel, samples)
            peak = float(np.max(np.abs(samples)))
            print(f"  {rel}  ({len(samples) / TARGET_RATE:.2f}s, peak {peak:.3f})")
            generated += 1
    finally:
        tmp_path.unlink(missing_ok=True)

    if generated == 0:
        sys.exit(f"no manifest entry matched --only {args.only}")
    print(f"{generated} fixture(s) written under {root}")


if __name__ == "__main__":
    main()
