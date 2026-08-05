# 0013 — The microphone and the turn are separate types

Status: accepted
Date: 2026-08-04

## Context

`CaptureSession` was extracted to own the microphone for one satellite connection, and
`.claude/rules/voice.md` records it as one of the voice server's owned concerns. The
field it was extracted from stayed where it was: `SatelliteSession.cs:9` still holds the
`UtteranceCapture?`, and `:72-95` still exposes seven public members over it.

Two callers therefore go around the module. `WakeArbiterHandle` carries an entire
`SatelliteSession` to reach a capture activity and an abort. More interestingly,
`RequestApprovalTool.CaptureAnswerAsync` at `:153-196` opens a capture, waits on it,
closes it in a `finally` and records the gate statistics into the room-noise memory — by
hand, duplicating the pairing `CaptureSession.Close` exists to keep together.

That duplication looks like an oversight and is not. The approval mic genuinely must not
do what `CaptureSession` does. `CaptureSession.OpenWakeTurn` and `OpenFollowUpTurn` both
call `Playback.MarkTurnStart`, and `Close` calls `Playback.MarkSpeechEnd`. An approval
prompt is not a turn: it is a question the agent asked mid-turn, and marking a turn start
on it would corrupt the turn-to-first-audio latency of the turn actually in flight.

So there are two true statements that today's structure cannot hold at once: the mic
should have one owner, and the approval capture must not carry turn semantics.

## Decision

Two types, one under the other.

The **microphone** owns the capture field and everything that touches it: open, close,
feed, force-end, abort, read activity, and the pairing of closing with recording gate
statistics into the room-noise memory. It knows nothing about turns.

**`CaptureSession`** owns the turn semantics on top of it: the playback anchors, the wake
announcement and its room-level payment, the wake and follow-up openers, and the two
indicator events the satellite's LED depends on.

A wake turn and a follow-up turn go through `CaptureSession`. The approval capture holds
the microphone directly. Which type a caller holds is now the statement that it is or is
not a turn.

## Considered options

**Widen `CaptureSession` to cover approvals.** One type owns the mic, with a third
opener and an optional wake hook. Rejected: it makes the wake-announcement hook
meaningless on one of three paths, and every approval test then constructs a gate
factory, a `TimeProvider`, a history span and a null hook to reach an open and a close.

**Leave the approval sequence hand-rolled and only make the field private.** Fixes the
public surface and the arbiter handle. Rejected because it leaves the close-and-record
pairing duplicated, which is the part that can silently go wrong — a capture that closes
without paying back into the room-noise memory lets the memory expire on a satellite used
mostly for approvals, and the voice rules already warn about exactly that.

**Narrow only `WakeArbiterHandle`.** Smallest change, and it addresses the caller that is
merely wide rather than the caller that is duplicating a rule. Rejected on those grounds.

## Consequences

- The capture-observation point moves onto the microphone, as `Microphone.IsOpen`, and keeps
  having no production caller. It is an observation point with test call sites only, and it
  now sits on the thing being observed rather than on the session.
- `SatelliteConnection` must expose its microphone, not only its session, or the
  connection tests have nothing to spin-wait on.
- A future turn-taking path picks its type and inherits the right rules. A new kind of
  one-shot listen — a disambiguation prompt, say — holds the microphone and cannot
  accidentally anchor a turn.
- The session keeps `Turn` and `Playback` as owned sub-objects and now has no capture
  surface at all, so the three per-connection concerns are shaped the same way.
