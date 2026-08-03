# 06 — The connection store becomes the single source of status

**What to build:** one connection status story. A user sees the same answer to "am
I connected?" wherever it is shown, because there is only one place the answer
comes from.

Today there are two. Some components read the connection store like every other
piece of state in the client; two dot renderers and the space effect instead read
the live connection's own flag and subscribe to its own change event. They render
the same thing from different sources.

Move those three onto the connection store, then delete the live connection's
status surface: its connected and reconnecting flags, its state-changed event and
its reconnecting event. The last two have no consumers anywhere in the client
today — confirm that before deleting rather than trusting this ticket.

What is left on the live connection is connect, reconnect-if-needed, async
disposal, and the raw transport accessor. That accessor stays on purpose: removing
it is candidate 5's work and is out of scope here.

**Blocked by:** 03.

**Status:** ready-for-agent

- [ ] Both connection dot renderers and the space effect read connection status
      from the connection store
- [ ] The live connection no longer exposes connected or reconnecting flags, a
      state-changed event, or a reconnecting event
- [ ] Nothing outside the connection store publishes connection status
- [ ] The raw transport accessor is still present on the interface
- [ ] The status shown in the header and in the chat panel cannot disagree
