# 04 — Sends and resumes run on leases

**What to build:** every topic stream in the client is opened through a lease and appended
through it. The streaming service stops keeping its own map of streams in flight and its own copy
of the accumulating reply; both live in the module. The old per-topic task map is absorbed and
deleted.

From a user's seat nothing changes, and that is the point of this ticket: sending a message opens
one reply, sending again while it runs joins the reply already being written, stopping keeps the
text that arrived, and a reply that finishes after the user has already sent again does not
disturb the newer one. All of it now rests on one owner instead of two copies that agreed by
convention.

The per-message-id stash that lets an interleaved reply continue an earlier message stays local
to the chunk loop. It is display state for a message, not state about the topic's stream.

**Blocked by:** 01 (fewer entry points to migrate), 03 (the module must exist).

**Status:** ready-for-agent

- [ ] Sending a message to an idle topic opens exactly one topic stream, through a lease.
- [ ] Sending a message to a topic that is already streaming enqueues onto the running reply and
      opens no second stream; when the server says there was nothing to enqueue onto, a fresh
      stream opens, still through a lease.
- [ ] A resumed stream is opened through the same mechanism and refuses when the topic already
      has one.
- [ ] The chunk loop holds no accumulator, no processed-length counters and no map of streams; it
      appends to the lease and uses what the lease returns.
- [ ] The old per-topic task map type is deleted and nothing references it.
- [ ] The store receives the same actions in the same order as before this ticket — the stream
      started, each chunk with the full accumulated content, content resets at turn boundaries,
      and exactly one completion per stream.
- [ ] The stop button and a topic deletion both end the topic stream and commit the text that had
      arrived, as they do today; the drained loop's own ending afterwards changes nothing.
- [ ] At the whole-client seam: a send opens one reply; a second send does not open another; an
      old stream's ending does not clear a newer stream's state.
- [ ] `dotnet test` on `Tests/Unit` is green.
- [ ] `dotnet format` has run over the staged files.
