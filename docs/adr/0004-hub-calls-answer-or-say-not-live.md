# 0004 — A hub call answers or says it was not live

Status: accepted
Date: 2026-08-03

## Context

`IChatConnectionService` exposed the raw `HubConnection`, and 24 call sites in the
WebChat client reached through it. Every one of them repeated the same three lines:
fetch the connection, null-check it, and on null return an empty list, `false` or
`null` — the same value the server itself returns when there is genuinely nothing
there.

So "we could not ask" and "the answer is nothing" were the same value, and no
signature said so. Three user-visible behaviours followed:

- `AgentSelectionEffect.cs:84-86` dispatches `TopicsLoaded` with whatever
  `GetAllTopicsAsync` returned. Picking an agent while the client is between
  connections empties the sidebar.
- `SendMessageEffect.cs:93-97` returns silently when `StartSessionAsync` is `false`,
  so a message typed in that window disappears with no error.
- `StreamingService.cs:43-49` reads a `false` from `EnqueueMessageAsync` as "no
  active stream", dispatches `StreamStarted` and opens a new stream that yields
  nothing.

The guard was also incomplete. The connection is null only between a teardown and
the next successful start; while SignalR is `Connecting` or `Reconnecting` it is
non-null and not active, so the null check passes and the call goes to a transport
that cannot carry it.

## Decision

Every hub call returns one of two things: the server's answer, or **not live**.

```csharp
readonly record struct HubResult<T>(bool IsLive, T? Value);
```

The live connection is the only thing that decides. It owns the transport instance
and knows whether it is live, so the invoke and stream verbs move onto
`IChatLiveConnection` and the raw `HubConnection` accessor is deleted from it and
from `IChatHubConnection`. SignalR client types stay inside
`SignalRHubConnectionFactory`.

The result travels all the way to the effects. The five calling services stay, as
one-line typed method lists with no null handling, and their interfaces carry
`HubResult<T>`. The two streaming calls return `Task<HubResult<IAsyncEnumerable<ChatStreamMessage>>>`
rather than an `IAsyncEnumerable` that yields nothing, so not-live is known before
iteration starts.

Effects branch on it under one rule, decided by who asked for the call:

- **A read that feeds a store** skips its dispatch and leaves the existing state
  alone. Candidate 2's connection epoch already reloads everything on becoming live.
- **A call the user initiated** raises one `ShowError` toast. Nothing the user did
  disappears without a word.

## Considered options

**Keep the silent defaults, documented once.** The smallest possible change: state
the rule on the gateway rather than restating it 24 times. Rejected because it
preserves all three defects and keeps the interesting fact — that an empty list can
mean two different things — unexpressed in any signature.

**Throw a typed exception.** Uniform, loud, and all 24 guards delete. Rejected
because nothing in the client catches it: a user action would fail into a logged
stack trace, which is the same silent nothing as today with more noise.

**Wait for the connection to become live, then fail.** Most not-live windows are the
~2.5 s rebuild, so a slow sidebar would replace an empty one. Rejected: it adds a
queue and a second timeout to reason about, and still needs an answer for the
genuinely offline case, which is the case that produced the defects.

**Stop the result at the services and have each map it back.** Leaves the effects
and their tests untouched. Rejected because the decision is then still made in five
places under five rules, and it cannot fix the sidebar wipe — only the effect knows
that an empty topic list should not overwrite a populated one.

## Consequences

- `HubResult<bool>` carries three outcomes, not two: not live, the server said no,
  the server said yes. Callers of `StartSessionAsync`, `EnqueueMessageAsync` and
  `RespondToApprovalAsync` must keep the middle one distinct from the first.
- The five services stop being worth unit-testing. Each method is one line, so the
  tests go where the decisions are: the live connection's two outcomes, and each
  effect's not-live branch.
- This narrows seams rather than deleting them, so it does not conflict with
  ADR-0001. That record rejects adapter counting as grounds for removing an
  interface; the argument here is that the interface was as wide as the
  implementation and its failure rule was unstated. All five services keep their
  interfaces.
- Sequenced after the chat live connection module. That work renames the interface,
  adds the receive verb to `IChatHubConnection` and gives its fake a handler
  registry; the send verbs land on the same seam.
