# 03 — Pin the connection transition table

**What to build:** the connection slice gets its first test. It is the transition table that decides what the UI shows when the hub connects, drops, retries and recovers, and both the reconnection effect and the chat connection service depend on it. Nothing pins any of it today, so the slice collapse in ticket 05 would be refactoring untested code.

Characterize what the reducer does now, not what it ought to do. If a transition looks wrong, write the test for the current behaviour and say so in the ticket's closing note rather than fixing it here — a characterization test that encodes a fix cannot tell you whether the refactor preserved behaviour.

The store is already constructed directly in the reconnection effect's tests, so the construction pattern is settled: build a dispatcher, build the store on it, dispatch, assert on the resulting state.

The three actions ticket 02 removes are gone by the time this starts. Do not pin them.

**Blocked by:** 02 — Delete the state code nobody calls.

**Status:** ready-for-agent

- [ ] Every reducer arm that survives ticket 02 has at least one test asserting the state it produces.
- [ ] Each connection lifecycle transition — connecting, connected, reconnecting, reconnected, closed — is covered.
- [ ] Tests assert on observable state, never on which handlers were registered.
- [ ] No test references any of the three actions deleted in ticket 02.
- [ ] The tests construct the store through a real dispatcher rather than calling the reducer directly, so they survive the collapse in ticket 05 unchanged.
- [ ] Any transition that looks incorrect is pinned as-is and noted, not corrected.
