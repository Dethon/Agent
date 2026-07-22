# TSE Live-Trial Runbook

Spec: `docs/superpowers/specs/2026-07-22-tse-live-integration-design.md`. Everything here is
config-only; no code changes during the trial.

## Enable on a deployment (pi5 today, AI 370 later)

1. Deploy/update the stack so `tse-extractor` is running. `docker logs tse-extractor` shows
   the checkpoint download (only the first time `./volumes/tse-models` is empty) followed by
   Flask binding (`Running on http://0.0.0.0:9098`). `curl http://localhost:9098/health` (run
   on the deployment host — the compose service publishes port 9098) lists enrolled speakers,
   e.g. `{"speakers":[],"status":"ready"}`.
2. On the deployment host, edit `DockerCompose/.env` — the same per-host override mechanism
   already used for `Satellites__*` — and add:
   - `Tse__Mode=Auto` (or `Always` for a diagnostic session)
   - `Tse__AuditDir=/tse-audit` to opt into the audio audit ring
   - pi5 only: consider `Tse__TimeoutMs=90000` (already the shipped default — only add this
     line if tuning away from it) — extraction on Pi 5 CPU is expected to take tens of
     seconds; `Auto` keeps quiet turns fast by never invoking the sidecar below the noise
     floor.

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
