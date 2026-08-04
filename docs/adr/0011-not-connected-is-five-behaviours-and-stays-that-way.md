# 0011 — Not connected is five behaviours, and stays that way

Status: accepted
Date: 2026-08-04

## Context

`Infrastructure/Clients/Channels/McpChannelConnection.cs` implements two interfaces,
`Domain.Contracts.IChannelConnection` and `Infrastructure`'s `IMcpChannelConnection`.
Between them they describe none of what the connection does when it has no client.

There is no client between construction and the first `ConnectAsync`, and again for the
whole of a reconnect. In that window the type behaves five different ways:

- `SendReplyAsync` (`:288`) and `RequestApprovalAsync` (`:312`, `:334`) throw
  `InvalidOperationException`, via `EnsureConnected` at `:455-461`
- `CreateConversationAsync` (`:355-358`) returns null
- `RegisterAgentsAsync` (`:396-401`) returns silently
- `IsHealthyAsync` (`:427-430`) returns false
- `Messages` (`:54`) yields forever, because the channel reader has nothing to give

Reading the five side by side invites the conclusion that they drifted and should be
unified. They did not all drift, and unifying them would break a caller.

## Decision

The five behaviours stay exactly as they are, and the interface states them.

`CreateConversationAsync`'s null is the load-bearing one.
`Domain/Monitor/DeliveryTargetResolver.cs:51` and `:91` read null as **this channel
minted nothing** — which is also what an attach-only channel returns, and what a channel
whose server has no `create_conversation` tool returns. The resolver's job is to try each
candidate target and move on, so all three "no conversation here" answers being one value
is the point, not an accident. An exception would make the resolver catch in order to
continue.

`Messages` yielding forever is the same shape from the other side: the agent's read loop
awaits messages for the process lifetime and a reconnect is invisible to it. A completed
sequence would end the loop.

The throwing pair and the silent pair differ because their callers differ. The send verbs
are called by an agent mid-turn with somewhere to report a failure; `RegisterAgentsAsync`
and `IsHealthyAsync` are called by the connection's own supervision, which reacts to the
answer rather than to an exception.

So the interface says which of the five each member does. The rule is per member,
because the reason is per member.

## Considered options

**Unify on throwing.** One rule, easy to state. Rejected: `DeliveryTargetResolver` would
wrap every target in a `try`/`catch` purely to keep iterating, and the catch would have to
distinguish "not connected" from a genuine transport failure, which is the distinction
the null already makes for free.

**Unify on a nullable or result type everywhere.** The `HubResult<T>` shape from ADR 0004
applied to channels. Rejected here for a reason that does not apply there: in WebChat, 24
call sites each rebuilt the same guard and three user-visible defects followed. Here the
callers are few, each reads exactly one of the five, and no defect has been observed. It
would be a large edit to every channel caller to fix nothing.

**Make not-connected unrepresentable.** `RunAsync` hands out a connected handle for the
duration, and the send verbs exist only on it. The deepest answer. Rejected because
`ChatMonitor` and `DeliveryTargetResolver` hold their connection for the process lifetime
and would each need a new way to say "nothing right now" — the same question moved one
layer up, with real behaviour risk in the delivery path.

**Leave it undocumented.** Rejected: the five differ for five reasons, and none of the
reasons is visible from the code. That is exactly what an interface is for.

## Consequences

- A new channel caller reads the rule instead of the implementation.
- Anyone who notices the inconsistency later finds this record rather than re-deriving it,
  and a change to any of the five is a decision rather than a tidy-up.
- The interface is now wider in prose than in signature. That is deliberate; per the
  vocabulary this repo's audits use, "interface" means everything a caller must know.
- This does not conflict with ADR 0004. That record narrows a seam where one value meant
  two things and defects followed; here the values mean what the callers need them to
  mean and the ambiguity is doing work.
