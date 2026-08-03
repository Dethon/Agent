# Spec — The Conversation Group

Status: ready-for-agent

Grilled from candidate 8 of `.scratch/architecture-audit-2026-08-03/candidates.md`,
which holds the exact file and line evidence for every claim below. Decision recorded
as `docs/adr/0006-a-group-is-anchored-and-built-by-its-first-turn.md`. Vocabulary
follows `CONTEXT.md`: a **turn** is one message the agent answers, a **chat command**
is a message that steers the conversation instead of being answered, a **conversation
group** is every message for one conversation and one agent taken as a single thing,
and the **delivery identity** is the conversation a turn's replies actually land in.

## Problem Statement

Clearing a conversation costs a full agent.

The chat monitor groups incoming messages by conversation and agent, and processes
each group as a unit. It reads the group's first message and then, before looking at
what that message says, resolves the delivery targets, builds the agent, restores the
thread from the state store and starts the session warmup — which connects every MCP
endpoint, lists their tools and fetches their prompts.

Only after all of that does anything parse the message. A `/clear` or `/cancel` is
never answered by the agent; it is handed to the thread resolver, which disposes the
thread context, which ends the group. So a group whose first message is a command —
routine after an agent restart, when the user clears a conversation that has no live
group — pays for the whole apparatus and immediately throws it away.

The same ordering created a second problem. Because the group is anchored on its
first *message*, the queuing loop has to tell later stages which message that was, and
it does so with an integer index carried on the pending turn. The index counts every
message in the group, commands included. Two unrelated places read it and mean
different things by it: target resolution reads index zero as "reuse the group
anchors", and the turn-start announce reads it as "this turn's minted targets already
announced themselves". A comment above the queuing loop is the entire specification
of that rule.

The index is only correct because of an invariant nothing states. A command tears the
group down, so an index above zero always implies a preceding real turn. That chain
runs through three files and neither the command parser nor the thread resolver
mentions it. Nothing in any signature says the number means anything at all.

There is a third symptom of the same shape. The turn-start announce takes a
`skipMinted` flag because the minted marker on a delivery target goes stale: the group
anchors keep it set forever, so every later turn has to be told to disregard it. The
flag is corrected at the call site rather than being right.

The result is that a turn — the thing that queues, resolves its own delivery, gets
announced and reports its own latency — has no value anywhere. It is a tuple, an
integer and six parameters threaded through five private methods, and the rules
binding them together live in prose.

## Solution

Make the turn a value, and give the group that runs turns a module.

A conversation group becomes one thing with one job: take the group's messages and
produce reply updates. It owns the pending-turn queue, the command dispatch, the
anchors, the agent's lifetime, the restored thread and the warmup. Every ordering rule
that is a comment today — anchors before agent, commands never queue, warmup awaited
before the first stream — becomes internal to it.

The group resolves its anchors and builds everything else on its **first turn**
instead of its first message. Because a chat command is not a turn, "the message the
anchors came from" and "the first turn the group ran" are the same message by
construction. The integer is deleted rather than renamed: the group knows from its own
state whether it has anchors yet and whether this turn is the one they came from.

That also fixes the wasted work. A group whose first message is a command resolves no
targets, mints no conversation, builds no agent, reads no thread and opens no MCP
connection.

The minted marker is redefined to mean "minted while resolving this turn". Reused
anchors carry it as false, because nothing was minted for the turn reusing them. The
announce stops taking a correction flag and simply skips the targets that announced
themselves.

The chat monitor is left with what it is actually for: merge the channels, group the
messages, deliver each update to its targets, and report the first reply.

## User Stories

1. As a user clearing a stale conversation, I want the clear to cost nothing, so that the agent is not built and torn down for a message it never sees.
2. As a user clearing a stale conversation, I want the stored thread still wiped, so that the cheaper path does not quietly stop doing the one thing I asked for.
3. As a user cancelling a running turn, I want the cancel to still reach the monitor while the turn is running, so that the stop button keeps working.
4. As a user typing a second message while the first is still answering, I want it answered in order, so that a conversation stays a conversation.
5. As a user typing in WebChat inside a conversation a satellite started, I want my reply delivered back to WebChat, so that the answer appears where I asked.
6. As a user receiving a scheduled task's result, I want it delivered into a conversation I can open, so that the reply is not filed under a synthetic identifier.
7. As a user receiving an agent-initiated message into an existing conversation, I want the live stream set up before the first chunk arrives, so that I see the answer stream rather than appear all at once at the end.
8. As an operator, I want an agent restart followed by a clear not to connect every MCP endpoint, so that a no-op costs no tool discovery round trips.
9. As an operator, I want first-reply latency to keep meaning what it means today, so that the dashboard series does not step on deploy day for reasons unrelated to performance.
10. As a developer reading the queuing loop, I want to see turns and commands as two different kinds of thing, so that I do not have to work out which messages the counter is counting.
11. As a developer reading target resolution, I want "is this the turn the anchors came from" answered by identity rather than by a number, so that I do not have to reconstruct what zero meant.
12. As a developer reading the turn-start announce, I want the minted marker to be true, so that I do not have to pass a flag correcting it.
13. As a developer adding a chat command, I want to add it to the parser and nothing else, so that the group's construction order is not something I have to think about.
14. As a developer adding a field a turn carries, I want one record to add it to, so that I am not threading a seventh parameter through five private methods.
15. As a developer changing how a turn is delivered, I want the delivery to read one value, so that targets and tracker cannot get out of step with the message they belong to.
16. As a developer, I want the group's construction order expressed as the order of statements inside one module, so that reordering it is a local edit rather than a cross-file argument.
17. As a maintainer, I want the invariant "an index above zero implies a preceding turn" to stop being load-bearing, so that a change to command handling cannot silently break target resolution.
18. As a maintainer, I want the comment that specifies the counter deleted along with the counter, so that no reader is left maintaining a rule that no longer exists.
19. As a maintainer, I want the first-reply comment corrected, so that it stops claiming to measure a wait it excludes.
20. As a maintainer, I want the chat monitor to fit on a screen, so that its remaining job — merge, group, deliver — is visible without scrolling past seven private methods.
21. As a maintainer, I want a group that runs no turn to build nothing, so that "what does this group cost" has one answer rather than depending on message content.
22. As a maintainer, I want the anchoring decision recorded, so that a reviewer does not restore the eager order on the strength of the overlap comment in the history.
23. As a test author, I want to assert that a leading clear constructs no agent, so that the cheap path is pinned rather than assumed.
24. As a test author, I want to assert the group-opener rule when the first message was a command, so that the case the old invariant covered is covered by a test instead.
25. As a test author, I want to assert that a reused anchor is announced rather than skipped, so that the redefined marker is pinned at the level a user would notice it.
26. As a test author, I want these assertions to need no gate, spin-wait or timeout, so that the suite does not get slower or flakier for having covered more.
27. As a future reader, I want turn, chat command, conversation group and delivery identity defined in the glossary, so that four terms the code uses interchangeably today stop drifting.

## Implementation Decisions

**One module per conversation group.** A conversation group type is constructed per
conversation and agent, and exposes roughly "run these messages, yield reply updates"
plus disposal. It owns the pending-turn queue, the command dispatch loop, the anchors,
the agent, the restored thread, the warmup task and the running of turns. It replaces
the monitor's private turn-scope record, its three queuing and turn-running methods,
its group-anchor resolution and its per-turn target resolution.

It is internal to the domain layer's monitor namespace and is constructed only by the
chat monitor. It does not become a test seam; see the testing decisions.

**The chat monitor keeps merge, group, deliver, report.** It merges the channel
message streams, groups them, constructs a conversation group per key, and consumes
the updates: dispatch each to its targets through the reply dispatcher, and publish
first-reply latency when the first update actually delivered content. Its error
handling around the whole loop is unchanged.

**A turn becomes a record** carrying the originating channel, the channel message, the
resolved delivery targets and the first-reply tracker. It is minted at dequeue, by the
group, once per turn.

**The turn-update record collapses to the update plus the turn.** Today it carries the
update, the targets and a nullable tracker as three parallel fields. Carrying the turn
instead makes the tracker non-nullable and makes it impossible for an update to travel
with targets belonging to a different message.

**The pending-turn record and its index are deleted.** The queue carries the raw
channel-and-message pair. The queuing loop no longer counts.

**Anchors are resolved from the group's first turn.** On the first turn the group
resolves the delivery targets, derives the delivery identity and the approval channel
from them, builds the agent, restores the thread and starts the warmup without
awaiting it. Later turns reuse that agent and thread.

**The group-opener rule becomes an identity check.** A turn uses the group anchors
when its message is the message the anchors were resolved from, or when it carries
reply-to targets — re-resolving the latter would re-mint conversations. Any other turn
re-resolves against its own origin channel. This is the same rule as today, expressed
without a counter and without depending on how commands affect group lifetime.

**The thread context and its completion callback stay eager.** The thread resolver
only deletes persisted state when it finds a live context, so deferring the context
would make a leading clear stop wiping the stored thread, and would leave nothing to
end the group. The context is created and its completion callback registered before
any message is parsed, exactly as today. Only the expensive half moves.

**The minted marker on a delivery target is redefined as per-turn truth.** It means
"minted while resolving this turn". When the group reuses its anchors for a later
turn, it projects them with the marker cleared. The turn-start announce loses its
`skipMinted` parameter and skips the targets marked minted. Target resolution itself
is unchanged: it still marks a target minted when it created the conversation.

**First-reply latency keeps its current window.** The tracker is created when the turn
is dequeued, so queue wait stays excluded and the published series is continuous
across this change. The comment claiming it covers every stage the user waits on is
corrected to say what it measures: the turn, from the moment it starts, not the user's
wall clock. A separate queue-wait stage was considered and rejected as scope.

**Warmup keeps the overlap it was written for.** It starts before the turn-start
announce and before the user message is built, which are the two network stages it was
overlapping. The only overlap it loses is with the command parser, a string switch.

**The agent's disposal scope has to tolerate never being built.** The group is
disposable and disposes the agent if one exists. This replaces the monitor's
`await using` over an eagerly constructed agent.

**Nothing else changes shape.** The agent factory interface, the delivery target
resolver's resolution method and its conversation-context builder, the reply
dispatcher, the chat command parser, the thread resolver, the thread context and the
merge and grouping helpers are all untouched. The channel message contract, the
delivery target record's other fields and every published metric event keep their
current shape.

**Sequenced after the metrics publishing module.** That work's chat-monitor ticket
rewrites the exact publish sites this change relocates, against today's layout, and
asserts the existing monitor tests pass unchanged. This change lands on top of its
result, so the first-reply publish it moves is already the post-metrics one.

## Testing Decisions

A good test here asserts what a caller can observe: which agents were constructed,
which channel received which reply, which conversations were announced, and which
latency events were published. It does not assert that a private method was called and
does not reach into the group's state. Everything this change alters is expressible
that way.

**Two seams, both of which already exist. No new seam.**

*The chat monitor seam* is the primary one and is where every new assertion goes. A
monitor is constructed over fake channel connections, a fake agent factory and a
recording metrics publisher; messages are written to a channel, the channel is
completed, and the monitor task is awaited. This is deterministic for a finite message
set — the existing test helper already writes then completes — so none of the new
tests needs a gate, a poll or a timeout. The conversation group is internal and reached
only through here, which keeps the seam count where it is.

*The delivery target resolver seam* already exists for resolution and announce tests
called directly. Dropping the `skipMinted` parameter rewrites those announce tests in
place; the resolution tests are unaffected.

**Four new behaviours to pin, all at the monitor seam.**

- A group whose first message is a chat command constructs no agent. The fake agent
  factory already records every construction, so this is an assertion that the record
  is empty.
- A group whose first message is a chat command resolves no delivery targets and mints
  no conversation. The fake channel already records every conversation creation.
- A turn following a chat command is routed correctly. This is the case the deleted
  invariant used to cover, and it becomes a test rather than a chain of three files.
- A later turn reusing the group anchors has those targets announced rather than
  skipped, and the group-opening turn's minted targets are still skipped. This pins the
  redefined marker where a user would notice it: a live stream that does or does not
  get set up.

**Red first for the behaviour changes.** The two "constructs nothing" tests describe
behaviour the current code does not have and must be seen to fail before the reorder.
The routing and announce tests describe behaviour that is preserved, so they are
written against the current code and must pass before and after.

**The existing monitor suite is the regression net and should not need rewriting.**
Its tests drive the monitor through channel writes and assert on routing, ordering,
sequencing, cancellation, first-reply latency and schedule metrics. All of that
behaviour is preserved, so a test that changes is a signal worth stopping on. The
delivery-identity, config-patch, conversation-context and schedule-metrics files are
in the same position.

**Prior art.** The monitor tests are the model for the new ones: the fake channel
connection, the fake agent factory with its construction record, the recording metrics
publisher and the write-then-complete helper are all in place. The announce tests are
the model for the marker assertions.

## Out of Scope

- Metrics publishing. That is candidate 1, and it lands first. This change relocates its result and adds no publish site.
- Memory recall's ownership. The recall hook call moves with the user-message building into the new module, but the anchor-index contract, the extraction window and the recall block are candidate 12.
- Publishing queue wait as its own latency stage. Considered and rejected here; first-reply keeps its current window.
- Making the thread resolver's clear delete persisted state when no live context exists. Not reachable today and not reachable after this change, because the context stays eager.
- The merge and streaming-group helpers, and the keying of a group by conversation and agent.
- Adding, removing or changing chat commands, and how a command is written by a user.
- The delivery identity rule itself. It is pinned in the glossary here, and its behaviour is unchanged.
- The agent factory's interface. Candidate 7 states it changes nothing there either.

## Further Notes

**Two claims in the survey are corrected.** The unawaited warmup task does not outlive
the agent: warmup takes the agent's session lock before the dispatch loop is reached,
so disposal always waits behind it. The waste is real; the leak is not. And the eager
order's documented reason survives the change, because warmup was never overlapping
the stages that move.

**One constraint the survey missed.** The thread resolver only deletes persisted state
when it finds a live context. A reorder that defers the context along with the agent
would break clear-after-restart, which is the exact case the reorder exists to serve.
This is why the split runs between the context and the agent rather than around the
whole prologue.

**Design decisions were settled by interview.** Alternatives considered and rejected:
naming the counter as a group-opener flag on the turn while leaving resolution eager;
resolving targets at enqueue so a turn is complete when queued, rejected because it
would mint a conversation for a queued turn and leave an empty thread visible;
deferring the thread context along with the agent; keeping the `skipMinted` correction
under a better name; moving the minted set off the delivery target onto the turn;
splitting the work into a queue module and a runner module; leaving everything in the
chat monitor with only the turn record extracted; starting first-reply at enqueue;
adding a queue-wait latency stage; and making the conversation group a third test seam.

**Documentation lands with the implementation.** The decision record and the four
glossary entries are already written. The candidate file records the sequencing against
candidates 1 and 12.
