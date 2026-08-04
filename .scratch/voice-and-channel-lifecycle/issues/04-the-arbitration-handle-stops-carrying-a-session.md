# 04 — The arbitration handle stops carrying a session

**What to build:** The wake arbiter reaches only the facts and verbs arbitration actually
depends on, so that someone changing the satellite session can tell from the type whether
arbitration is affected.

`WakeArbiterHandle` at `WakeArbiter.cs:8-11` carries a `SatelliteSession`, a pause delegate
and a legacy-end delegate. The arbiter reads through that session at seventeen sites:

- `GetCaptureActivity` — `:139`
- `Config.RmsOffsetDb` — `:146`, `:186`
- `TryAbortCapture` — `:201`, `:250`
- `Config.Room` and `Config.Identity` — `:225-226`, `:233-234`, `:263-264`, `:317-318`
- `SupportsPause` — `:289`
- `SatelliteId` — `:312`, `:316`, `:327`

After this ticket the handle carries exactly those and no session: the satellite identity
(one value, after ticket 01, rather than three fields), the RMS offset, whether the
satellite supports pause, its capture activity, and the verbs to abort, pause and re-arm.

Note that the original noted entry proposed narrowing to `CalibratedPeakIn`, `TryAbort` and
`ReArmAsync`. That was written from an incomplete read and is not enough — see the list
above.

The handle is built at `SatelliteConnection.cs:75`, which is the one production
construction site, and at `WakeArbiterTests.cs:53`, which is the one test site.

Behaviour must not change, including the ordering the arbiter depends on: a satellite whose
link just died must stop being an arbitration candidate before anything unbounded runs.

**Seam:** `WakeArbiterTests` already constructs a handle directly, so the narrowed handle is
proven where it is already built. `Tests/Integration/McpChannelVoice/WakeArbitrationHostTests.cs`
covers the cross-satellite behaviour end to end and must stay green untouched.

Start red: change the handle's shape first and let `WakeArbiterTests` fail to compile —
that is the red — then build until both it and the integration test pass.

**Blocked by:** 03, and 01 through it. The capture activity and abort verbs come off the
microphone type that 03 creates, and the four identity pairs collapse to one value only
after 01.

**Status:** ready-for-agent

- [ ] `WakeArbiterHandle` does not mention `SatelliteSession`.
- [ ] It carries the identity, the RMS offset, pause support, capture activity, and the abort, pause and re-arm verbs — nothing else.
- [ ] `SatelliteConnection` is the only production site that builds one.
- [ ] `WakeArbiterTests` and `WakeArbitrationHostTests` pass with no assertion changed for behaviour reasons.
- [ ] The arbiter registration is still released synchronously and first during unwind.
