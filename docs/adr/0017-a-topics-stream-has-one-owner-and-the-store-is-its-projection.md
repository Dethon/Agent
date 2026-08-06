# 0017 — A topic's stream has one owner and the store is its projection

Status: accepted
Date: 2026-08-06

## Context

Everywhere else in the chat client the store is the truth. A service dispatches an action, a
reducer folds it into state, and components subscribe to what comes out. Whether a topic has a
reply in flight did not work that way, and it did not work like anything else either — it was
one fact kept in five places and written from six.

The stream's task lived in a per-topic map inside `ActiveStreams`. Whether the topic was
streaming lived again as a set in `StreamingState`, and the text arriving lived again as a
buffer dictionary beside it. Whether the topic was being resumed lived as a third set in the
same state. The text arriving lived a fifth time as a local accumulator inside the chunk loop,
which sent the whole accumulated message on every chunk, so the store's copy was a mirror of
it. The streaming service, the hub event dispatcher, the message pipeline, the stream resume
service, the send-message effect and the topic-delete effect all wrote one copy or another.

Because no module owned the fact, each fix landed as a guard at a seam between two copies
rather than as a change to the rule. The reducer learned to drop a chunk for a topic that was
not in the streaming set, because a tool-call push could create a live buffer for a topic with
no stream. `ActiveStreams.Forget` learned to compare by task value, because a stream's cleanup
could land after the user had sent again and forgetting by topic alone would let the next send
open a second stream over a live one. The resume service asked "is this topic already
streaming" three times in twenty lines against three different staleness windows, because no
one answer was authoritative.

## Decision

**One module owns whether a topic has a reply in flight, and the store's streaming slice is the
projection that module publishes for rendering.**

`TopicStreams` holds one record per topic in one of three states — no stream, resuming,
streaming — and is the only thing that moves a topic between them. Resuming is a state of the
same record rather than a separate set, so a resume claims the topic before it asks the server
anything and upgrades in place when it finds a reply to take over.

Opening a topic stream hands back a **stream lease**, or nothing when the topic already has
one. The lease is what the opener holds and the only way to add to that stream or end it. Its
own identity is the stream's identity: the module compares the lease presented against the one
currently holding the topic, so a lease that has been replaced can do nothing at all.

Callers that legitimately touch a topic stream without having opened it — another person's
message, the stop button, a topic being deleted — get topic-keyed verbs instead, and each does
nothing on a topic with no reply in flight.

`TopicStreams` dispatches the same stream actions the streaming service used to dispatch, as it
transitions. Every component subscription, the render coordinator's sampling, the unread-count
and agent-activity selectors and the browser suite read the projection exactly as before.

## Considered options

**Keep the store as the truth and make the streaming state richer.** Fold the task and the
accumulator into `StreamingState` so one reducer owns everything. It would have kept the
client's usual shape. Rejected because a reducer cannot hold a running task or hand out an
identity, so "this ending belongs to the stream that is no longer here" would still have to be
a comparison somebody writes at each call site — the same class of guard, in a new place.

**Leave the copies and add an invariant test over them.** Cheapest, and it would have caught
the two bugs that were found. Rejected because it pins the copies in place: the next writer is
still free to add a sixth, and the test only says they disagree, not which one is right.

**Give the streaming service the ownership rather than a new module.** It already held the task
map. Rejected because the callers that need to ask are not the callers that send: the hub
dispatcher and the topic-delete effect would have had to depend on the service that reaches
back through the live connection, to ask a question that touches no transport.

## Consequences

- Four shapes stop being expressible rather than being guarded against: a live buffer for a
  topic with no reply in flight, an ending from one stream clearing another's state, two
  replies in flight on one topic, and a stream nothing is tracking.
- This slice reads differently from the rest of the client. A reader who meets a component
  subscription first will see a store that services no longer write into and may take it for a
  mistake. The uniformity is the price: it is given up in exchange for an invariant a new
  caller cannot break, because there is no verb that breaks it.
- Writing the streaming slice is now a compile-time question, not a convention. Reading it
  stays free — `StreamingStore` keeps its shape and its observable.
- The reducer's guard against a chunk for an idle topic is gone, so a future writer that
  dispatches `StreamChunk` directly would re-create the phantom buffer. What stops that is that
  the action has one dispatcher and no reason to gain another.
- A stale lease fails silently. That is what makes the late-cleanup case safe, and it also
  means a genuine misuse — holding a lease past the stream it opened — produces nothing rather
  than an error.
