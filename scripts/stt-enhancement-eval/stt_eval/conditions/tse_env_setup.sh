#!/usr/bin/env bash
# Rebuild the isolated Target-Speaker-Extraction (TSE) stack for the `tse`
# condition from scratch, with the exact pins used in the Task 10 spike.
#
# WHY isolated: the WeSep stack (wesep + wespeaker + silero-vad) must not touch
# the pinned torch==2.1.2+cpu project venv that DFN3/gtcrn depend on. So it lives
# in its own gitignored venv under data/models/tse-env and is driven by
# stt_eval/conditions/tse.py as a persistent subprocess. Nothing here is added to
# pyproject.toml; everything lands under the gitignored data/models/.
#
# Usage (from the eval package root, or anywhere -- paths are resolved from $0):
#   bash stt_eval/conditions/tse_env_setup.sh
#
# Idempotent-ish: it removes and recreates data/models/tse-env, and skips the
# checkpoint/clone downloads if they already exist.
set -euo pipefail

# ---- pinned versions / sources (do not bump casually; see tse.py header) ----
TORCH_VER="2.1.2"                                               # cpu wheels, matches the project pin
WESPEAKER_COMMIT="e9bbf73d0fd13db6cf42a6cb2eafb0d7dd0f8e0e"     # the demo's pinned commit (light import tree)
WESEP_REPO="https://github.com/wenet-e2e/wesep"
WESEP_COMMIT="99eca54b60300d39b9353d93cf285a14bba37854"         # HEAD used in the spike
CKPT_URL="https://www.modelscope.cn/datasets/wenet/wesep_pretrained_models/resolve/master/bsrnn_ecapa_vox1.tar.gz"

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"   # -> scripts/stt-enhancement-eval
MODELS="$ROOT/data/models"
ENV_DIR="$MODELS/tse-env"
PY="$ENV_DIR/bin/python"
mkdir -p "$MODELS"

echo "==> [1/4] create tse-env venv (python 3.11)"
rm -rf "$ENV_DIR"
uv venv "$ENV_DIR" --python 3.11

echo "==> [2/4] install torch ${TORCH_VER}+cpu + runtime deps"
uv pip install --python "$PY" --index-url https://download.pytorch.org/whl/cpu \
  "torch==${TORCH_VER}" "torchaudio==${TORCH_VER}"
# scientific + wesep/wespeaker import-time deps (none pull torch); numpy<2 for the
# torch 2.1.2 ABI. wespeaker is pinned to the demo commit and installed --no-deps
# so it cannot drag in a newer torch or the heavy s3prl frontend that HEAD added.
uv pip install --python "$PY" \
  "numpy<2" soundfile pyyaml scipy silero-vad tqdm \
  kaldiio hdbscan umap-learn scikit-learn onnxruntime requests
uv pip install --python "$PY" --no-deps \
  "wespeaker @ git+${WESEP_REPO%wesep}wespeaker.git@${WESPEAKER_COMMIT}"

echo "==> [3/4] clone wesep source (added to sys.path by the worker, not pip-installed)"
if [ ! -d "$MODELS/wesep-src/.git" ]; then
  rm -rf "$MODELS/wesep-src"
  git clone "$WESEP_REPO" "$MODELS/wesep-src"
fi
git -C "$MODELS/wesep-src" checkout -q "$WESEP_COMMIT" 2>/dev/null || \
  echo "    (could not pin wesep-src to $WESEP_COMMIT; using current checkout)"

echo "==> [4/4] fetch + extract the bsrnn_ecapa_vox1 checkpoint (~262 MB)"
CKPT_DIR="$MODELS/wesep-english"
if [ ! -f "$CKPT_DIR/avg_model.pt" ] || [ ! -f "$CKPT_DIR/config.yaml" ]; then
  mkdir -p "$CKPT_DIR"
  curl -sSL -o "$CKPT_DIR/bsrnn_ecapa_vox1.tar.gz" "$CKPT_URL"
  tar xzf "$CKPT_DIR/bsrnn_ecapa_vox1.tar.gz" -C "$CKPT_DIR"
fi

echo "==> verify"
test -x "$PY"
test -f "$CKPT_DIR/avg_model.pt"
test -f "$CKPT_DIR/config.yaml"
test -d "$MODELS/wesep-src"
WESEP_SRC="$MODELS/wesep-src" "$PY" - <<'PYEOF'
import os
import sys
sys.path.insert(0, os.environ["WESEP_SRC"])
import torch
print("    torch", torch.__version__)
from wesep.cli.extractor import load_model_local  # noqa: F401 - smoke: import tree resolves
print("    wesep import OK")
PYEOF
echo "==> DONE. tse-env ready under $ENV_DIR"
