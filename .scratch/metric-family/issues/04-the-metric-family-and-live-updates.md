# 04 — The metric family, and live updates through it

**What to build:** Watching the dashboard during a busy turn stops costing the
observability service one full Redis aggregation per event, and every family refreshes
using whatever the user actually picked.

Today each live event immediately refetches its family's whole breakdown and cancels the
request before it. Cancelling a request from a WebAssembly client does not stop the
server, which has already started reading every event in the range and grouping it in
memory. A turn emitting twenty events makes the server do twenty aggregations, and the
browser reads one.

Introduce the metric family as a value. A family knows its name, the prefix its
preferences are saved under, how to load its events and how to refresh its breakdown.
One registration site declares all seven — this is the only place a family is declared,
and the whole family is declared here even though this ticket wires only the refresh
side. The family is identified by name, not typed by its dimension and metric enums;
`docs/adr/0007-a-metric-family-is-named-not-typed.md` records why and what the generic
alternative would have cost.

Refreshing a family has a stated contract. Awaiting it means the family's breakdown
reflects the store state at or after the call. Concurrent callers share the run already
in flight, and the run repeats once if the state changed while it was running. There is
no timer and no debounce, so nothing gets slower. It reports failure by throwing to
everyone awaiting it and does not swallow.

The live-update effect becomes a table walk: update the store from the event, refresh the
family. Its seven cancellation token sources and seven near-identical refresh methods go,
and its seven copies of the swallow-and-keep-the-last-value rule become one.

This is the only behaviour change in the whole feature. A user watching a burst now sees
an intermediate value briefly before the final one, where before the intermediate was
cancelled and never shown. Both settle on the same number.

**Blocked by:** 03 — every family entry calls the single grouped request.

**Status:** ready-for-agent

- [ ] A metric family carries its name, its preference prefix, and both operations: load my events, refresh my breakdown.
- [ ] One registration site declares all seven families and is the only place a family is declared.
- [ ] Awaiting a family's refresh guarantees its breakdown reflects state at or after the call.
- [ ] Triggering a family repeatedly while a response is outstanding produces two requests, not one per trigger, and the breakdown ends at the last request's value.
- [ ] Refreshing never adds latency: no timer, no waiting window.
- [ ] A failing refresh throws to its caller rather than swallowing.
- [ ] The live-update effect holds no per-family cancellation state and no per-family refresh method.
- [ ] A failure reaching the live-update path leaves that family's breakdown at its last known value, and this rule is written once.
- [ ] A parameterised test over all seven families asserts that a refresh sends every choice currently held in that family's store, and writes the response into that family's breakdown. This is the general form of the aggregation bug and gives the six uncovered families their first unit coverage.
- [ ] The existing rapid-events test is replaced by one asserting the coalescing count, keeping the same parameterisation over families and the same staged-delay technique.
- [ ] The voice aggregation regression test still passes.
- [ ] The dashboard's Playwright suite passes unchanged; if the real-time test becomes flaky, treat that as a signal about coalescing rather than lengthening its waits.
