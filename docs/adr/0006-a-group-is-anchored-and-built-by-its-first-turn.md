# 0006 — A conversation group is anchored and built by its first turn

Status: accepted
Date: 2026-08-03

## Context

`ChatMonitor` groups incoming messages by `(ConversationId, AgentId)` and processes
each group as a unit. `ProcessChatThread:81-99` reads the group's first message and,
before anything is parsed, resolves the delivery targets, builds the agent, restores
the thread from the state store and starts the MCP session warmup. Only then does
`DispatchCommandsAndQueueTurnsAsync:164` look at what the message actually says.

A `/clear` or `/cancel` is never answered by the agent. It is dispatched to
`ChatThreadResolver`, which disposes the thread context, which fires the
`group.Complete` callback registered at `:88` and ends the group. So a group whose
first message is a command — routine after an agent restart, when the user clears a
conversation that has no live group — pays for a full `McpAgent`, a state-store read
and `ThreadSession.CreateAsync` (every MCP endpoint connected, tools listed, prompts
fetched), then throws all of it away.

The same ordering produced a second problem. Because the anchors come from the first
*message*, the queuing loop has to tell later stages which message that was, and it
does so with an `int Index` on `PendingTurn` that counts every message including
commands. Two unrelated call sites read it: `ResolveTurnTargetsAsync` treats `index == 0`
as "reuse the group anchors", and `AnnounceTurnStartAsync(skipMinted: index == 0)`
treats it as "this turn's minted targets announced themselves". The comment at
`:150-152` is the entire specification of that rule.

The counter is only correct because of an invariant nothing states: a command tears
the group down, so an index above zero always implies a preceding real turn. That
chain runs through three files — `ProcessChatThread:88`, `ChatThreadResolver.ClearAsync`,
`ChatThreadContext.Dispose` — and neither `ChatCommandParser` nor `ChatThreadResolver`
mentions it.

## Decision

A conversation group resolves its anchors, builds its agent, restores its thread and
starts its warmup on its **first turn**, not on its first message.

A chat command is not a turn. It is never queued and never answered, so "the message
the anchors were resolved from" and "the first turn the group ran" are the same
message by construction. The counter is deleted: the group knows whether it has
anchors yet, and whether this turn is the one they came from, from its own state.

The thread context and its `group.Complete` callback stay eager. `ChatThreadResolver.ClearAsync`
only deletes persisted state when it finds a live context, so deferring the context
would make a leading `/clear` stop wiping the stored thread — the exact case this
change exists to serve — and would leave nothing to end the group.

Everything a group owns for the length of its life — the anchors, the agent, the
thread, the warmup, the pending-turn queue, the command dispatch — moves into one
`ConversationGroup` module, so the order they are established in is internal rather
than a sequence of statements in `ChatMonitor`.

## Considered options

**Keep eager resolution, name the flag.** Carry an `IsGroupOpener` bool on the turn
instead of an `int`. The smallest honest change: the rule gets a name and one
decision site. Rejected because the flag still has to travel through a queue to
describe something the group already knows, and it leaves the wasted agent build and
the unstated command invariant exactly as they are.

**Resolve targets at enqueue, so a turn is complete when queued.** Removes the
question entirely — nothing downstream can ask "is this the first one". Rejected
because target resolution mints conversations. A turn queued behind a running one
would create its WebChat conversation on arrival, leaving an empty thread visible to
the user for as long as the turn ahead of it takes.

**Defer the context as well as the agent.** Uniform laziness. Rejected: it silently
breaks `/clear` on a conversation with no live group, which is the common case, and
it removes the thing that ends the group.

## Consequences

- The documented reason for the eager order survives. Warmup now starts in
  `ConversationGroup.EnsureEstablishedAsync`, without being awaited. It still starts
  before the turn-start announce and before `BuildUserMessageAsync`'s memory recall,
  which are the two network stages the old eager start in `ChatMonitor` was
  overlapping. The only overlap lost is with `ChatCommandParser.Parse`, a string
  switch.
- `DeliveryTarget.Minted` changes meaning to "minted while resolving this turn".
  Reused anchors are projected with `Minted: false`, and `AnnounceTurnStartAsync`
  loses its `skipMinted` parameter. The flag has one production reader, so the
  change is contained to it.
- A group whose messages are all commands resolves no targets and mints no
  conversation. Today it resolves the command message's targets, which for a
  `/clear` carrying `ReplyTo` would mint a conversation nobody would ever write to.
- The agent is built from the first turn's sender rather than the first message's.
  These differ only when a command precedes a turn in one group, which the group
  teardown already makes a race window rather than a normal path.
- Sequenced after the metrics publishing module. Its ticket 03 rewrites the monitor's
  publish sites against today's layout and asserts the existing monitor tests pass
  unchanged.
