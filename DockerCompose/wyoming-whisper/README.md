# Patched wyoming-whisper

Stock `wyoming-faster-whisper` discards every quality signal faster-whisper produces
(`avg_logprob`, `no_speech_prob`, `compression_ratio`) — its `Transcriber` contract
returns a bare string and the Wyoming `transcript` event carries only `text`/`language`.
The hub's confidence gate (`TranscriptDispatcher`, `VoiceSettings.ConfidenceThreshold`)
therefore never fires against a stock server.

This image overlays two files onto the digest-pinned upstream:

- `patch/faster_whisper_handler.py` — materializes the segment generator, computes
  duration-weighted `avg_logprob`/`no_speech_prob` + max `compression_ratio`, and
  returns `(text, stats)`. `score = exp(min(weighted_avg_logprob, 0))` ∈ (0, 1] —
  the hub's default threshold 0.4 ≈ whisper's canonical `logprob_threshold = -1.0`.
- `patch/dispatch_handler.py` — unpacks the tuple (tolerates bare-string backends)
  and attaches the stats as extra JSON keys on the `transcript` event. Standard
  Wyoming clients ignore extra keys; the hub reads them.

**Fail-open:** empty-audio / VAD-stripped / non-faster-whisper paths emit stock
text-only events; the hub treats a missing `score` as null confidence and passes
the transcript through — a broken patch can never kill the voice pipeline.

## Bumping the upstream image

1. `docker pull rhasspy/wyoming-whisper:latest` and note the new digest + version
   (`docker run --rm --entrypoint python3 rhasspy/wyoming-whisper:latest -c
   "from importlib.metadata import version; print(version('wyoming-faster-whisper'))"`).
2. Extract the new upstream `faster_whisper_handler.py`/`dispatch_handler.py` from the
   image and re-port the `PATCH(jackbot)` blocks onto them (they are full-file overlays).
3. Update the `FROM` digest, rebuild, and run `scripts/verify-whisper-score.py`
   (see its header for the docker invocation).
