FROM python:3.11-slim

RUN apt-get update \
    && apt-get install -y --no-install-recommends git curl libsndfile1 \
    && rm -rf /var/lib/apt/lists/*

# torch first (CPU wheels; index serves x86_64 and aarch64), then the wesep/wespeaker
# import tree. torch/torchaudio pinned to 2.1.2 -- the version proven against
# wespeaker@e9bbf73 by the round-1 eval harness (tse_env_setup.sh). This is load-bearing,
# not cosmetic: current torchaudio (verified 2.11.0 against torch 2.13.0+cpu, 2026-07-22)
# dropped its built-in soundfile/sox backend dispatch (torchaudio.list_audio_backends is
# gone) and torchaudio.load() now hard-requires the separate torchcodec package, which
# isn't installed here -- wesep's Extractor.extract_speech calls torchaudio.load()
# directly, so an unpinned torch/torchaudio 500s on every /extract call
# (ModuleNotFoundError: No module named 'torchcodec'). numpy<2 for wespeaker@e9bbf73
# compatibility. wespeaker --no-deps so it cannot drag a different torch or the heavy
# s3prl frontend HEAD added.
RUN pip install --no-cache-dir --index-url https://download.pytorch.org/whl/cpu "torch==2.1.2" "torchaudio==2.1.2" \
    && pip install --no-cache-dir "numpy<2" soundfile pyyaml scipy silero-vad tqdm \
        kaldiio hdbscan umap-learn scikit-learn onnxruntime requests flask \
    && pip install --no-cache-dir --no-deps \
        "wespeaker @ git+https://github.com/wenet-e2e/wespeaker.git@e9bbf73d0fd13db6cf42a6cb2eafb0d7dd0f8e0e"

RUN git clone https://github.com/wenet-e2e/wesep /opt/wesep-src \
    && git -C /opt/wesep-src checkout 99eca54b60300d39b9353d93cf285a14bba37854

COPY app.py entrypoint.sh /opt/tse/
RUN chmod +x /opt/tse/entrypoint.sh

ENV WESEP_SRC=/opt/wesep-src \
    MODEL_DIR=/models/wesep-english \
    VOICES_DIR=/voices

EXPOSE 9098
ENTRYPOINT ["/opt/tse/entrypoint.sh"]
