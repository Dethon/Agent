# TSE Live-Trial Runbook

Spec: `superpowers/specs/2026-07-22-tse-live-integration-design.md`. Everything here is
config-only; no code changes during the trial.

## Enable on a deployment (pi5 today, AI 370 later)

1. Deploy/update the stack so `tse-extractor` is running (`docker logs tse-extractor`
   shows the checkpoint provisioned and Flask bound; `curl :9098/health` lists speakers).
2. On `mcp-channel-voice`, set (compose env on the deployment host):
   - `Tse__Mode: "Auto"` (or `"Always"` for a diagnostic session)
   - `Tse__AuditDir: "/tse-audit"` to opt into the audio audit ring
   - pi5 only: consider `Tse__TimeoutMs: "90000"` (default) — extraction on Pi 5 CPU is
     expected to take tens of seconds; Auto keeps quiet turns fast.
3. Restart `mcp-channel-voice`. Mode changes always need a restart — `Mode` is read once
   when the STT decorator is built (`Off` isn't even wrapped), not re-evaluated per turn.

## Calibrate `Tse__NoiseFloorThreshold`

`FloorRms` is published on the existing `UtteranceRejected`/`UtteranceTranscribed` voice
metrics and on every `Tse*` event. Pull recent values from the dashboard (voice metrics) or
Redis and pick a threshold between the quiet-room band and the TV-on band. Default 400 is
provisional. Too many `TseSkipped/quiet` on TV turns → lower it; extractions firing in
silence → raise it.

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

Kill switch: `Tse__Mode: "Off"` + restart. Worst case during the trial: slower noisy turns;
quiet turns and all failures fall back to today's raw path.