#!/usr/bin/env bash
# =============================================================================
# Purpose:  Bootstrap the TTS toolchain and regenerate the fixture corpus
# Layer:    Tests.Fixtures
# Owns:     (script, no public types)
# Depends:  python3 (everything else is venv-installed: piper-tts, soxr, numpy)
# =============================================================================
# Usage: ./generate.sh              # regenerate the whole corpus
#        ./generate.sh --only NAME  # regenerate entries whose file matches NAME
# Runs anywhere with python3 + network (WSL verified). Generated WAVs are
# committed, so running this is only needed to add or change fixtures.
set -euo pipefail
cd "$(dirname "$0")"

# Toolchain lives in the user cache, NOT inside the fixtures tree — Unity
# imports this folder during host-project test runs.
CACHE="${XDG_CACHE_HOME:-$HOME/.cache}/voxr-fixture-gen"
VENV="$CACHE/venv"
VOICE=en_US-lessac-medium
DATA_DIR="$CACHE/voices"

if [ ! -x "$VENV/bin/piper" ]; then
    echo "Bootstrapping venv (piper-tts + soxr + numpy)..."
    python3 -m venv "$VENV"
    "$VENV/bin/pip" install --quiet "piper-tts==1.6.0" "soxr==1.1.0" numpy
fi

if [ ! -e "$DATA_DIR/$VOICE.onnx" ]; then
    echo "Downloading voice $VOICE..."
    mkdir -p "$DATA_DIR"
    "$VENV/bin/python" -m piper.download_voices "$VOICE" --data-dir "$DATA_DIR"
fi

"$VENV/bin/python" generate.py \
    --manifest audio/manifest.json \
    --fixtures-root . \
    --voice "$VOICE" \
    --data-dir "$DATA_DIR" \
    --piper-bin "$VENV/bin/piper" \
    "$@"
