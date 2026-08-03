# Spec — Chat Live Connection

Status: ready-for-agent

Grilled from candidate 2 of `.scratch/architecture-audit-2026-08-03/candidates.md`,
which holds the exact file and line evidence for every claim below. No ADR: both
candidate subjects failed the hard-to-reverse test. Vocabulary follows the "Chat
client connection" section of `CONTEXT.md` — **live connection**, **hub
connection**, **rebuild**, **reconnect**, **becoming live**, **connection epoch**,
**session recovery**.

## Problem Statement

A WebChat user on a phone opens the app, uses it, switches to another app, and
comes back a few minutes later. The header says Connected and the dot is green.
The agent's replies never arrive. Messages the user sends appear to go nowhere,
because the reply and the streaming updates for it are pushed by the server and
the client is no longer listening. The only way out is a full page reload, and
nothing in the interface suggests that.

The cause is a caller obligation that nobody meets. Handler binding lives in a
separate object from the connection it binds to, and it is triggered once, from
start-up. When the browser suspends the tab, the transport dies without a close
event; on resume the client throws the hub connection away and builds a new one.
That rebuild re-identifies the user, rejoins the space and re-sends the push
subscription, but it never rebinds the six server pushes — topic changes, stream
changes, user messages, tool calls, approval resolutions and agent list updates.
They stay bound to a disposed object. The client is genuinely connected and
genuinely deaf.

The obligation is invisible because the binder guards itself with an
already-subscribed flag and the teardown path never clears it. The second bind
attempt, if anyone made one, would silently do nothing. Nobody makes one.

Underneath the defect is why it was possible. The live-connection story is spread
across eight files, and its ordering rules exist only as comments. Teardown must
publish a disconnected status before reconnecting, or the reconnection reload is
skipped — the connection module is shaped by a downstream effect's internal flags.
A rebuild does not raise the transport's own reconnected event, so post-connect
recovery is fired by hand on every connect after the first. Neither rule is
enforceable, and both are the kind a future change quietly breaks.

The tests cannot catch any of it. The existing connection tests drive rebuild
scenarios through a faked hub connection, but that fake cannot carry handlers,
because binding reaches past the fake to a raw transport object the fake reports
as null. The assertion that matters — a server push still reaches the store after
a rebuild — is unwritable. The binder and the connection factory have no tests at
all.

## Solution

Binding becomes part of connecting. One module, the **live connection**, owns the
whole sequence of becoming live: build a hub connection, bind the server pushes to
it, start it, publish the status, then run session recovery. Tearing down unbinds
before disposing. There is no step a caller can forget, because there is no step a
caller performs.

For the user, resuming a backgrounded tab restores a chat that actually works.
Replies arrive, streams resume, the topic list updates, approval prompts appear.
The status indicator keeps meaning what it says, because a live connection is one
that is bound and recovered, not merely started.

Three smaller things follow from putting the sequence in one place. The live
connection stops publishing its own status and lets the connection store be the
one source, so the two connection dots rendered from two different sources become
one story. Interruption is detected by a **connection epoch** — a count of how
many times the client has become live — instead of by observing a disconnected
status in between, so a rebuild fast enough that nobody sees the gap still
triggers the history reload. And session recovery becomes an injected
collaborator rather than an event subscription, so it runs as an ordered step of
becoming live rather than as a callback somebody wired up at start-up.

## User Stories

1. As a mobile WebChat user, I want the chat to keep receiving the agent's replies
   after I switch away from the app and come back, so that I do not lose a
   conversation to a silent connection.
2. As a mobile WebChat user, I want a resumed tab to show messages that arrived
   while I was away, so that I can catch up without reloading the page.
3. As a mobile WebChat user, I want a streaming reply that was in flight when I
   backgrounded the app to keep rendering when I return, so that I see the whole
   answer.
4. As a mobile WebChat user, I want tool call activity to keep appearing after a
   resume, so that I can tell the agent is working.
5. As a mobile WebChat user, I want an approval prompt raised while I was away to
   reach me when I return, so that the agent is not blocked waiting on a dialog I
   never saw.
6. As a mobile WebChat user, I want the topic list to update after a resume, so
   that conversations created or renamed elsewhere show up.
7. As a mobile WebChat user, I want the agent list to refresh after a resume, so
   that a newly registered agent is selectable.
8. As a WebChat user on an unstable network, I want a short transport drop to heal
   without losing server pushes, so that a lift ride does not break the session.
9. As a WebChat user, I want the connection indicator to mean the client can both
   send and receive, so that a green dot is not a lie.
10. As a WebChat user, I want the same connection status wherever it is shown, so
    that the header and the chat panel never disagree.
11. As a WebChat user, I want history to reload after any interruption, so that I
    am not looking at a stale transcript.
12. As a WebChat user, I want history to reload even when the interruption was too
    short to notice, so that a fast recovery is not a silently degraded one.
13. As a WebChat user, I want the server to know who I am again after an
    interruption, so that my messages are attributed correctly.
14. As a WebChat user, I want to be rejoined to my space after an interruption, so
    that I keep receiving that space's messages.
15. As a WebChat user, I want my push subscription to survive an interruption
    without generating a new endpoint, so that I do not lose the space
    memberships attached to it.
16. As a WebChat user, I want the first page load to validate my space before
    joining it, so that a bad slug in the URL is corrected rather than joined.
17. As a WebChat user on a dead network, I want the client to stop retrying and
    show Disconnected, so that I can tell the problem is my connection.
18. As a WebChat user, I want a failed recovery attempt to be retried the next time
    I foreground the app, so that I am not stuck until I reload.
19. As a WebChat user, I want a stale connection that reports itself as healthy to
    be replaced, so that a half-open transport does not strand me.
20. As a developer, I want binding to be part of connecting, so that I cannot
    introduce a deaf-but-connected client by forgetting a call.
21. As a developer, I want one module to own the order of building, binding,
    starting, publishing status and recovering, so that the ordering rules are
    executable instead of being comments.
22. As a developer, I want to assert that a server push reaches the store after a
    rebuild, so that the defect that motivated this work has a regression test.
23. As a developer, I want to assert that session recovery runs after a rebuild
    and not on the first connect, so that the first-connect rule is protected.
24. As a developer, I want to fake the hub connection and keep the binder,
    dispatcher and stores real, so that my tests describe user-visible behavior
    rather than internal call sequences.
25. As a developer, I want the connection store to be the single source of
    connection status, so that adding a status display does not mean choosing
    between two mechanisms.
26. As a developer, I want interruption detected by a counter rather than by a
    status sequence, so that the reload decision does not depend on winning a
    race.
27. As a developer, I want the reconnection effect to stop tracking two booleans,
    so that its logic is one comparison.
28. As a developer, I want session recovery to be a named type in the container,
    so that I can find what happens after an interruption without reading an
    initialization closure.
29. As a developer, I want the live connection to expose only what callers use, so
    that the interface stops advertising members nothing reads.
30. As a developer, I want the binder's idempotence guard gone, so that a bind
    call cannot silently do nothing.
31. As a developer, I want handlers bound before the transport starts, so that
    there is no window in which the client is started and deaf.
32. As a developer, I want the connect call to complete only once the server knows
    who the client is, so that tests and callers do not race a detached recovery
    task.
33. As a developer, I want the rebuild attempt timeout to bound only the
    handshake, so that a slow recovery call does not trigger a spurious rebuild.
34. As a developer, I want the module named for what it is, so that I can tell the
    thing that survives rebuilds from the transport instance that does not.
35. As a developer new to the client, I want the connection vocabulary written
    down, so that I do not use "reconnect" for two different things.

## Implementation Decisions

### The live connection module

`IChatConnectionService` and `ChatConnectionService` are renamed to
`IChatLiveConnection` and `ChatLiveConnection`. The rename is not cosmetic: the
type changes from a connection holder into the owner of binding and recovery, and
the old name does not distinguish it from the hub connection abstraction.

The interface narrows to `ConnectAsync`, `ReconnectIfNeededAsync`, the existing
raw hub connection accessor, and async disposal. `IsConnected`, `IsReconnecting`,
`OnStateChanged`, `OnReconnecting` and `OnReconnected` are all removed.
`IsReconnecting` and `OnReconnecting` have no consumers anywhere in the client
today. The raw accessor is retained deliberately and temporarily; removing it is
candidate 5's work and is out of scope here.

`ForegroundReconnectPolicy` and `AggressiveRetryPolicy` stay outside the module as
the pure decision functions they already are. `HubEventDispatcher` stays as the
pure action mapping.

### The sequence

Becoming live is one ordered sequence inside the module:

1. Build a hub connection through the factory.
2. Bind the server pushes to it.
3. Start it.
4. Publish the connected status.
5. Run session recovery, unless this is the first connect.

Binding before starting closes the window in which a started connection has no
handlers, so a push arriving immediately after the handshake is not dropped.
Teardown is the mirror: detach the close handler, unbind, dispose.

Session recovery is awaited rather than detached, so a completed connect means the
server has re-identified the client. It is awaited outside the per-attempt
timeout. That timeout exists to bound a handshake that can hang for tens of
seconds after a mobile radio resume; extending it over recovery would let a slow
space rejoin cancel and trigger a rebuild retry on an otherwise healthy
connection.

Recovery is skipped on the first connect. This is a real rule, not an accident of
the current implementation: first-load start-up validates the space slug and can
replace it before joining, so running recovery on the first connect would join an
unvalidated space and then join again after validation.

### The receive seam

`IChatHubConnection` gains a generic receive verb — a method taking a wire method
name and a typed handler, returning a disposable registration, mirroring the
transport's own API. This is the only widening of that interface in this spec; the
send verbs are candidate 5.

### The binder

`ISignalREventSubscriber` and `SignalREventSubscriber` become `IHubEventBinder`
and its implementation, with `Bind` taking the hub connection to bind to and
`Unbind` releasing the registrations. The six wire-name-to-dispatcher-method pairs
stay together in this one type; the module does not learn them.

The binder is driven by the live connection and is no longer touched by the
initialization effect. The already-subscribed guard is deleted — it exists to make
a repeated bind safe, and its only effect in practice was to make the missing
unbind silent. The module calls unbind and bind in a known order, so the guard has
nothing to protect.

### Session recovery

The reconnect closure currently living in the initialization effect becomes a
named `ISessionRecovery` with a single asynchronous recover operation, registered
in the container and injected into the live connection. Its body is unchanged:
re-identify the user, rejoin the current space, and re-send the existing push
subscription without force-refreshing the push channel. That last detail matters
and is preserved — a full resubscribe would generate a new endpoint in Chrome and
lose accumulated space memberships.

The initialization effect keeps its own first-load work, including the space
validation that must precede the first join, and stops subscribing to a
reconnected event.

### Status

The live connection publishes no status of its own. `ConnectionStore` becomes the
single source. The two components that currently render a connection dot from the
module's own state, and the space effect that reads its connected flag, all move to
reading the store, matching how every other component in the client reads state.

### The connection epoch

`ConnectionState` gains an integer epoch, incremented every time the client
becomes live — on both the connected and reconnected transitions. `ConnectionState`
is otherwise unchanged.

`ReconnectionEffect` replaces its two booleans with a single record of the last
epoch it reloaded for. It reloads when it observes a connected status whose epoch
is higher than that record, and records the epoch without reloading the first time
it sees one. Its reload body — refetch topics, reload the selected topic's history,
restart the session, resume streams — is unchanged.

This deletes the synthesized disconnected dispatch in teardown and the ordering
rule that went with it. It also closes a race the old rule could not: a rebuild
that completes without anyone observing a disconnected state still reloads,
because the epoch moved.

### Placement

Everything stays in the WebChat client project. There is no shared Blazor
connection seam and there will not be one: candidate 11's grilling reached the same
conclusion this section did from the other side, and
`docs/adr/0008-the-two-browser-clients-stay-separate.md` records it. The dashboard's
metrics hub client has no rebuild, no probe, no session recovery, no space and no
user identity; it gets its own live-connection module rather than adopting this one.

## Testing Decisions

A good test here asserts what a user would notice. The headline assertion is that
a server push raises a store change after a rebuild — not that a bind method was
called, and not that a flag flipped. Tests that name internal call sequences are
the reason the current defect survived: the existing suite drives rebuild
scenarios thoroughly and asserts everything except whether the client can still
hear.

### Seams

Four, of which three already exist.

`IChatHubConnection` is the single seam beneath the module. Its existing fake
gains a handler registry and a raise helper, which the new receive verb makes
possible for the first time. `IHubConnectionFactory` and its existing fake script
the connection sequence so a rebuild yields a distinguishable second instance.
`ISessionRecovery` is faked to observe that recovery ran, and that it did not run
on the first connect. `ConnectionStore` is used real and asserted directly.

The binder, the hub event dispatcher, the action dispatcher and the stores are all
real in module tests. That is what makes the headline assertion a store change
rather than a call record.

### What gets tested

The live connection gets the behavioral suite: a push reaches the store after a
rebuild; a push reaches the store on a first connect; handlers are released when a
connection is torn down, so a push on a disposed instance changes nothing; recovery
runs on rebuild and is skipped on first connect; a completed connect implies
recovery has finished; the probe, retry and give-up paths behave as they do today.

The reconnection effect gets epoch-based coverage in place of its flag-based
coverage: no reload on the first epoch, reload on a higher epoch, no reload on a
repeated status at the same epoch, and a reload for a rebuild in which no
disconnected status was ever observed. That last case is the one the old design
could not express.

The binder gets its first tests: binding registers the six pushes against the
connection it was given, and unbinding releases them.

The connection store gets a test that the epoch advances on becoming live and does
not advance on the connecting or disconnected transitions.

### Prior art

`ChatConnectionServiceTests` is the model for the module suite, including its
scripted connection factory and its fake hub connection; both are extended rather
than replaced. `ReconnectionEffectTests` is the model for the epoch tests and
already exercises the store observable directly. `InitializationEffectTests` shows
the call-recorder pattern for ordering assertions and needs its subscribe
assertion removed, since the effect no longer binds.

Follow red-green-refactor. The first test written is the headline one, and it must
fail against the current design before any of the extraction lands.

## Out of Scope

Candidate 5, the hub call surface. The raw hub connection accessor stays on the
interface, the eight calling services keep reaching through it, and the
disconnected-path guards they each duplicate are untouched. Adding the send verbs
to the hub connection abstraction, introducing a gateway that owns the
disconnected decision, and deleting the duplicated integration adapters are all
that candidate's work.

Candidate 11, the dashboard's live connection. Nothing in the dashboard client is
modified here. That candidate was surveyed as a shared Blazor seam extracted from
this module; its grilling rejected the sharing and reframed it as a separate fix to
the dashboard's own connection, which this spec mirrors in naming and test structure
but shares no code with.

The connection store reducer's direct use of the system clock. It violates the
project's testable-time rule and sits in a reducer this spec edits, but fixing it
means giving a static reducer a dependency, which is a design change with its own
argument. Leave it and note it.

The transport's own reconnect behavior, the retry policy, the foreground decision
policy, the probe timeout and the attempt bounds. All are preserved exactly.

Any change to what the six server pushes carry or how the dispatcher maps them to
actions.

## Further Notes

The defect and the extraction are not separable in this spec, by decision. A
minimal patch exists — unbind and rebind around the recovery step in the
initialization effect — and it is testable today. It was rejected because it
leaves the caller obligation in place. That reasoning stands on its own; the
original supporting argument — that candidate 11 would copy the obligation into a
shared seam — no longer applies, since there is no shared seam.

The candidate document's note that "a red test for the missing behaviour is
enough" and its note that the assertion is "unwritable today" cannot both hold.
The store-level assertion is genuinely unwritable before the receive seam exists,
because the current fake reports a null transport. An effect-level assertion is
writable today. This spec takes the store-level one, which is why the seam comes
first.

Vocabulary was written into `CONTEXT.md` during the grilling session, under "Chat
client connection". Use those terms in code, comments and tickets. In particular,
do not use "reconnect" for a rebuild: they are different events with different
consequences for anything bound to the connection, and conflating them is how the
defect reads as harmless.
