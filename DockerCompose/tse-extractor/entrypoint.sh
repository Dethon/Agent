#!/bin/sh
set -e
CKPT_DIR="${MODEL_DIR:-/models/wesep-english}"
if [ ! -f "$CKPT_DIR/avg_model.pt" ] || [ ! -f "$CKPT_DIR/config.yaml" ]; then
    echo "provisioning bsrnn_ecapa_vox1 checkpoint into $CKPT_DIR"
    mkdir -p "$CKPT_DIR"
    curl -fSL --retry 3 -o "$CKPT_DIR/ckpt.tar.gz" \
        "https://www.modelscope.cn/datasets/wenet/wesep_pretrained_models/resolve/master/bsrnn_ecapa_vox1.tar.gz"
    tar xzf "$CKPT_DIR/ckpt.tar.gz" -C "$CKPT_DIR"
    rm "$CKPT_DIR/ckpt.tar.gz"
fi
exec python /opt/tse/app.py
