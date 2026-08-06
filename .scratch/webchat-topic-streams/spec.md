# A topic's stream has one owner

Status: ready-for-agent

## Problem Statement

In the chat client, "this topic has a reply in flight" is one fact stored in five places and
written from six.

The stream's task lives in a per-topic map inside `ActiveStreams`. Whether the topic is
streaming lives again as a set in `StreamingState`, and the text arriving lives again as a
buffer dictionary beside it. Whether the topic is being resumed lives as a third set in the
same state. The text arriving lives a fifth time as a local accumulator inside the chunk loop,
which sends the whole accumulated message on every chunk so the store's copy is a mirror of it.

Six places write those copies: the streaming service, the hub event dispatcher (stream
endings, tool-call pushes, approval-resolved pushes), the message pipeline, the stream resume
service, the send-message effect (cancel), and the topic-delete effect (cancel). The chat input
dispatches a cancel of its own.

Because no single module owns the fact, every recent fix is a guard at a seam between two
copies rather than a change to the rule:

- The phantom-buffer fix made the reducer defend itself against dispatchers that do not consult
  the task map: a tool-call push for an idle topic used to create a live buffer for a topic with
  no stream.
- The forget-by-value fix exists because two writers can flip the store's copy independently: a
  stream's cleanup can land after the user has sent again, and forgetting by topic alone would
  let the next send open a second stream over a live one.
- The resume service asks "is this topic already streaming" three times in twenty lines against
  three different staleness windows, because no one answer is authoritative.

The same shapelessness shows up in the code around it. Two public streaming verbs exist that no
production caller uses, and both bypass the task map entirely, so a stream can be opened with
nothing tracking it. The hub event dispatcher maps two stream-ended notifications that no server
in the repo ever sends. The resume service needs eight collaborators to construct. Nine test
files poll for streaming state on a five-second loop because no transition is observably
complete.

## Solution

One module owns the fact, and everything else either asks it or renders what it publishes.

**A topic stream** is a topic's one reply in flight, from the send or resume that opened it to
its single ending. A new `TopicStreams` module holds one record per topic — nothing, resuming,
or streaming — and is the only thing that can move a topic between those states.

Opening a topic stream hands back a **stream lease**. The lease is what the opener holds, and it
is the only way to add to that topic stream or end it. Its identity *is* the stream's identity,
so a lease that no longer holds the topic can neither append nor end anything: the
forget-by-value rule becomes structural rather than a comparison someone remembered to write.

Callers that legitimately touch a topic stream without having opened it — a tool-call push
arriving over the hub, another user's message finalising ours, the stop button, a topic being
deleted — get topic-keyed verbs on the module instead. Each does nothing on a topic with no
stream, so "a chunk for an idle topic" is answered once, in the module, and the reducer's guard
against it is deleted.

`StreamingStore` stops being a parallel truth and becomes the module's projection for
rendering. The module dispatches the same actions the streaming service dispatches today, so
every component subscription, the render coordinator's sampling, the unread-count and
agent-activity selectors, and the E2E suite keep working with no change.

What that makes impossible, rather than merely guarded against: a live buffer for a topic with
no stream, an ending from one stream clearing another's state, two streams open on one topic,
and a stream nobody is tracking.

## User Stories

1. As a chat user, I want a topic to have at most one reply in flight, so that I never see the
   same answer written twice into one conversation.
2. As a chat user, I want my second message to join the reply already being written, so that
   the agent answers both instead of racing itself.
3. As a chat user, I want a tool call that finishes after the reply ended to leave nothing
   behind, so that an idle conversation does not sprout an empty streaming bubble.
4. As a chat user, I want an approval I resolve after the reply ended to be equally harmless,
   so that answering a prompt late cannot revive a finished conversation.
5. As a chat user, I want the stop button to keep the part of the answer I already received, so
   that stopping a long reply does not throw away what it said.
6. As a chat user, I want the stop button to be reliable regardless of how long the answer has
   been running, so that stopping never depends on timing.
7. As a chat user, I want the cleanup of an old reply not to disturb the new one, so that
   sending again immediately after a reply ends behaves like sending at any other time.
8. As a chat user, I want a reply that started while I was disconnected to be picked up exactly
   once when I come back, so that reconnecting does not duplicate the answer.
9. As a chat user, I want reconnecting twice in quick succession to still resume once, so that a
   flaky network does not multiply the reply.
10. As a chat user, I want the typing indicator in the sidebar to be true, so that a topic shows
    as busy exactly while it is busy.
11. As a chat user, I want the header and the input box to agree about whether the agent is
    answering, so that the stop button is present exactly when there is something to stop.
12. As a chat user, I want unread counts to stay correct across a reply I never opened, so that
    the sidebar does not lie about what I have seen.
13. As a chat user, I want deleting a topic mid-reply to end that reply completely, so that a
    deleted conversation stops producing.
14. As a chat user, I want a reply that interleaves several messages to render each one whole,
    so that tool calls arriving out of order do not shuffle the text.
15. As a chat user, I want another person's message arriving mid-reply to close off what the
    agent had written so far, so that the two do not merge into one bubble.
16. As a developer, I want one module to answer whether a topic is streaming, so that a new
    caller cannot get a different answer by asking a different place.
17. As a developer, I want the only way to append to a topic stream to be holding its lease, so
    that a future feature cannot append from a fourth site.
18. As a developer, I want a stale lease's operations to be no-ops, so that the
    late-cleanup class of bug cannot come back.
19. As a developer, I want no way to open an untracked stream, so that the tracked path is the
    only path.
20. As a developer, I want the resume decision made once, so that a fix to it cannot land in two
    of the three places that used to make it.
21. As a developer, I want the resume service to need fewer collaborators, so that a test about
    resuming does not have to build the whole client.
22. As a developer, I want to await a topic stream's completion in a test, so that streaming
    tests assert a transition instead of polling for five seconds.
23. As a developer, I want the notification types the client handles to be ones some server
    actually sends, so that reading the dispatcher tells me what can happen.
24. As a developer, I want the store's streaming slice to be a projection, so that I know
    reading it is safe and writing it is not mine to do.
25. As a developer new to the client, I want the glossary to tell me what a topic stream and a
    stream lease are, so that "stream", "buffer" and "resuming" stop being used loosely across
    six files.
26. As a developer, I want an ADR explaining why this one slice inverts the client's
    store-as-truth pattern, so that I do not "fix" it back.

## Implementation Decisions

### The module and its record

- A new `TopicStreams` module holds one record per topic with three states: no stream, resuming,
  streaming. It is registered in the client's DI container and is the only writer of the
  streaming slice of state.
- Resuming is a state of the same record, not a separate set. A resume begins as `Resuming` and
  upgrades in place to `Streaming`, so the three staleness checks in the resume service collapse
  into one call that either grants a lease or refuses.
- The streaming state carries the stream's task, the current accumulating assistant message and
  the current message id.

### The lease

- Opening a topic stream returns a `StreamLease` or nothing, where nothing means the topic
  already has one. The lease's own identity is the stream's identity: the module compares the
  lease presented against the one currently holding the topic, and a lease that no longer holds
  it can do nothing.
- Lease verbs: `Append` (adds a chunk and returns the accumulated assistant message),
  `StartMessage` (a turn boundary — commits and resets the accumulator for a new message id),
  `Complete` (the single ending).
- Topic-keyed verbs on the module, each a no-op when the topic has no stream: `Append`,
  `FinalizeCurrent`, `End`. These serve the callers that hold no lease.
- `Snapshot(topicId)` answers what a topic's stream currently is, for callers that need to ask
  rather than write.
- The lease exposes an awaitable completion, so a test can wait for a real transition.

### Cancellation

- Cancel does **not** carry a cancellation token into the chunk loop. The server closes the
  stream when the client asks it to cancel a topic, so the loop ends by itself.
- Cancel ends the lease: the topic goes back to having no stream and the accumulated text is
  committed as a message. The loop drains whatever the transport already delivered, and its
  later `Complete` is a no-op because its lease is stale. This preserves today's observable
  behaviour exactly and avoids truncating text that had already arrived.

### The projection

- `TopicStreams` dispatches the existing stream actions — started, chunk, completed, reset — as
  it transitions. One owner writes both the truth and its projection; no intermediate effect
  translates between them.
- `StreamingStore` keeps its shape minus the resuming set, and keeps publishing its observable.
  No component, selector, or render-coordinator change.
- The reducer's guard that drops a chunk for a topic not in the streaming set is deleted: the
  module no longer emits one.

### What moves and what goes

- Stream-state questions move off `IStreamingService` onto `TopicStreams`. `IStreamingService`
  keeps sending a message and the resume entry point.
- The chunk loop drops its local accumulator and length counters in favour of what `Append`
  returns. The per-message-id stash for interleaved messages stays local to the loop: it is
  per-message display state, not stream state.
- The message pipeline stops writing streaming state. Its finalise path calls the module's
  topic-keyed verb, so the reset action ends up with exactly one dispatcher.
- The two public streaming verbs with no production caller are deleted from the interface and the
  service. Their tests are re-pointed at the tracked entry points.
- `ActiveStreams` is absorbed into `TopicStreams` and deleted.
- The hub event dispatcher's branches for stream-completed and stream-cancelled notifications are
  deleted, and `StreamChangeType` is narrowed to the one case a server actually sends. Cancel and
  completion remain entirely client-side facts.

### Sequencing

Eight tickets, each red-green, each leaving the suite green and the client working. The
deletions come first as prefactors: removing the two dead verbs before anything is migrated
halves the entry points the migration has to move, and removing the unreachable notification
branches shrinks the dispatcher before a ticket touches it.

1. Delete the two untracked streaming verbs; re-point their tests.
2. Delete the unreachable stream-ended notification branches; narrow the change-type enum.
3. `TopicStreams` and `StreamLease` with tests only: lease identity, stale-lease no-ops,
   idle-topic no-ops, the three-state record.
4. The streaming service opens and drives streams through the module and drops its own state.
   Blocked by 1 and 3.
5. The hub event dispatcher and the message pipeline move onto the topic-keyed verbs; the
   reducer guard is deleted. Blocked by 2 and 4.
6. Resume moves into the module; the resuming set collapses into the record. Blocked by 5. The
   WebChat E2E suite runs here, being the last ticket that can change what the projection
   publishes.
7. The nine streaming test files await completion instead of polling. Blocked by 6.
8. ADR-0017 and the `CONTEXT.md` **Chat streaming** section. Blocked by 6.

Tickets 1, 2 and 3 have no blockers and can start in any order.

### Documentation

- ADR-0017, "a topic's stream has one owner and the store is its projection". It is worth
  recording because everywhere else in this client the store is the truth and services dispatch
  into it; this slice inverts that, and a future reader will otherwise try to restore the
  pattern.
- `CONTEXT.md` gains a **Chat streaming** section with two terms:
  - **Topic stream** — a topic's one reply in flight, from the send or resume that opened it to
    its single ending. _Avoid_: active stream, streaming state, stream session.
  - **Stream lease** — what the opener of a topic stream holds. It is the only way to add to
    that stream or end it, and a stale one can do neither. _Avoid_: stream handle, stream token,
    stream id.

## Testing Decisions

A good test here asserts what a person using the chat client would notice, or what a caller of
the module would observe: whether a topic reads as streaming, whether a buffer exists, what text
ends up committed as a message, how many streams a send opened. It does not assert that a
particular action was dispatched, that a private field changed, or that two internal copies of
the state agree — those are the things this change is removing.

Two seams, and the total does not grow:

1. **`ScriptedChatClient`** (`Tests/Unit/WebChat.Client/Fixtures/ScriptedChatClient.cs`) is the
   primary seam and the highest one available: the whole client wired by the same registration
   extensions the browser uses, with only the transport scripted. Chunk sequences are scripted
   through `FakeHubConnection.StreamAsync`. The invariants belong here — a send opens one stream,
   a second send joins it, a tool-call push on an idle topic leaves no buffer, cancel commits the
   text that arrived, an old stream's ending does not clear a newer one, a double reconnect
   resumes once. Prior art: `NotLiveUserActionTests`, `NotLiveRecoveryTests`,
   `NotLiveReadTests`, `NotLiveRemainingReadTests`.
2. **`TopicStreams` unit tests replace `ActiveStreamsTests`** for lease identity, stale-lease
   no-ops and idle-topic no-ops. This is the same level as the file it retires, so one seam dies
   as one is born. Prior art: `ActiveStreamsTests` itself.

Relocations and retirements:

- `StreamingServiceTests` stays at the streaming-service seam. Its roughly fifty tests are
  re-pointed from the two deleted verbs onto the tracked entry points. Chunk interleaving, turn
  finalisation and error classification are properties of the chunk loop and belong next to it.
- The phantom-buffer test in `StreamingStoreTests` moves up to seam 1, because the reducer guard
  it covers is deleted.
- The two dead-branch tests in `HubEventDispatcherTests` are deleted with the branches they
  cover.
- Streaming tests stop polling. The nine files that poll for streaming state on a five-second
  loop await the lease's completion instead. The five files that poll for unrelated reasons are
  left alone.

Gating: unit tests on every ticket. The WebChat E2E suite runs once, in ticket 6 — the last
ticket that can change what the projection publishes. It is the real check that the Cancel
button and the sidebar streaming indicator still behave, since both key off the projection.

## Out of Scope

- The dashboard client. It has its own connection and metrics state, and ADR-0008 keeps the two
  browser clients separate; nothing here is shared with it.
- A server-pushed end to a stream. No server sends one today, so designing the client's
  behaviour for it would be designing for an untriggerable case. If one is ever wanted it arrives
  as its own change, with a sender.
- The per-message-id interleaving mechanism. It stays as it is, local to the chunk loop.
- Message identity, the finalised-message set, and history loading. The module owns whether a
  topic is streaming, not what a message is.
- The approval overlay's own lifecycle. Resume continues to reconcile approvals exactly as it
  does now.
- The five one-line hub services. ADR-0001 and ADR-0004 keep them, and this change gives no new
  reason to revisit that.
- Removing the five-second polling from the test files that poll for reasons unrelated to
  streaming.

## Further Notes

Three claims in the architecture review that prompted this are wrong, and the spec is written
against the corrected picture. The forty-nine polling call sites are spread across the whole
WebChat test suite; only nine files touch streaming, so this change does not remove
forty-nine loops. The resume service takes eight collaborators, two of which are stores, not
seven stores and a dispatcher. And the writers number six, not five, once both cancel paths are
counted.

The review also missed a copy. Because a chunk action carries the whole accumulated message and
the reducer replaces rather than appends, the store's buffer is a mirror of the loop's local
accumulator. That is the fifth copy, and the lease owning the accumulator is what removes it.

The cheapest correctness win in this change is unrelated to the module: two public streaming
verbs exist that no production code calls and that bypass stream tracking altogether, and they
carry the bulk of the streaming service's test surface. Deleting them means a large share of the
tests stop describing a path production never takes.
