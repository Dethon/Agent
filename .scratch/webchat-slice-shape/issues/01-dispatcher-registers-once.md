# 01 — Make the dispatcher safe to register once

**What to build:** a store can register a single handler and be shown every action, and dispatching an action no reducer cares about costs nothing. Today a store names each action type it wants, and an action dispatched through a variable typed `IAction` matches no registration at all and silently does nothing.

Two changes make that possible. `Store.Dispatch` stops notifying subscribers when the reducer handed back the state it was given — a reducer falling through to its `_ => state` arm returns the same instance, and `with` expressions always allocate, so a reference comparison separates the two cases exactly. `Dispatcher` gains a catch-all registration that receives every action regardless of its type.

The reference comparison lands first. In the other order, a single catch-all would notify every subscriber of all ten slices on every action.

Nothing switches over to the catch-all in this ticket. `RegisterHandler<TAction>` keeps working and keeps its current behaviour, because the effects use it to subscribe to specific actions and gain nothing from seeing all of them. This is the expand half of the change; ticket 05 migrates the stores.

**Blocked by:** None — can start immediately.

**Status:** done

- [x] A subscriber receives no emission when a reducer returns the state it was passed.
- [x] A subscriber still receives an emission when a reducer returns a new state instance, including one that is equal by value to the previous state.
- [x] A catch-all registration receives an action dispatched through a variable declared as `IAction`.
- [x] A catch-all registration receives actions of every type, including types no handler was registered for.
- [x] A catch-all registered before a typed handler runs before it, for an action both would receive.
- [x] A catch-all registration can be disposed and stops receiving actions afterwards, matching the existing typed registration.
- [x] `RegisterHandler<TAction>` behaviour is unchanged and every existing test that uses it still passes.
- [x] The three ordering and emission behaviours are covered by a new unit test file for the dispatcher, written before the production change.
- [x] No test asserts on the dispatcher's internal handler storage.

## Comments

**The unchanged-state skip was confirmed against the direct subscribers, not
assumed.** The spec asks for that confirmation. Five sites subscribe to a
`StateObservable` outside `StoreSubscriberComponent`, which already applies
`DistinctUntilChanged`:

- `ReconnectionEffect` branches on `state.Status`; a skipped emission means the
  status did not change.
- `AgentSelectionEffect` compares `state.SelectedAgentId` against its own
  `_previousAgentId`.
- `AgentActivityEffect` diffs `state.StreamingTopics` against its own
  `_previousStreamingTopics`.
- `AgentSettingsEffect` diffs `state.ByAgent` against its own `_previous`.
- `RenderCoordinator` samples and then applies `DistinctUntilChanged` itself.

Each receives strictly fewer emissions and each recomputes from the state it is
handed, so the ones it loses were the ones that would have changed nothing.

**`Dispatch` now snapshots the matching handlers before invoking them.** The old
per-type dictionary enumerated the live list, so a handler that registered or
disposed during dispatch threw. One ordered list makes that reachable from more
places, so the snapshot is part of the change rather than an optimisation left
out. It costs one list allocation per dispatched action.
