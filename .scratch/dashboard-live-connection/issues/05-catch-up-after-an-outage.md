# 05 — Catch-up after an outage

**What to build:** After the dashboard recovers from an outage, the numbers on screen are
right again. The event tables include what arrived while it was disconnected, the totals
account for it, and the charts are drawn from the full set.

Today none of that happens. The metrics hub pushes what happens while somebody is
attached and never replays a gap, and the dashboard does nothing on recovery but flip a
flag. Every event from the outage is missing from the tables, the totals and the charts,
under an indicator that says the dashboard is live. It stays missing until the user
changes a pill or reloads the page.

Add catch-up as the last step of becoming live. It is a named collaborator with a single
asynchronous operation, registered in the container and injected into the module. Its
implementation reloads every metric family for the range the families currently hold —
the same work a page load does, which after the metric family change is a walk of the
family table. The user's group-by, metric and time choices are untouched, so catch-up
does not move the page under them.

Catch-up never runs on the first connection, where ordinary page load fetches the same
data; running it there would double every request on first paint. The rule is keyed off a
connection epoch: an integer on the connection state, incremented every time the client
becomes live.

It is awaited as part of becoming live rather than detached, so a completed connect means
what is on screen is current. A failure inside catch-up is caught and leaves the previous
values in place. It does not fail the connection, which is live whether or not the reload
worked.

One thing worth knowing before someone simplifies the epoch away. In the chat client the
epoch closes a race, where a rebuild completes before anyone observes a disconnected
state in between. That race cannot happen here: there is no rebuild, and the transport
always announces that it is reconnecting before it announces that it has reconnected. The
epoch is here for vocabulary shared with the chat client and because it makes this rule a
comparison assertable against the store rather than a private flag. There is no
correctness argument underneath it.

**Blocked by:** 03 — catch-up is a step in the module's sequence.

Note: this and ticket 04 both edit the connection state record. Neither gates the other;
whichever lands second rebases.

**Status:** ready-for-agent

- [x] The connection state carries an epoch, incremented every time the client becomes live.
- [x] The epoch does not advance on the connecting or reconnecting transitions.
- [x] Catch-up is a named collaborator with one asynchronous operation, registered in the container.
- [x] The module awaits catch-up as the last step of becoming live.
- [x] Catch-up reloads every metric family for the range the families currently hold.
- [x] The user's group-by, metric and time choices are unchanged by a catch-up.
- [x] Catch-up does not run on the first connection.
- [x] A failure inside catch-up leaves the previous values in place and leaves the connection live.
- [x] After a reconnect, a store holds data it did not hold before, asserted at the store rather than by recording a call.
