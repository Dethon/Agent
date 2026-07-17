# Patched wyoming-faster-whisper: emits transcription-quality stats (score, avg_logprob,
# no_speech_prob, compression_ratio) as extra JSON keys on the transcript event, so the
# hub's confidence gate (TranscriptDispatcher + VoiceSettings.ConfidenceThreshold) has a
# real signal. Stock servers discard these — see README.md for the rebase procedure.
# Pinned by digest: patch/ contains full-file overlays of the 3.5.0 sources.
FROM rhasspy/wyoming-whisper@sha256:308b7959a925d0cca381c3e6e77292fed87822695635db39c24199d6b7a9e610

COPY patch/faster_whisper_handler.py patch/dispatch_handler.py /tmp/patch/
# NOTE(jackbot): the base image's system python3 lacks the `wyoming` runtime dependency —
# only its venv (also what docker_run.sh execs at container start) has it, so both the
# copy and the post-copy import check must run under the venv interpreter.
RUN /usr/src/.venv/bin/python3 -c "import pathlib, shutil, wyoming_faster_whisper as m; pkg = pathlib.Path(m.__file__).parent; [shutil.copy(f'/tmp/patch/{n}', pkg / n) for n in ('faster_whisper_handler.py', 'dispatch_handler.py')]" \
    && rm -rf /tmp/patch \
    && /usr/src/.venv/bin/python3 -c "from wyoming_faster_whisper import dispatch_handler, faster_whisper_handler; print('patch applied')"
