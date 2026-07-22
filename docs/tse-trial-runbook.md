# TSE Live-Trial Runbook

Spec: `docs/superpowers/specs/2026-07-22-tse-live-integration-design.md`. Everything here is
config-only; no code changes during the trial.

## Prerequisites — read before step 1

TSE only ever fires on a turn where the speaker-identity gate **accepts and names** a
speaker (`TranscriptionOptionsFactory.Create` sets `TargetSpeaker` only when
`SpeakerVerification.Decision == Accepted`, from `IdentifiedSpeaker ?? BestMatch`; every other
decision — `Skipped`, `Rejected`, `Unavailable` — leaves it `null`). Two things must be true
on the deployment host or every turn will skip and the trial will read as "the sidecar never
fired":

1. **The gate must be enabled.** `McpChannelVoice/appsettings.json` ships
   `SpeakerVerification.Enabled: false`. Turn it on in `DockerCompose/.env` before the trial —
   either globally (`SpeakerVerification__Enabled=true`) or per satellite
   (`Satellites__<id>__Verification__Enabled=true`, `McpChannelVoice/Settings/SatelliteConfig.cs`).
   `.env` is loaded wholesale into the container via `env_file:` in
   `docker-compose.yml`, so no compose edit is needed for this key — recreate
   `mcp-channel-voice` the same way as step 3 below to pick it up.
2. **At least one enrolled speaker must exist.** `voices/<name>/enroll-*.wav`, written by
   `scripts/enroll-voice.sh` — both the gate's `SpeakerProfileStore`
   (`SpeakerVerification.VoicesPath`, default `/voices`) and the sidecar's `app.py` scan the
   same `enroll-*.wav` glob under the shared `./volumes/voices` mount.

**Reading `TseSkipped`:** outcome `no_speaker` means the gate did not accept-and-name a
speaker on that turn — it is not a noise/quiet signal (that's the separate `quiet` outcome,
gated on `Tse__NoiseFloorThreshold` in `Auto` mode). This includes short first turns: the gate
skips verification below `SpeakerVerification.MinVerifySpeechMs` (default 800ms,
`SpeakerVerifier.VerifyAsync`), so a brief opening utterance reports `no_speaker` even with
the gate correctly enabled and the speaker correctly enrolled — don't misread the early turns
of a session as a broken gate.

## Enable on a deployment (pi5 today, AI 370 later)

1. Bring the sidecar up (build + start `tse-extractor` — nothing else depends on it, so it
   won't come up as a side effect of starting `mcp-channel-voice`):
   ```
   docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d --build tse-extractor
   ```
   (swap the `.linux.yml` override for `.windows.yml` on a Windows host, matching the launch
   commands in the main `CLAUDE.md`). `docker logs tse-extractor` shows the checkpoint
   download (only the first time `./volumes/tse-models` is empty) followed by Flask binding
   (`Running on http://0.0.0.0:9098`). `curl http://localhost:9098/health` (run on the
   deployment host — the compose service publishes port 9098) lists enrolled speakers, e.g.
   `{"speakers":[],"status":"ready"}` (empty until the Prerequisites enrollment step above is
   done).
2. On the deployment host, edit `DockerCompose/.env` — the same per-host override mechanism
   already used for `Satellites__*` — and add:
   - `Tse__Mode=Auto` (or `Always` for a diagnostic session)
   - `Tse__AuditDir=/tse-audit` to opt into the audio audit ring. **The container must be able
     to write the host directory.** The hub runs as `PUID:PGID` — the same `.env` pair the
     other volume-writing services (qbittorrent, jackett, plex, …) already use (default
     `1654:1654`, the aspnet image's app user). Hosts that set them once are covered; otherwise
     set both to the owner of your volumes (usually `id -u` / `id -g`) and pre-create the
     directory as yourself so Docker doesn't auto-create it root-owned:
     ```
     echo "PUID=$(id -u)" >> DockerCompose/.env
     echo "PGID=$(id -g)" >> DockerCompose/.env
     mkdir -p DockerCompose/volumes/tse-audit
     ```
     An unwritable directory makes every `TseAuditTrail.Record` call throw
     `UnauthorizedAccessException` — caught and logged as a warning by design (audit failures
     must never affect a turn), which silently produces an empty audit directory for the whole
     trial instead of a loud error. If Docker already created the directory root-owned, remove
     it (or `sudo chown` it once) before restarting. The same knob lets the hub write its
     `profile.json` speaker-embedding cache into `volumes/voices`, skipping re-embedding of
     every enrollment WAV on each boot.
   - pi5 only: consider `Tse__TimeoutMs=90000` (already the shipped default — only add this
     line if tuning away from it). Extraction on Pi 5 CPU uses the ONNX-compiled core by
     default (`TSE_ONNX=1`, interpolated in docker-compose.yml); set `TSE_ONNX=0` in
     `DockerCompose/.env` + `up -d tse-extractor` to fallback to eager torch if needed.
     Measured on an AMD 5900X: ~1.7 s per 8 s capture warm (vs ~2.8 s eager); HX 370 projected
     ~1.2–1.6 s; measure on Pi 5 in the field for your configuration. The first container start
     after this change exports the ONNX core into tse-models (expect ~1–2 min) and self-verifies
     (`exporting bsrnn core` and `onnx core parity vs eager` in logs); any export or verify
     failure falls back to eager automatically with an error log — extraction never breaks.
     Per-speaker embeddings cache after first extraction, so the first turn for each speaker is
     ~0.3–0.5 s slower than steady state. `Auto` keeps quiet turns fast by never invoking the
     sidecar below the noise floor. `TSE_TORCH_THREADS` pins the torch thread count (0 =
     physical core count, auto-detected if unset).

   `DockerCompose/docker-compose.yml`'s `mcp-channel-voice` service interpolates all five
   `Tse__*` keys (`Mode`, `Endpoint`, `TimeoutMs`, `NoiseFloorThreshold`, `AuditDir`) from the
   environment with the shipped defaults as fallback (`Tse__Mode: "${Tse__Mode:-Off}"` etc.),
   so a `.env` line here now actually reaches the container.
3. Apply the change — a plain `docker restart mcp-channel-voice` does **not** pick up a
   compose-file or `.env` edit, only a recreate does:
   ```
   docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d mcp-channel-voice
   ```
   (swap the `.linux.yml` override for `.windows.yml` on a Windows host, per the main
   `CLAUDE.md` launch commands). This matters for every `Tse__*` key, not just `Mode`: all of
   them are bound once into `TseSpeechToText.Wrap(...)` when the STT decorator is built
   (`McpChannelVoice/Modules/ConfigModule.cs`) — `Off` isn't even wrapped — and are never
   re-read per turn.

   **Kill switch:** set `Tse__Mode=Off` in `DockerCompose/.env` (or delete the line — `Off` is
   the compose file's own fallback) and re-run the same `up -d mcp-channel-voice` command.

## Moving extraction off-box (AI 370 or any remote sidecar)

`Tse__Endpoint` (default `http://tse-extractor:9098`) is env-overridable like the other
`Tse__*` keys, so pointing the hub at a different box is a one-line `.env` change:
`Tse__Endpoint=http://<remote-host>:9098`, then recreate `mcp-channel-voice` as in step 3. The
remote host must independently mount the same `voices/` enrollments the hub uses (they are
not shared automatically once the sidecar leaves the compose network) — copy or sync
`./volumes/voices` to wherever the remote `tse-extractor` mounts `/voices`.

## Calibrate `Tse__NoiseFloorThreshold`

`FloorRms` is published on the existing `UtteranceRejected`/`UtteranceTranscribed` voice
metrics and on every `Tse*` event. Pull recent values from the dashboard (voice metrics) or
Redis and pick a threshold between the quiet-room band and the TV-on band. Default 400 is
provisional. Too many `TseSkipped/quiet` on TV turns → lower it; extractions firing in
silence → raise it. Apply a new value the same way as Enable above: set
`Tse__NoiseFloorThreshold=<value>` in `DockerCompose/.env`, then `docker compose ... up -d
mcp-channel-voice` — this is a startup-bound setting too, not hot-reloaded.

## Watch during the trial

- Dashboard voice metrics: `TseInvoked` / `TseSkipped` (Outcome quiet|no_speaker) /
  `TseFailed` (Outcome unavailable|malformed) counts, `TseLatencyMs` distribution (this IS
  the deployability number).
- Audit pairs under `DockerCompose/volumes/tse-audit/` — listen to mixture vs extracted for
  a few TV-heavy turns (`scp` them off the pi; newest 50 kept).
- `docker logs mcp-channel-voice` — the decorator logs a warning (with the speaker) only when
  the sidecar call itself fails: unavailable/timeout or a malformed reply. Skipped turns (no
  target speaker, floor below threshold) are not logged — they only show up as `TseSkipped`
  in the metrics.
- Gate behavior in noise (observational, feeds the v2 reverify question): rejected-utterance
  metrics vs floor level.

## Readout (spec §Trial Readout)

1. `TseLatencyMs` on pi5, later AI 370.
2. Invoked-turn transcript quality (audit pairs + daily use).
3. Quiet-path check: skip rate ≈ 100 % in quiet scenes, latency unchanged there.
4. Gate accept/identify vs floor (observational).

Kill switch: `Tse__Mode=Off` in `DockerCompose/.env` + `docker compose ... up -d
mcp-channel-voice` (step 3 above — a restart alone does not apply it). Worst case during the
trial: slower noisy turns; quiet turns and all failures fall back to today's raw path.
