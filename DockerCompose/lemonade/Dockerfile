# Lemonade Server: OpenAI-compatible STT (whisper.cpp on cpu or vulkan) + TTS (Kokoro).
# Replaces wyoming-whisper + wyoming-piper. The entrypoint maps STT_BACKEND (cpu|gpu) to
# the whisper.cpp device; both tiers run the same Whisper-Medium model.
# Pin: v11.0.0 (2026-07-15, current latest); bump deliberately, not via :latest.
FROM ghcr.io/lemonade-sdk/lemonade-server:v11.0.0

# The stock image has the Vulkan loader but no ICD, so ggml-vulkan would enumerate zero
# devices and fall back to CPU *without erroring*. RADV is what actually drives the 890M.
USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends mesa-vulkan-drivers \
    && rm -rf /var/lib/apt/lists/*
USER lemonade

COPY --chmod=755 entrypoint.sh /entrypoint.sh

ENTRYPOINT ["/entrypoint.sh"]