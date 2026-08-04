# 0009 — All outgoing user-turn decoration lives in one domain function

Status: accepted
Date: 2026-08-04

## Context

A user turn does not reach the model as the user typed it. On its way out it picks up
who sent it, from where, through which satellite, the local time, the alert the user
just dismissed, and the block of remembered facts. That is the turn's **decoration**:
it exists only on the copy sent to the model and is never persisted.

All of it was assembled inside `OpenRouterChatClient.GetStreamingResponseAsync`, an
adapter whose job is HTTP transport to OpenRouter. The sender and timestamp prefix was
built inline in the per-message `Select`, and the recall block came from
`FormatMemoryContext`, a private static at the bottom of the same file.

The recall block is the sharper case. `MemoryPrompts.FeatureSystemPrompt` tells the
model that remembered facts arrive in a `[Memory context]` block at the start of user
messages. Nothing in the memory subsystem produced that block. The promise and the
thing that kept it were in different layers, and the block had no test anywhere in the
repo — the only way to reach it was to drive the chat client through a mocked inner
client.

## Decision

One domain function takes a message and a time zone and returns the decorated copy.
It builds every string the model reads that the user did not type, in a fixed order:
the recall block, then the sender and timestamp prefix, then the user's own content.

It is a plain static function, not an injected service. The only ambient thing the
decoration needs is the local time zone, and taking that as an argument keeps the
function pure and its tests free of fakes.

A chat client decides **when** a turn is decorated and never **what** the decoration
says. The client keeps the call, because the decoration must land on the copy it sends
and never on the copy that gets persisted — the extraction worker reads the persisted
copy back, and decoration text in there would be fed to the extractor as the user's own
words.

## Considered options

**Leave it in the chat client.** Nothing to move, and today there is only one client.
Rejected because the block the system prompt promises had no owner and no test, and a
second client would silently drop it — the prompt would keep promising a block that
never arrived.

**Move only the recall block.** The narrowest fix: it is the part with a prompt behind
it. Rejected because it leaves four other prompt-facing strings in a transport adapter
and splits "what a user turn carries to the model" across two layers, so the next
decoration has no obvious home.

**Make the decoration an injected service.** Would let a client be configured with a
different decoration. Rejected: nothing wants that, and an interface buys an
indirection where a static call is testable already.

## Consequences

- Adding or changing a decoration is one edit in the domain, next to the prompt that
  names it, with a test that needs no transport.
- A second chat client gets the decoration by calling the same function, so it cannot
  disagree with the first about what the model reads.
- The chat client keeps the timing responsibility, which is the part that is genuinely
  about sending: the decorated copy is built per request and thrown away with it.
- The prepend order and the conditions guarding each part are now stated in one place
  rather than implied by statement order in a `Select`.
