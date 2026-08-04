# 04 — Connection status on every page

**What to build:** Someone parked on the tokens page during an outage can see that the
dashboard is not live. Today only the overview page shows a connection indicator, so on
the other eight pages a stale chart is indistinguishable from a current one.

They can also tell the difference between a dashboard that is recovering and one that
has not connected yet. Today both are the same boolean false. Knowing which one it is
tells someone whether to wait or to go and check the agent.

Connection status widens from a boolean to three named states: connecting, live and
reconnecting. There is no permanent disconnected state, because the module never stops
trying, so the honest distinction is between never having been up and having lost it.
The module publishes the states as part of becoming live and in response to the
transport's lifecycle events.

The indicator moves into the layout so all nine pages show it. The connection store is
the single source, and the overview page drops its own reading in favour of it, keeping
whatever presentation its header wants.

**Blocked by:** 03 — the module publishes the states.

Note: this and ticket 05 both edit the connection state record. Neither gates the other;
whichever lands second rebases.

**Status:** ready-for-agent

- [x] Connection status is three named states — connecting, live, reconnecting — rather than a boolean.
- [x] There is no permanent disconnected state.
- [x] The live connection module publishes the states; nothing else does.
- [x] The connection store is the only source of connection status in the dashboard.
- [x] The indicator renders in the layout, so all nine pages show it.
- [x] The overview page reads the store rather than holding its own connection reading.
- [x] Each of the three states is reachable and asserted.
