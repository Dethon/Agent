# Speaker Identification Routing (Per-Person Attribution)

**Date:** 2026-07-21 · **Branch:** `noise` · **Status:** Draft ·
Follow-up to `2026-07-21-speaker-identity-gate-design.md`

## Problem

The speaker-identity gate now blocks strangers (background TV) and admits the
household. But every admitted voice message is still attributed to the generic
`Sender = "household"` (`SatelliteConfig.Identity`). That `Sender` is what
`ChatMonitor` keys on for **memory recall and personalization**
(`ChatMonitor.cs:194,202` set the userId; `:71` builds the agent with it), so
the agent cannot tell one family member from another over voice.

The verifier already computes the best-match identity — the `voices/<name>/`
folder name — and its cosine similarity on every accepted capture. Today those
are used only for the accept/reject gate and telemetry, then discarded. This
was the deferred non-goal of the gate spec: *"routing the matched identity into
the conversation — the plumbing makes it possible later."*

## Goal

When the gate's match is **conclusive**, attribute the message to the
recognized person (the folder name) so memory/personalization is per-person.
When the match is **doubtful**, keep the default satellite identity
(`"household"`). Nothing else about gating, endpointing, STT, or the
early-close path changes.

## Design

Identification is a thin policy layer over the existing accept/reject result —
it only chooses the `Sender` string; it never changes whether a capture is
dispatched.

### Decision (in `SpeakerVerifier`, beside the existing accept/reject)

From the per-profile cosine similarities of a capture:

- `best` = highest-scoring profile (name + similarity).
- `runnerUp` = second-highest similarity, or *absent* when fewer than 2
  profiles are enrolled.
- The speaker is **identified** (verifier returns `IdentifiedSpeaker =
  best.Name`) iff **all** hold:
  1. `Decision == Accepted` (already `≥ SimilarityThreshold`), **and**
  2. `best.Similarity ≥ IdentifyThreshold`, **and**
  3. `runnerUp` absent, **or** `best.Similarity − runnerUp ≥ IdentifyMargin`.
- Otherwise `IdentifiedSpeaker = null`.

Because the default `IdentifyThreshold (0.65) ≥ SimilarityThreshold (0.45)`, the
band `[SimilarityThreshold, IdentifyThreshold)` is "accepted but doubtful" and
routes to `"household"`. The margin guard prevents naming person A when two
enrolled voices score near each other; with a single enrolled profile it
auto-passes, so identification still works in the current field state (only
`fran` enrolled).

`SpeakerVerification` gains one field:
`IdentifiedSpeaker (string?)`. `BestMatch`/`Similarity` are unchanged (still the
gate's best match, published as telemetry on both outcomes).

### Routing (hub + dispatcher)

`WyomingSatelliteHost.TranscribeAndDispatchAsync`: after the gate accepts,
```
var sender = verification.IdentifiedSpeaker ?? session.Config.Identity;
```
Thread `sender` through `TranscriptDispatcher.DispatchAsync` into
`ChannelNotificationEmitter.EmitMessageNotificationAsync`'s `sender` argument
(today it passes `session.Config.Identity` directly). `Skipped` / `Unavailable`
/ short-command / below-`IdentifyThreshold` / thin-margin captures all leave
`IdentifiedSpeaker == null` and fall back to `session.Config.Identity`.

Telemetry keeps `Identity = session.Config.Identity` (the satellite/room
dimension the dashboard groups by is unchanged). The identified person is **not**
a metric dimension in v1 — dashboards stay stable.

### Configuration

`SpeakerVerificationSettings` gains two knobs (non-secret → `appsettings.json`
only, field-tunable via the same `SPEAKERVERIFICATION__…` env override path the
gate threshold already uses):

| Setting | Default | Notes |
|---|---|---|
| `IdentifyThreshold` | `0.65` | Cosine bar to name the person; `≥ SimilarityThreshold`. |
| `IdentifyMargin` | `0.10` | Min `best − runnerUp` gap; skipped when `< 2` profiles. |

Per-satellite overrides in `VerificationOverrides { IdentifyThreshold?,
IdentifyMargin? }` with `SatelliteConfig.ResolveIdentifyThreshold` /
`ResolveIdentifyMargin`, mirroring the existing `SimilarityThreshold` /
`ResolveSimilarityThreshold` pattern.

### Defaults rationale (field data 2026-07-20)

Enrolled speaker: clear commands 0.60–0.92, over-loud-TV lows 0.50–0.60. TV
(survives endpointing): 0.38–0.51. `IdentifyThreshold 0.65` names clean speech
and stays `"household"` when the speaker's own score dips into the ambiguous
0.50–0.60 band — the safe default (attribute to a person only when sure).
`IdentifyMargin 0.10` is a conservative starting guard; it can only be tuned
from field data once a second person is enrolled (see Known limits).

### Testing (TDD, red-green-refactor)

- **Verifier:** conclusive → `IdentifiedSpeaker == best`; accepted but
  `Similarity < IdentifyThreshold` → `null`; above threshold but
  `best − runnerUp < IdentifyMargin` (two-profile fixture) → `null`; single
  profile above threshold → identified (margin auto-pass); `Skipped` /
  `Unavailable` / `Rejected` → `null`.
- **Dispatcher:** emits the notification with the resolved sender — identified
  name when present, `session.Config.Identity` when `null`.
- **Host:** `TranscribeAndDispatchAsync` routes `IdentifiedSpeaker` into the
  dispatch call and falls back to the config identity otherwise.

## Non-goals

- Per-speaker metric dimension / per-person dashboards (telemetry keeps
  `Identity` = satellite).
- Runner-up similarity telemetry — margin tuning is blind until a second
  profile exists; added then, not now.
- Voice-driven enrollment; any change to accept/reject, endpointing,
  early-close, STT, or the satellite / Wyoming protocol.

## Known limits

- With one enrolled profile the margin guard is inert (nothing to compare);
  identification rests on `IdentifyThreshold` alone until a second person is
  enrolled.
- `IdentifyMargin` cannot be field-calibrated before a second voice exists, so
  `0.10` is a reasoned default, not a measured one.
- A capture the gate accepts but that scores in the doubtful band is attributed
  to `"household"` even when it really was the enrolled speaker (safe-by-design:
  we under-personalize rather than mis-attribute).
