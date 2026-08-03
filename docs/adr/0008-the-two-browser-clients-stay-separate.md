# 0008 — The two browser clients stay separate

Status: accepted
Date: 2026-08-03

## Context

`WebChat.Client` and `Dashboard.Client` are both Blazor WebAssembly projects, both
reference `Domain` and nothing else, and both contain a file called `Store.cs`, a
file called `IAction.cs`, a file called `LocalStorageService.cs` and a SignalR
client. Opened side by side they look like the same client written twice.

The architecture review of 2026-08-03 proposed, as candidate 11, extracting the
store, the dispatcher, local storage and the connection seam into a shared Blazor
class library, with the dashboard adapting to it. This is the second review in two
days to reach for a sharing argument; ADR-0001 closed the previous one.

Read against the code, the four sharing claims do not hold.

**The two stores are not used the same way.** `WebChat.Client/State/Store.cs` is
`sealed`. Each WebChat store holds one privately and registers a catch-all with a
central `Dispatcher`, so every store sees every action and reduces it through a
single `Reduce(state, IAction)` switch — `WebChat.Client/State/Topics/TopicsStore.cs:32-45`.
`Dashboard.Client/State/Store.cs` is a public base class that all ten dashboard
stores inherit, each calling `Dispatch(action, reducer)` with its own per-action
lambda. The dashboard has no dispatcher at all. Deleting the dashboard's copy and
referencing WebChat's does not compile, and making it compile means rewriting ten
stores onto a pattern the dashboard does not use. That is not a deletion.

**The dispatch guard the dashboard is missing could not fire there.** WebChat's
reference-equality guard at `Store.cs:28-35` skips a notification when a reducer
hands back the instance it was given, which happens because the catch-all sees
actions belonging to other stores and falls through to `_ => state`. Every
dashboard reducer is bound to one action and always allocates. The guard would be
dead code. The dashboard does re-render more than it needs to, but the cause is
that its pages subscribe to a whole store observable with no selector, where
WebChat's `StoreSubscriberComponent` selects a slice and applies
`DistinctUntilChanged`. That is a different fix in different files.

**The two local storage services are a union, not a duplicate.** The dashboard's
has `GetAsync<T>() where T : struct, Enum`, `GetIntAsync` and `GetStringAsync`;
WebChat's has `RemoveAsync` and an interface. Neither is a subset of the other, and
ADR-0007's family table reduces the dashboard's 41 call sites to two before any
sharing could happen.

**The two connections need different things.** The chat connection resolves its URL
from configuration and detects a reverse proxy, tunes server timeout and keep-alive,
retries aggressively, probes a suspected half-open transport, rebuilds it, and runs
session recovery afterwards to re-identify a user and rejoin a space. The metrics
connection has no user, no space, no session and no rebuild; what it needs is a
retry that never gives up, a loop around its own initial start, and catch-up.

What is left that is genuinely identical is `IAction`, which is one line in each
project.

## Decision

`WebChat.Client` and `Dashboard.Client` keep separate state, storage and connection
code. No shared Blazor class library is created.

The two clients do share vocabulary. `CONTEXT.md`'s "Client live connection" section
defines live connection, hub connection, rebuild, reconnect, becoming live,
connection epoch and catch-up for both of them. A shared term there names the same
concept in both clients and does not imply a shared type.

## Considered options

**One shared Blazor class library, dashboard adapts.** The surveyed proposal.
Rejected because the deletion test fails: complexity moves rather than vanishing.
The library would have to either widen `Store<TState>` until it serves two dispatch
models or force a rewrite of the dashboard's ten stores, and would take a union of
two local storage services with it. The cost is a new project, a rewritten state
layer and a widened base class; the return is one line of `IAction`.

**Share only the pieces that are already identical.** `IAction` alone, or `IAction`
plus a local storage interface. Rejected because a class library existing to hold
one line is worse than the line, and because a nearly-empty shared project invites
the next reviewer to push more into it on the same reasoning being rejected here.

**Share the connection seam only, extracted from the chat live connection.** The
candidate's own sequencing note asked for this. Rejected because the requirements
are disjoint: the dashboard would take a rebuild path, a probe, a foreground policy
and a session-recovery collaborator it has no use for, in order to avoid writing a
retry policy and a start loop. The chat spec at
`.scratch/chat-live-connection/spec.md` had already reached the same conclusion from
the other side.

## Consequences

- Adapter counting was rejected as an argument in ADR-0001. File-name matching is
  rejected here on the same footing. A future review proposing a shared Blazor
  library should start from the four points above rather than from the resemblance.
- The two clients will keep drifting in their internals, and that is accepted. What
  they are held to is the shared glossary, not a shared base class.
- Each client fixes its own connection defects. The dashboard's are real and are
  tracked as the reframed candidate 11: a bare `WithAutomaticReconnect()` that stops
  permanently after roughly 42 seconds, an initial start that cannot be retried, and
  a reconnect that flips a flag without catching up.
- If a third browser client is ever added, this record should be reopened. It rests
  on there being two clients with disjoint needs, not on a principle against sharing.
