# 06 — Give message identity one owner

**What to build:** a caller who dispatches an add-message action can predict whether the message will appear. Today three separate registries decide and they disagree — the finalized ids on the messages state, a private dictionary in the message pipeline held under a lock, and a set local to a single stream-processing call.

The local set is the one that misleads. It lives for one stream, so a message id committed by an earlier stream is unknown to the next one. That next stream dispatches an add, the reducer's dedup recognises the id and drops it, and the message silently never appears. This is the user-visible bug: a reply that should have updated a bubble vanishes instead.

The finalized ids on the messages state become the only registry. The pipeline reads that state instead of keeping its own dictionary. The stream processor drops its local set and reads the same state. An id committed by any earlier path is then known to every later one, so a repeat routes through an update and the bubble changes in place.

Deleting the pipeline's dictionary empties its clear-topic method. Its only caller dispatches the clear-messages action on the line above, and the messages reducer already drops that topic's finalized ids while handling it. So clear-topic leaves the pipeline interface, and the call site loses one line.

**Widening the set is intended.** Reading from state means ids finalized by a history load or by the send-message effect are now visible to a stream that did not commit them itself. Such a stream routes through an update rather than dispatching an add the reducer would drop. That is the behaviour the comment in the stream processor describes wanting. There is no follow-up ticket to narrow it back.

This is the only part of the slice-shape work that changes what a user sees, and it touches the foreground-resume path that two recent commits fixed. Land it on its own and review it on its own.

**Blocked by:** 05 — Collapse the ten slices to two files each.

**Status:** done

- [x] The pipeline no longer holds a finalized-ids dictionary and no longer locks around message identity.
- [x] The stream processor no longer holds a per-stream committed set.
- [x] The finalized ids on the messages state are the only place a committed message id is recorded.
- [x] A message id committed during one stream is treated as committed by a later stream in the same topic, and a repeat updates the existing message rather than being dropped.
- [x] A message id present from a history load is treated as committed by a subsequent stream.
- [x] The pipeline's finalized count is derived from the messages state and reports the same number the old dictionary did for the paths that populated both.
- [x] The clear-topic method is gone from the pipeline interface and the implementation, and its call site in the topic delete effect is removed.
- [x] Deleting a topic still clears both its messages and its finalized ids, through the clear-messages action alone.
- [x] The two existing pipeline tests that covered clear-topic are rewritten against the clear-messages action.
- [x] The existing stream processing and message store tests pass, with edits only where the widened set genuinely changes an asserted outcome.
- [x] Any test edited for the widened behaviour has a comment naming why the expectation moved.

## Comments

**Two counting paths diverge, both harmlessly.** `FinalizeMessage` used to record
a message id even when there was no streaming content to commit; now the id
enters state only through the `AddMessage` that carries it. A finalize with no
content therefore no longer bumps `FinalizedCount`. Nothing observable follows
from it: the second finalize for that id dispatches nothing either way.

`LoadHistory` used to union history ids into the pipeline's dictionary, while the
`MessagesLoaded` reducer replaces the topic's set. After a history reload the
registry now describes exactly the messages the client holds, which is the point
of having one owner.

**No test needed editing for the widened behaviour.** The two new
`StreamingService` tests are the widening; every existing stream and message
test passed unchanged.

**Two more divergences checked and left alone, both unreachable in production.**

`FinalizeMessage` no longer records an id when there is no streaming content to
commit, so in principle a later chunk for that id would not be guarded. Both
call sites — `SendMessageEffect.cs:117` and `HubEventDispatcher.cs:124` — test
`currentContent?.HasContent == true` before calling, and nothing dispatches
between that test and the pipeline's own read of the same store. A no-content
finalize is reachable only from a direct test call.

`MessagesLoaded` replaces a topic's finalized set where the old dictionary
unioned into it, so the registry can now shrink. Every `LoadHistory` caller is
guarded by `MessagesByTopic.ContainsKey(topicId)` being false, and the two
dictionaries are only ever written together, so the set it replaces is empty.
The unguarded path is `ReconnectionEffect`, which dispatches `MessagesLoaded`
with the server's history after a reconnect: there the same action also replaces
the message list, so an id that disappears takes its message with it. Under the
old code that combination was the bug — the dictionary said finalized while the
state held no such message, and it could never come back.
