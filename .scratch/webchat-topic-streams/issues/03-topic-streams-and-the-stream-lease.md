# 03 — TopicStreams and the stream lease

**What to build:** the module that owns whether a topic has a reply in flight, verified on its
own before anything depends on it.

`TopicStreams` holds one record per topic in one of three states: no stream, resuming, or
streaming. Asking it to open a topic stream either hands back a **stream lease** or refuses
because the topic already has one. The lease is what the opener holds and the only way to add to
that topic stream or end it. Its identity is the stream's identity, so a lease that no longer
holds the topic can do nothing at all — the late-cleanup bug where a finishing stream clears the
state of the newer stream that replaced it becomes impossible to express rather than something a
comparison has to catch.

Callers that legitimately touch a topic stream without having opened it get topic-keyed verbs
instead, and each does nothing when the topic has no stream. That is where "a chunk for an idle
topic" is answered, once.

Nothing calls the module yet. The existing per-topic task map stays in use until the next ticket.

**Blocked by:** None — can start immediately.

**Status:** resolved

- [x] Opening a topic stream on a topic with none returns a lease; opening one on a topic that
      already has a stream refuses, and the first lease is unaffected.
- [x] A lease that still holds its topic can append a chunk, start a new message, and end the
      stream.
- [x] A lease that no longer holds its topic can do none of those: appending, starting a message
      and ending are all no-ops, and in particular a stale lease ending does not disturb the
      stream that currently holds the topic.
- [x] The topic-keyed verbs — append, finalise the current message, end — each do nothing on a
      topic with no stream, and do the corresponding thing on a topic that has one.
- [x] A topic can be moved into resuming and upgraded in place to streaming; a resume attempt on
      a topic already resuming or already streaming refuses.
- [x] The record carries the stream's task, the accumulating assistant message and the current
      message id, and a snapshot answers what a topic's stream currently is.
- [x] A lease exposes an awaitable completion that finishes when the stream ends, whichever way
      it ended.
- [x] Appending returns the accumulated assistant message, so a caller has no reason to keep its
      own copy.
- [x] Tests replace the existing per-topic-task-map tests at the same level; those are retired,
      not duplicated.
- [x] `dotnet test` on `Tests/Unit` is green.
- [x] `dotnet format` has run over the staged files.
