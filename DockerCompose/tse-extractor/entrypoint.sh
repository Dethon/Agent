#!/bin/sh
set -e
CKPT_DIR="${MODEL_DIR:-/models/wesep-english}"
if [ ! -f "$CKPT_DIR/avg_model.pt" ] || [ ! -f "$CKPT_DIR/config.yaml" ]; then
    echo "provisioning bsrnn_ecapa_vox1 checkpoint into $CKPT_DIR"
    # Stage in a scratch dir and move into place only after download AND extraction fully
    # succeeded: extracting straight into CKPT_DIR let a truncated archive land the two
    # guard files above, which skips provisioning forever and crash-loops the app on the
    # broken checkpoint. config.yaml moves last so a crash mid-move cannot satisfy the guard.
    TMP="$CKPT_DIR/.provisioning"
    rm -rf "$TMP"
    mkdir -p "$TMP"
    curl -fSL --retry 3 -o "$TMP/ckpt.tar.gz" \
        "https://www.modelscope.cn/datasets/wenet/wesep_pretrained_models/resolve/master/bsrnn_ecapa_vox1.tar.gz"
    tar xzf "$TMP/ckpt.tar.gz" -C "$TMP"
    rm "$TMP/ckpt.tar.gz"
    for f in "$TMP"/*; do
        [ "$(basename "$f")" = "config.yaml" ] || mv -f "$f" "$CKPT_DIR/"
    done
    mv -f "$TMP/config.yaml" "$CKPT_DIR/"
    rm -rf "$TMP"
fi
# gunicorn pinned to 1 worker: every worker imports app.py and loads its own copy of the
# checkpoint, so scaling workers multiplies memory for a sidecar whose extractions serialize
# under an in-app lock anyway; the thread pool keeps /health responsive during a long
# /extract. --timeout guards worker BOOT, not requests (gthread heartbeats while requests
# run): the import loads the checkpoint and may export the ONNX core -- minutes on a Pi --
# and the 30s default would kill the worker mid-boot and crash-loop. --no-control-socket:
# unused, and its default path ($HOME/.gunicorn) is unwritable for the non-root PUID this
# service runs as -- it ERRORs at every boot otherwise.
exec gunicorn --chdir /opt/tse --workers 1 --threads 4 --timeout 600 \
    --bind 0.0.0.0:9098 --access-logfile - --no-control-socket app:app
