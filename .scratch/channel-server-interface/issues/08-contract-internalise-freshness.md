# 08 — Contract: internalise freshness, assert policy per server

**What to build:** The contract step. With every server migrated, the old form is removed and the new contract is enforced by a test rather than by a comment.

Two things happen here, and neither is possible until all six migrations have landed.

**The freshness window becomes internal to the inbox.** It is currently a shared public constant that every emitter passes in. That is precisely what allowed six near-miss variants to exist and be fixed three separate times. Once no caller supplies it, no caller can substitute its own value. The eighteen-line doc comment that currently warns readers which liveness question is the right one can shrink to whatever still earns its place — the warning label is replaced by the type.

**The conformance theory asserts each server's declared delivery policy.** The existing six-row theory boots each real server's configuration and checks it exposes a conforming channel surface. It gains a per-server policy assertion, so the table becomes the single place where "this is a channel server, and this is the policy it chose" is verified against the real registration. That table is what would have caught all three rounds of the original defect.

After this ticket, a seventh channel cannot be added without declaring a policy, cannot compute liveness a different way, and appears in the conformance table by construction.

**Blocked by:** 03, 04, 05, 06, 07

**Status:** done

- [x] The freshness window is internal to the inbox; no caller supplies it.
- [x] No public liveness property remains on any channel server.
- [x] The conformance theory asserts each of the six servers' declared delivery policy against its real registration.
- [x] The theory still boots each real server configuration rather than a stub.
- [x] The doc comment on the freshness constant is reduced to what the type does not already state.
- [x] Every channel server builds and its tests pass.
- [x] The two thin channel servers still reference the domain project alone, with no infrastructure dependency acquired anywhere in this feature.
- [x] A count of the deletions is recorded below: six transport tools to one, six error filters to one, four emitters to one, six liveness properties to zero, two interfaces removed.

## Result

Deletion counts, recorded per the last acceptance criterion:

| before | after |
|---|---|
| 6 per-server `McpChannelReceiveTool` wrappers | 1 (`Channels.Hosting`) |
| 6 hand-copied call-tool filters | 1 (inside `AddChannelServer`) |
| 6 emitter classes (4 `ChannelNotificationEmitter` + `ScheduleNotificationEmitter` + `DownloadNotificationEmitter`) | 1 sealed `ChannelNotificationEmitter` |
| 6 public `HasActiveSessions` properties | 0 — liveness is the emit's return value |
| 2 single-adapter interfaces (`IScheduleNotificationEmitter`, `IDownloadNotificationEmitter`) | 0 |
| 4 emitter test subclasses (`CapturingEmitter` x2, `CollectingEmitter`, `RecordingEmitter`) | 1 `VoiceInboxProbe` over a real inbox |
| 1 public `LiveSubscriberFreshness` constant every emitter passed in | internal to `ChannelInbox`, no parameter |

**Deviation from the plan.** The service-bus channel is registered gate-on-live, not the broadcast
the plan's table named. With liveness only available as the emit's return value the check happens
after the enqueue, so broadcast would buffer a copy *and* abandon the broker message — every
redelivery leaving another copy behind. Gate-on-live reproduces today's behaviour exactly: nothing
buffered, false returned, message abandoned and redelivered once. Confirmed with the author before
implementing.
