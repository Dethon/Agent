# 03 — The microphone becomes a type

**What to build:** The satellite's microphone becomes its own type that owns being open
and closed, so that every path that listens gets the rule about closing it, and a caller
that is not opening a turn cannot accidentally anchor one.

`SatelliteSession.cs:9` holds a `private UtteranceCapture? _capture` and exposes seven
public members over it at `:72-95`: `OpenCapture`, `CloseCapture`, `HasActiveCapture`,
`RouteAudio`, `EndCapture`, `GetCaptureActivity` and `TryAbortCapture`. `CaptureSession`
was extracted to own the microphone — `.claude/rules/voice.md` says so — but the field it
was extracted from stayed public, so callers go around the module.

The field and the seven members move into one small type that owns the microphone and
nothing else, including the pairing of closing a capture with recording what it taught the
room-noise memory. `CaptureSession` keeps the **turn** semantics on top of it: the playback
anchors (`MarkTurnStart`, `MarkSpeechEnd`), the wake announcement and its room-level
payment, the wake and follow-up openers, and the two indicator events.

**This is what makes the approval mic's difference structural.**
`RequestApprovalTool.CaptureAnswerAsync` at `:153-196` opens a capture, waits, closes it in
a `finally` and records the gate statistics — by hand, and correctly, because an approval
prompt must not mark a turn start or a speech end on the playback queue. It is not a turn.
After this ticket it holds a microphone and the compiler says so; today it holds a session
and a comment says so. See `docs/adr/0013-the-microphone-and-the-turn-are-separate-types.md`.

`HasActiveCapture` moves onto the microphone type. It keeps having zero production callers
and sixteen test call sites, which is fine — it is an observation point, and it now sits on
the thing being observed. The sixteen sites are a mechanical rename:
`RequestApprovalToolTests.cs:140`, `:159`, `:186`, `:231`, `:531`;
`FollowUpConversationTests.cs:35`, `:191`, `:381`, `:392`, `:461`, `:566`;
`CaptureSessionTests.cs:108`; `SatelliteConnectionTests.cs:555`, `:723`, `:837`, `:877`.

`SatelliteConnection` exposes `Session` at `:31` but not its `CaptureSession`. It must
expose the microphone, or those four `SatelliteConnectionTests` spin-waits have nothing to
wait on.

`WakeArbiterHandle` still carries a whole `SatelliteSession` after this ticket. Ticket 04
narrows it; leave it alone here beyond whatever the moved members force.

Behaviour must not change. In particular the split unwind in `SatelliteConnection` — the
synchronous phase that releases the arbiter registration before anything unbounded runs —
stays synchronous and stays first.

**Seam:** the two callers, both of which already have test files. The turn module's tests
cover turn semantics; the approval tool's tests cover the distinguishing rule. No new test
file.

Start red: write a test asserting the approval capture closes and pays back into the
room-noise memory without touching the playback queue's turn anchors, watch it fail against
today's shape, then build.

**Blocked by:** 01. The moved members are read at identity-stamp sites, and going first
means editing the same lines twice.

**Status:** ready-for-agent

- [ ] One type owns the capture field and the seven operations over it, including the close-and-record pairing.
- [ ] `SatelliteSession` no longer exposes any capture member.
- [ ] `CaptureSession` keeps the turn semantics and holds the microphone rather than reimplementing it.
- [ ] `RequestApprovalTool.CaptureAnswerAsync` uses the microphone type; it still marks no turn start and no speech end.
- [ ] `SatelliteConnection` exposes its microphone; the four spin-waits in `SatelliteConnectionTests` still compile and pass.
- [ ] The split unwind in `SatelliteConnection` still releases the arbiter registration synchronously and first.
- [ ] `.claude/rules/voice.md`'s "One gate factory, one turn module, one capture module" paragraph says what the microphone type owns and why the approval mic uses it directly.
- [ ] Voice unit and integration tests pass with no behaviour change.
