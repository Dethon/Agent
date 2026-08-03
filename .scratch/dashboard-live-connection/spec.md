# Spec — Dashboard Live Connection

Status: ready-for-agent

Grilled from candidate 11 of `.scratch/architecture-audit-2026-08-03/candidates.md`,
which holds the exact file and line evidence for every claim below. That candidate was
surveyed as a shared Blazor client library; the sharing half was rejected and recorded
as `docs/adr/0008-the-two-browser-clients-stay-separate.md`. What follows is the half
that survived.

Vocabulary follows the "Client live connection" section of `CONTEXT.md` — **live
connection**, **hub connection**, **reconnect**, **becoming live**, **connection
epoch**, **catch-up**. Those terms are shared with the chat client and the code is not;
the ADR says why.

## Problem Statement

Someone leaves the metrics dashboard open on a second monitor. The agent gets
redeployed, which takes a couple of minutes. When it comes back, the dashboard does
not. The page still shows a green Live dot on the overview, every chart still has
numbers in it, and not one of them will ever change again. The only way out is a page
reload, and nothing on screen suggests one is needed.

Three separate things have to go wrong for that, and all three are true today.

The connection gives up. The dashboard asks SignalR for automatic reconnection without
saying how, so it gets the default: four attempts spread over about forty-two seconds,
and then it stops for good. Any outage longer than that is permanent. A container
restart is longer than that.

Opening the dashboard at the wrong moment is just as bad. Automatic reconnection never
covers the first connection attempt — that is documented behaviour, not a bug — and the
dashboard's own start-up swallows the failure and marks itself started before it finds
out whether it worked. So a dashboard opened while the agent is still coming up is dead
on arrival, and calling start again does nothing, because the module believes it has
already started.

Even a recovery that does work leaves the page wrong. The metrics hub pushes what
happens while somebody is attached and never replays a gap, and the dashboard does
nothing when it comes back but flip a flag. Every event that arrived during the outage
is missing from the tables, missing from the totals, and missing from the charts, under
a dot that says Live.

Underneath all three is that nobody owns the connection. It is built in one place,
started from a layout component that ignores what happens, and wired up inside an
effect whose real job is turning events into store updates. There is no object whose
responsibility is being live, so there is no place where any of these rules could have
been written down.

The tests could not have caught it either. The dashboard's hub client is a concrete
class carrying fourteen `virtual` members and a `protected` constructor that exist only
so a test can subclass it, and the subclass stubs the three lifecycle members out to do
nothing. No test can raise a reconnect. The behaviour on reconnect is therefore
untested by construction, which is how "reconnect flips a flag and nothing else"
survived.

## Solution

One module, the **live connection**, owns being live. It builds the hub connection,
binds the event handlers to it, starts it, publishes the status, and catches up. It
keeps trying to start until it succeeds. It never gives up reconnecting.

For someone watching the dashboard, that means an agent restart is a blip. The dot goes
amber while the connection is coming back, the events resume, and the numbers correct
themselves to include what was missed. Opening the dashboard early works — it says
Connecting and then connects on its own. The dot means what it says, on every page
rather than only on the overview, because a live connection is one that is bound and
caught up rather than merely started.

The dashboard is not getting the chat client's connection. It has no user, no space, no
session and no rebuild, and it does not need a probe or a foreground policy. It gets
its own module, with the same vocabulary and none of the same code.

## User Stories

1. As a dashboard user, I want the live feed to come back on its own after the agent is
   restarted, so that I do not have to notice and reload the page.
2. As a dashboard user, I want the dashboard to keep trying to reconnect however long
   the outage lasts, so that a slow deploy does not kill the page permanently.
3. As a dashboard user, I want to open the dashboard while the agent is still starting
   and have it connect by itself, so that being early is not punished with a dead page.
4. As a dashboard user, I want the totals on screen to be right again after an outage,
   so that I am not reading numbers that are silently short.
5. As a dashboard user, I want the event tables to fill in what I missed during an
   outage, so that I can see what happened while I was not connected.
6. As a dashboard user, I want the breakdown charts redrawn from fresh data after an
   outage, so that a chart is not drawn from a partial set of events.
7. As a dashboard user, I want to know that the dashboard is not live whichever page I
   am on, so that I do not trust stale numbers on the seven pages that say nothing
   today.
8. As a dashboard user, I want to tell a dashboard that is recovering from one that has
   not connected yet, so that I know whether to wait or to go and check the agent.
9. As a dashboard user, I want the indicator to say Live only when events are actually
   arriving, so that a green dot is not a lie.
10. As a dashboard user on any metrics page, I want the connection information the
    overview page gives me, so that I do not have to navigate away to find out whether
    what I am reading is current.
11. As a dashboard user, I want a short network blip to heal without my noticing, so
    that a moment of bad wifi does not need a page reload.
12. As a dashboard user, I want catch-up to reload the range I am already looking at,
    so that recovering does not move my dates.
13. As a dashboard user, I want my group-by, metric and time choices to survive a
    recovery, so that catch-up does not reset the page under me.
14. As a dashboard user, I want the first page load not to fetch everything twice, so
    that opening the dashboard is no slower than it is today.
15. As a dashboard user watching during a deploy, I want reconnection attempts to back
    off to a steady interval, so that a service that is trying to start is not being
    hammered by my browser tab.
16. As a developer, I want one module to own building, binding, starting, publishing
    status and catching up, so that the order is executable rather than a run of
    statements inside an effect that also does other things.
17. As a developer, I want binding to be part of connecting, so that a connection cannot
    end up started and deaf.
18. As a developer, I want to fake the hub connection through an interface, so that
    tests stop subclassing a concrete production class.
19. As a developer, I want to raise a reconnect in a test, so that the catch-up rule has
    coverage at all — today's fake stubs the lifecycle members out and cannot.
20. As a developer, I want one generic receive verb rather than eleven named
    registration methods, so that adding a twelfth event does not mean editing the
    connection type, its fake and both.
21. As a developer, I want the `virtual` members and the `protected` constructor gone,
    so that production code stops carrying a shape that exists only for a test.
22. As a developer, I want the module to retry a failed first start itself, so that no
    caller has to remember to and no caller can swallow it.
23. As a developer, I want the started latch to record a successful start rather than an
    attempted one, so that a failure cannot lock the module out permanently.
24. As a developer, I want the retry schedule to be a decision I can read in one place,
    so that "how long before it gives up" has an answer that is not "look it up in the
    framework docs".
25. As a developer, I want catch-up to be a named type in the container, so that I can
    find what happens after an interruption without reading a lambda inside a layout
    component.
26. As a developer, I want catch-up to be an ordered step of becoming live rather than a
    callback somebody subscribed to at start-up, so that it cannot be forgotten.
27. As a developer, I want the reload decision keyed off a connection epoch, so that it
    is a comparison I can assert against the store.
28. As a developer, I want the connection store to be the only source of connection
    status, so that adding a second status display does not mean choosing between two
    mechanisms.
29. As a developer, I want the retry loop's delays to come from a time provider, so that
    its tests do not wait in real time.
30. As a developer, I want to assert that catch-up does not run on the first connect, so
    that the first-connect rule is protected against someone simplifying it away.
31. As a developer, I want to assert that a server push still reaches a store after a
    reconnect, so that the binding cannot be lost silently.
32. As a developer, I want the live-update effect to keep only its event-to-store
    mapping, so that it is not also a connection lifecycle.
33. As a developer, I want status to be named states rather than a boolean, so that
    "connecting for the first time" and "reconnecting" are not both false.
34. As a developer, I want the dashboard's connection vocabulary to match the chat
    client's, so that I do not have to learn two names for becoming live.
35. As a developer new to the dashboard, I want the connection behaviour in one place,
    so that I do not learn by accident that a layout component swallows the start
    failure.

## Implementation Decisions

### The live connection module

A new `MetricsLiveConnection` owns being live. Its collaborators are the hub connection
seam, the binder, the connection store, catch-up and a time provider. Callers get a
connect operation and asynchronous disposal, and nothing else. The layout component
calls connect and stops catching anything, because there is no longer a failure for it
to catch.

The name follows the chat client's, and for the same reason: the thing that survives an
interruption needs a different name from the transport instance that carries it.

### The sequence

Becoming live is one ordered sequence inside the module:

1. Bind the event handlers to the hub connection.
2. Start it, retrying until it succeeds.
3. Publish the connected status and advance the connection epoch.
4. Catch up, unless this is the first connection.

Binding happens once, before the first start attempt, and the retry loop wraps only the
start. A failed start leaves the hub connection intact and its registrations with it, so
rebinding per attempt would double every handler. A reconnect reuses the same hub
connection by definition, so nothing needs rebinding there either. This is the one place
the dashboard's sequence is genuinely simpler than the chat client's, and it is simpler
because the dashboard has no rebuild.

Steps 3 and 4 also run when the transport reconnects on its own, which is the path that
does nothing useful today.

### The connection seam

`IMetricsHubConnection` replaces the concrete hub client. It carries one generic receive
verb taking a wire method name and a typed handler and returning a disposable
registration, the three lifecycle events, the connection state, a start operation and
asynchronous disposal. The eleven named registration methods collapse into the one verb.
The fourteen `virtual` members and the `protected` parameterless constructor go, because
the interface is now the seam and the concrete implementation has nothing to expose for
testing.

The wire method names move to the binder, which is where the mapping from a name to a
handler already belongs. There is no factory: without a rebuild there is never a second
hub connection instance, so the single implementation is constructed in the container as
it is today.

### The retry policy

The bare automatic-reconnect call is replaced with an explicit policy: zero, two, ten
and thirty seconds, then thirty seconds forever. It never returns the value that means
stop. The policy is a pure function of the retry context and lives on its own, so it can
be read and tested without a connection.

Thirty seconds is the steady interval because it is the last of the framework's own
defaults, so the change is exactly "keep going" rather than a new schedule with new
numbers to justify.

### The initial start

Automatic reconnection does not cover the first connection attempt, so the module wraps
the start in its own loop on the same schedule, delaying through the injected time
provider. It does not give up. Failures are logged and swallowed inside the loop; the
loop's job is to keep trying, and there is no caller who could do anything with the
exception.

The started latch records a start that succeeded, not one that was attempted. That is
the whole of the current unrecoverable-first-start defect: the flag is set before the
work, so the failure path leaves the module believing it is running.

### Status

Connection status widens from a boolean to named states: connecting, live and
reconnecting. There is no permanent disconnected state any more, because the module
never stops trying, so the honest distinction is between never having been up and having
lost it. The store is the single source, and the overview page drops its own reading of
the connection in favour of it.

The indicator moves into the layout so that all nine pages show it. The overview keeps
whatever presentation it wants for its own header, reading the same store.

### The connection epoch

`ConnectionState` gains an integer epoch, incremented every time the client becomes live.
The module publishes it; catch-up keys off it.

An honest note for whoever reads this next. The chat client's epoch closes a race, where
a rebuild completes before anyone observes a disconnected state in between. That race
cannot happen here, because there is no rebuild and the transport always announces that
it is reconnecting before it announces that it has reconnected. The epoch is here for
shared vocabulary and because it makes the catch-up rule a comparison assertable against
the store rather than a private flag reachable only through the module. If someone later
decides that is not worth an integer, they are not missing a correctness argument.

### Catch-up

Catch-up is a named collaborator with a single asynchronous operation, injected into the
module and registered in the container. It is the dashboard's counterpart to the chat
client's session recovery, and deliberately a different name, because one re-reads data
and the other re-establishes an identity.

Its implementation reloads every metric family for the range the families currently
hold — the same work a page load does. After the metric family change it is a walk of
the family table rather than a fan-out over eleven stores, which is why this spec is
sequenced behind it.

Catch-up never runs on the first connection, where ordinary page load fetches the same
data. Running it there would double every request on first paint.

It is awaited as part of becoming live rather than detached, so a completed connect
means the numbers on screen are current. A failure inside catch-up is caught and leaves
the previous values in place; it does not fail the connection, which is live regardless
of whether the reload worked.

### The binder

The live-update effect becomes the binder. It exposes a bind operation taking the hub
connection and an unbind operation releasing the registrations, and the module drives
both. Its body is unchanged: the mapping from each event to its store update and its
family refresh, which after the metric family change is a walk of the family table. It
stops owning the connection lifecycle, stops registering the three lifecycle handlers
and stops being started from the layout.

## Testing Decisions

A good test here asserts something a person looking at the dashboard would notice. The
headline assertion is that after a reconnect the dashboard holds data it did not hold
before — not that a catch-up method was called, and not that a flag flipped. The reason
the current defects survived is precisely that the existing suite asserts store contents
for live events and asserts nothing at all about the lifecycle, because its fake stubs
the lifecycle out.

### Seams

Four, of which two already exist.

`IMetricsHubConnection` is the single seam beneath the module and is new. Its fake holds
one handler registry keyed by wire method name with a raise helper, can fail a start a
scripted number of times before succeeding, and can raise the three lifecycle events —
which the current fake cannot. It replaces the existing fake's eleven handler lists,
eleven overrides and eleven raise helpers.

Catch-up's interface is the second new seam, faked to record that it ran and when. This
keeps the module's own suite free of HTTP entirely: proving that catch-up did not run on
the first connect is a check on a counter rather than a check on captured requests.

`TimeProvider` is the existing convention, with the fake from
`Microsoft.Extensions.TimeProvider.Testing`, already referenced and used across the
repository. Only the initial-start loop needs it. The reconnect delays belong to the
framework once it has the policy, and the policy itself is a pure function that needs no
seam.

`ConnectionStore` is used real and asserted directly.

The binder, the stores and, where a test reaches that far, the metric families are real.
That is what makes the headline assertion a store change rather than a call record. The
existing fake HTTP handler is reused wherever the API service is genuinely in play.

### What gets tested

The module gets the behavioural suite: a push reaches a store on a first connect; a push
still reaches a store after a reconnect; catch-up runs on a reconnect and is skipped on
the first connect; a completed connect implies catch-up has finished; a start that fails
several times is retried and eventually succeeds without the caller doing anything; a
failing catch-up leaves the connection live and the previous values in place; disposal
releases the registrations so a push afterwards changes nothing.

The retry policy gets a pure test: the first four delays match the intended schedule, and
the policy never returns the value that means stop, however many attempts have been made
and however long it has been going.

The connection store gets a test that the epoch advances on becoming live and does not
advance on the connecting or reconnecting transitions, and that the three status states
are reachable.

The live-update effect keeps its existing coverage. Its event-to-store assertions move to
driving the new fake rather than the subclassed one, and its lifecycle expectations go,
because it no longer registers lifecycle handlers.

### Prior art

The existing dashboard effect suite is the model for the store-level assertions and for
the fake HTTP handler, both of which are extended rather than replaced. The chat client's
connection suite is the model for the module's structure — a scripted connection and
store-level assertions after an interruption — though no code is shared with it. The
existing latency API test shows the capturing-handler pattern for anything that needs to
assert on a request. `FakeTimeProvider` usage across the unit suites is the model for the
retry loop's timing.

Follow red-green-refactor. The first test written is the headline one — after a reconnect
the dashboard holds what it missed — and it must fail against the current design before
any of the module lands. Writing it first also proves the point about the seam, because
it cannot be written at all until the lifecycle can be raised.

## Out of Scope

The shared Blazor client library. `docs/adr/0008-the-two-browser-clients-stay-separate.md`
records the decision and its evidence. Nothing in the chat client is modified by this
spec, and nothing is extracted from it.

The half-open transport case. A browser tab frozen in the background can thaw with a
connection that reports itself as healthy over a transport that is dead, and no close
event ever fires, so no retry policy helps. Covering it needs a visibility hook in
JavaScript, a foreground decision, a probe verb on the hub and a rebuild path — the
machinery the chat client has. The dashboard gets none of it here. Its dominant failure
is an agent restart, which the retry policy does cover.

Rebuilding the hub connection, probing it, and any ping verb on the metrics hub.

Server-side replay. The metrics hub stays an empty hub with no connection callback, and
catch-up stays entirely a client-side reload through the existing API.

The metric family work itself. This spec consumes the family table for catch-up and
changes nothing about how a family is declared, refreshed or rendered.

The unselective page subscriptions. Every dashboard page subscribes to a whole store
observable with no selector and re-renders on every dispatch, which is a real cost and a
real difference from the chat client. It is a rendering concern rather than a connection
one and there is no reported symptom, so it stays a recorded finding in the audit
document, together with the corrected diagnosis.

What the eleven events carry and how the effect maps them to store updates.

Reporting why a page load failed. The page-load path swallows the reason today and
continues to.

## Further Notes

Sequenced after the metric family change and after the chat live connection. The metric
family change is a hard prerequisite in practice: it rewrites the live-update effect's
subscription block and its test file, both of which this spec edits, and it turns the
page-load fan-out into the family table that catch-up walks. Written first, catch-up
gets written twice. The chat live connection is a softer prerequisite, kept because it
is the worked example this module mirrors in naming, module shape and test structure,
and doing them in the other order means writing the second one twice.

The audit's original write-up of this candidate should be read for its evidence and not
for its argument. Its claim that the two clients' state containers are interchangeable
is wrong, its explanation of the dashboard's extra re-renders is wrong, and the ADR
records both. Its file and line references are accurate and are the fastest way to find
everything this spec describes.

Two of the three defects here are one decision away from each other and it is worth
saying which. Never giving up on reconnection and retrying the first start look like the
same fix and are not: the framework covers neither by default, covers the first one when
asked, and covers the second one never. A change that only replaces the policy leaves a
dashboard opened during a restart just as dead as it is today.
