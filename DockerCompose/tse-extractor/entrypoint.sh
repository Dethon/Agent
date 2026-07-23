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
exec python /opt/tse/app.py
