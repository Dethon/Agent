# 05 — Connection epoch replaces the disconnected-status handshake

**What to build:** after any interruption, the user gets a fresh transcript —
topics refetched, the selected topic's history reloaded, the session restarted,
streams resumed — including when the interruption was too short for a disconnected
status to be observed at all.

Today the reload only arms if the client passes through a disconnected or
reconnecting status first, so the teardown path dispatches a close by hand purely
to arm it. That makes the connection module's teardown shaped by a downstream
effect's internal flags, and it leaves a race: a rebuild fast enough that nobody
observes the gap skips the reload.

Replace the handshake with a **connection epoch** on the connection state — a
count of how many times the client has become live, incremented on both the
connected and the reconnected transitions. The reconnection effect keeps a single
record of the last epoch it reloaded for: it records the first epoch it sees
without reloading, and reloads whenever it sees a connected status at a higher
epoch. Its two booleans go, and so does the synthesized close dispatch in
teardown.

The reload body itself does not change.

Add a short comment on the epoch explaining why interruption is counted rather than
observed, so a later reader does not delete it as redundant with the status. No
ADR — this is cheap to reverse.

**Blocked by:** 03.

**Status:** done

- [x] The connection state carries an epoch, incremented every time the client
      becomes live
- [x] The epoch does not advance on the connecting or disconnected transitions
- [x] The reconnection effect reloads on a higher epoch and not on the first one it
      sees, replacing both of its booleans
- [x] A test covers a rebuild in which no disconnected status is ever observed, and
      the reload still happens
- [x] The synthesized close dispatch in teardown is removed, along with the ordering
      rule it existed for
- [x] The reload body — refetch topics, reload selected history, restart session,
      resume streams — is unchanged
- [x] A comment on the epoch records why it exists
