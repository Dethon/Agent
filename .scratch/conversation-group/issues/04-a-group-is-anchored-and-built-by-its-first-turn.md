# 04 — A group is anchored and built by its first turn

**What to build:** Clearing a stale conversation costs nothing.

Today a group whose first message is a chat command still builds a full agent, restores its
thread from the state store and connects every MCP endpoint to list tools and fetch prompts,
then throws all of it away when the command ends the group. After this ticket a group builds
none of that until it has a turn to run: no agent, no thread read, no MCP connection, no
target resolution and no minted conversation.

A conversation group resolves its anchors from its first turn instead of its first message.
Since a chat command is not a turn, the message the anchors came from and the first turn the
group ran are the same message by construction. The message index is deleted rather than
renamed: whether a turn uses the group anchors is answered by identity against the anchor
message, or by the turn carrying reply-to targets, which is the same rule the index encoded
and no longer depends on a command having torn the group down.

The thread context and its completion callback stay eager. The thread resolver only deletes
persisted state when it finds a live context, so a clear must still find one — deferring the
context would stop a clear wiping the stored thread, which is the case this ticket exists to
serve, and would leave nothing to end the group.

Recorded as `docs/adr/0006-a-group-is-anchored-and-built-by-its-first-turn.md`.

**Blocked by:** 03.

**Status:** done

- [x] A group whose first message is a chat command constructs no agent. Asserted as a test, red before the change.
- [x] A group whose first message is a chat command resolves no delivery targets and mints no conversation. Asserted as a test, red before the change.
- [x] A leading clear still wipes the persisted thread and still ends the group.
- [x] The turn following a chat command is routed to the channel that sent it. Asserted as a test, green before and after, since this is the case the deleted invariant used to cover.
- [x] The group's anchors, agent, thread and warmup are established on its first turn, and later turns reuse them.
- [x] The message index no longer exists, and no comment specifies it.
- [x] Whether a turn uses the group anchors is decided by identity against the anchor message or by the turn carrying reply-to targets.
- [x] The warmup is still started before the turn-start announce and before the user message is built, so it still overlaps both.
- [x] First-reply latency still starts when the turn starts, and its comment says what it measures instead of claiming to cover the user's whole wait.
- [x] The project instruction describing the turn-start announce names whatever now makes the call, if the change makes it inaccurate.
- [x] The rest of the existing monitor test suite passes unchanged.
