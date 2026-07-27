# Multi-Satellite Wake Arbitration ("satellite attention")

**Date:** 2026-07-27
**Status:** Implemented (see docs/superpowers/plans/2026-07-27-wake-arbitration.md)

## Problem

With multiple satellites deployed, several can hear the same "ok nabu" + command. Today
nothing dedups: each satellite's connection independently wakes, captures, transcribes, and
dispatches — producing two conversations, two agent turns, and two spoken replies for one
utterance. The system needs exactly one satellite to process each utterance, ideally the one
closest to the user.

There are two collision scenarios, both in scope:

1. **Coincident fresh wakes** — two or more satellites detect the wake word within a few
   hundred milliseconds of each other.
2. **Wake vs. open capture** — satellite H is mid-conversation (initial or follow-up capture
   open, mic live) and the user says "ok nabu …" again. H's open capture ingests it as
   follow-up speech while satellite S treats it as a fresh wake. Both would process it.
   The same signal must NOT suppress a genuinely independent wake (a second person using
   their own satellite while the first conversation is open — normal in this household).

## Decision summary (user-approved)

- **Win signal:** loudest wake word — satellite-measured RMS, hub-compared. Not first-wins,
  not hub-side command RMS.
- **Loser UX:** the awake chirp keeps playing instantly on every satellite that wakes
  (zero-latency acknowledgment); losers then go **silent** — no done-cue, LED to idle.
- **Scope:** both scenarios above, now.
- **Handoff:** when a fresh wake steals from an open conversation, the conversation
  **follows the user** — the winning satellite re-binds to the existing conversation id.

## Mental model

A wake event is evidence of an utterance. The arbiter asks: did any other satellite hear the
same utterance (as a coincident wake, or as speech landing in an already-open capture)? If
so, the loudest calibrated mic wins; everyone else stands down silently. Independent
utterances (no acoustic coincidence) never interfere with each other.

## 1. Wire protocol & satellite changes (`satellite/` Rust crate)

### `run-pipeline` data payload

Today the satellite sends `run-pipeline` with an empty data object and the hub reads no
fields, so adding fields is forward- and backward-compatible in both directions.

| Field | Type | Meaning |
|-------|------|---------|
| `wake_rms` | f32 | RMS over the pre-roll ring **excluding** the trailing `wake_preroll_chunks` (3-chunk ≈ 240 ms detection gap) — i.e. the ~800 ms containing the wake word. Computed at fire time; the ring still holds the full word because trimming happens after detection (`state_machine.rs` `trim_preroll`). Units: i16 amplitude RMS, the same scale as the hub's `SilenceGate.Rms`, so satellite- and hub-side numbers are directly comparable. |
| `wake_score` | f32 | Classifier score at fire, surfaced from `WakeDetector::push_chunk` (computed at `detector.rs:141`, currently dropped — return type changes from `bool` to an option carrying score). Observability only; not used in the decision (score saturates near 1.0 and tracks SNR non-linearly). |
| `source` | string | `"wake"` or `"button"`. Button presses are deliberate physical intent: button claims are never suppressed and beat non-button claims. |

### New hub→satellite event: `pause-satellite`

Semantics (name matches the real Wyoming protocol's event of the same purpose):

- In `Mode::Streaming`: stop streaming, `detector.reset()` (wake re-armed), **no cue**,
  LED → Idle, ack with `audio-stop` (symmetric with the `transcript` path; hub uses the ack
  for bookkeeping only).
- In `Mode::Idle`: no-op.

Old satellites warn-ignore unknown event types (`state_machine.rs:253`), so shipping the hub
first cannot wedge them **only if** the hub detects old firmware: a claim without `wake_rms`
identifies a pre-arbitration satellite, and losers on old firmware get the empty
`transcript` abort instead (done-cue plays — degraded but correct; the satellite never
streams forever). The same legacy abort hits a new-firmware satellite whose first turn on a
connection was a button press (no `wake_rms` seen yet). Either way the transcript arm also
lights the Thinking LED, and since no reply ever follows an abort, the LED sits there until
the satellite's own 120 s Thinking fallback clears it.

`PROTOCOL_VERSION` bumps `1.2` → `1.3` (documentation; the codec ignores it). The
data-once-as-body wire format pin is untouched.

### Satellite invariants respected

- The awake cue still plays inside `start_turn` before any hub round-trip (instant chirp).
- The zero-lag pre-roll flush contract is unchanged — RMS is computed from the ring before
  the existing trim/flush, adding no latency and no protocol reordering.
- `pause-satellite` handling is a pure event-handler branch: no new compound I/O in the
  `select!` loop (pump-task cancellation-safety invariant).

## 2. Hub: `WakeArbiter` (`McpChannelVoice/Services`)

A new singleton, registered in `ConfigModule`, consulted synchronously on the wake path —
the same pattern as `ActiveAlertRegistry`, which `WyomingSatelliteHost.cs:187` already calls
there. The read loop calls `arbiter.Claim(satelliteId, wakeRms, wakeScore, source)` for
`run-pipeline`/`audio-start` before `coordinator.OnWake()`.

**Every claimant still opens its capture immediately** — no audio is lost whichever
satellite wins, and no decision ever delays the winner: dispatch happens seconds later at
utterance end, while arbitration resolves within the window.

### Definitions

- `calibratedRms = rms × 10^(RmsOffsetDb/20)` — per-satellite calibration knob.
- Wake-word span for a claim received at hub time `T_rx`:
  `[T_rx − DetectionLatencyMs − WakeWordDurationMs, T_rx − DetectionLatencyMs]`
  (detection latency ~181 ms is measured and pinned in `satellite/src/config.rs`).

### Rule A — coincident fresh wakes

The first claim opens a collection window (`WindowMs`, default 500 ms); later claims join
it. At window close:

- Winner: highest `calibratedRms`. Missing `wake_rms` (old firmware) = −∞. Full tie →
  earliest arrival. `source: "button"` beats any non-button claim.
- Losers: capture closed with a new `arbitration` end-reason; their `FollowUpConversation`
  exits **without dispatch and without its own transcript write**; the arbiter sends
  `pause-satellite` (or `transcript` for old firmware). Metric emitted per loser.
- Bypass: when only one satellite is connected (`SatelliteSessionRegistry` count), no window
  is opened at all.

The window delays nothing user-visible; its only cost is grouping — two genuinely
independent wakes within 500 ms would be falsely arbitrated, which is vanishingly rare in a
household.

### Rule B — Rule-A winner vs. open captures (leak / handoff / two-person discriminator)

Runs at window close for the Rule-A winner S against every other satellite H whose capture
is currently open (initial or follow-up). Requires H's recent per-chunk history (see §3).
Examine H's chunks over the wake-word span ± `AlignSlackMs`:

- **Aligned onset** — H shows a speech *onset* inside the span (a speech-classified chunk
  preceded by ≥ `QuietGapMs` of non-speech, or a capture opened in-span with immediate
  speech) → same utterance:
  - `calibratedRms(S) ≥ calibratedRms(H_span_peak) × 10^(StealMarginDb/20)`, where
    `H_span_peak` is H's maximum per-chunk RMS inside the span → **handoff**:
    H's turn is abandoned (no dispatch), H gets `pause-satellite`, S takes over the
    conversation (§4). The margin makes ties favor the incumbent.
  - Otherwise → **S suppressed**: the wake word leaked into H's open mic; S (and all Rule-A
    candidates) get `pause-satellite`. H's transcript will contain "ok nabu …" leading text,
    which STT/the agent already tolerate today.
  - S on old firmware (no `wake_rms`): never steals; aligned → suppressed.
- **Not aligned** — H silent across the span, or H already mid-speech since *before* the
  span (a different speaker talking to H) → independent utterances → both proceed untouched.
  This is what keeps a second person's satellite responsive while the first person's
  conversation is open.

Multiple open-capture holders: evaluate S against the loudest aligned holder.

Every open mic is a holder, including the approval-answer capture (`RequestApprovalTool`),
not just conversation turns. An approval capture that loses a steal resolves the approval
as rejected without transcribing the partial audio and without re-prompting — the arbiter
already re-armed that satellite via `pause-satellite`, so no one is listening there.

A handoff can go stale: loser re-arms run before Rule B and a wedged socket costs up to 2 s
each, so with 3+ satellites the steal can land after the winner's short turn already
dispatched and bound its own conversation — with the agent's reply to it still in flight.
`TransferBinding` therefore declines to displace a winner binding newer than the wake
claim: the reply still routes, the holder (whose capture was already aborted as a leak)
gets its silent re-arm and its conversation just idle-expires, and the event is recorded
as `WakeSuppressed`/`stale_steal` on the holder instead of a `WakeHandoff` that never
happened.

Rule-A claimants and Rule-B holders are disjoint by construction: a satellite with an open
capture is in `Mode::Streaming`, where its wake detector is not fed, so it cannot also emit
a fresh `run-pipeline` claim.

### Concurrency

Claims arrive on different per-connection read-loop tasks → the arbiter locks internally
(like `ActiveAlertRegistry`). The window decision fires on a timer continuation, not on any
read loop. Coordinator handles (`AbandonTurnAsync`, pause/transcript writers, the session)
are registered with the arbiter when `BuildCoordinator` runs and unregistered on disconnect
— today the coordinator is a local in `RunConnectionAsync`, unreachable from outside.

## 3. Capture chunk history

`SilenceGate` already computes per-chunk RMS and speech classification; it gains a bounded
ring (~2.5 s) of `(arrivalTimestamp, rms, isSpeech)` entries, exposed through
`UtteranceCapture` → `SatelliteSession` so the arbiter can read another session's recent
acoustic activity. Hub arrival timestamps are the common timeline (satellites have no
synchronized clocks); LAN jitter is absorbed by `AlignSlackMs`.

## 4. Conversation handoff

`VoiceConversationManager.TransferBinding(fromSatelliteId, toSatelliteId)`: under the
existing lock, the entry (conversationId + idle timer) moves from H's key to S's. If S had
its own idle conversation entry, it is dropped (it would have idle-expired anyway). Reply
routing needs no change: delivery already follows the per-message `SatelliteId`, so S's
dispatched message pulls the spoken reply to S. After S's turn, S opens the follow-up window
as the new holder; H is idle with wake re-armed.

Stated limitations (not solved here):

- The WebChat topic name remains "{Identity} @ {H's room}" after handoff (topic rename out
  of scope).
- Approval routing remains group-bound (pre-existing deferred limitation, unchanged).

## 5. Edge cases

- **Winner disconnects mid-window** → next-loudest candidate; all candidates dead → window
  dissolves, no action.
- **Wake vs. open *initial* capture** (not just follow-up): identical Rule B treatment.
- **Wake after H's capture completed** (in STT/dispatch): treated as independent. The
  same-utterance case cannot realistically hit this: a co-heard wake fires ~181 ms after the
  word ends, while H's endpoint needs ≥ 800 ms of trailing silence, so the wake always
  arrives before H dispatches.
- **TV/music near H**: can fake an aligned onset and wrongly suppress S when H measures
  louder. Accepted risk: the ± `AlignSlackMs` window keeps probability low, the failure mode
  is a retry, and the speaker-verification gate already covers TV downstream.
- **Button turns**: `source: "button"` claims are never suppressed by Rule B and win Rule A.
- **Hub restart**: arbiter state is in-memory only; connections re-dial, satellites reset to
  Idle. Nothing persists.

## 6. Configuration

New `Arbitration` section on `VoiceSettings` (all non-secret → `appsettings.json` skeleton
ships in the same change; nothing for `.env`):

| Key | Default | Meaning |
|-----|---------|---------|
| `Enabled` | `true` | Kill switch. |
| `WindowMs` | `500` | Rule-A collection window. |
| `StealMarginDb` | `6` | Loudness advantage required to steal an open conversation. |
| `DetectionLatencyMs` | `181` | Wake-word span reconstruction (advanced). |
| `WakeWordDurationMs` | `700` | Wake-word span reconstruction (advanced). |
| `AlignSlackMs` | `250` | Onset-alignment tolerance (advanced). |
| `QuietGapMs` | `400` | Quiet run required before a chunk counts as an onset (advanced). |

Per-satellite: `RmsOffsetDb` (default `0.0`) on `SatelliteConfig`, env path
`Satellites__<id>__RmsOffsetDb`.

No new satellite CLI flags: wake RMS is always computed (cheap) and `pause-satellite`
handling is unconditional.

## 7. Observability

- New **explicitly pinned** `VoiceMetric` values (pinned-int rule, guarded by
  `VoiceEnumsTests`): `WakeSuppressed`, `WakeHandoff` — emitted with existing
  `SatelliteId`/`Room`/`Outcome` dimensions.
- `wake_rms` and `wake_score` ride as optional members on the `WakeTriggered` `VoiceEvent`
  so cross-satellite RMS calibration data accumulates from day one.
- Dashboard charts: out of scope v1; the data will be present.

## 8. Validation caveat (field measurement required)

The XVF3800's processed capture path may AGC-compress level differences between near and far
speech, which would flatten the arbitration signal. The `WakeTriggered` metrics deliver real
cross-office `wake_rms` deltas within days of deployment. If AGC flattens them,
`RmsOffsetDb` cannot fix it and the decision signal needs revisiting (score or first-wins
fallback) — measure before concluding.

## 9. Testing (TDD, red-green-refactor)

- **Rust units**: `push_chunk` surfaces the score; `start_turn` emits `run-pipeline` with
  `wake_rms`/`wake_score`/`source` (button turns stamp `"button"`); ring-RMS excludes the
  detection gap; `pause-satellite` handling (Streaming → Idle, detector reset, no cue,
  `audio-stop` ack; Idle no-op); codec round-trip for the new fields. qemu smoke
  (`--no-wake`) unaffected.
- **Hub units** (`Tests/Unit/McpChannelVoice`): `WakeArbiter` rule table — Rule-A winner by
  calibrated RMS, tie → earliest, missing RMS → −∞, button precedence, single-satellite
  bypass; Rule B aligned / not-aligned / steal-margin / suppress against synthetic chunk
  histories; `SilenceGate` history-ring bounds; `FollowUpConversation` abandon path (no
  dispatch, no transcript write); `VoiceConversationManager.TransferBinding`; old-firmware
  `transcript` fallback; pinned-enum guard extended.
- **Integration**: two in-process fake Wyoming satellite servers against the real hub —
  coincident wakes → exactly one dispatch and one `pause-satellite`; handoff scenario →
  conversation id survives the satellite switch.
- **Manual bed**: the WSL two-satellite dev setup (fran-office-01 @10700,
  laura-office-01 @10600).
- **Field**: read cross-office `wake_rms` deltas from metrics; set `RmsOffsetDb` if units
  differ; confirm §8.

## 10. Rollout

1. Satellite binary + provisioning (new fields harmless to the old hub).
2. Hub (`mcp-channel-voice`).
3. Watch `WakeSuppressed`/`WakeHandoff` and `wake_rms` metrics; calibrate `RmsOffsetDb`.

`Arbitration.Enabled=false` is the kill switch at every stage.
