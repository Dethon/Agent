# 0010 — Every user turn carries its own recall block

Status: accepted
Date: 2026-08-04

## Context

`MemoryRecallHook` searches the memory store on every user turn and attaches what it
finds to the message as a structured `MemoryContext`. That structure is persisted with
the message. The **recall block** — the `[Memory context]` text the model is told to
look for — is not persisted: it is rendered from the structure on the way out, once per
request, for every historical user turn that carries a context.

So a conversation with ten user turns sends ten recall blocks, one against each turn,
each holding the memories that were recalled when that turn was answered. Read cold
that looks like an accident, and the obvious "fix" is to render the block once at the
recall hook and store it on the message.

## Decision

Every user turn keeps carrying its own recall block, rendered per request from the
persisted structure.

The memories shown against an older turn are the ones that shaped that answer. Showing
them where they applied is what the model needs to make sense of its own earlier
replies, and re-rendering them identically is what keeps the static part of the prompt
cacheable — the block for turn three is byte-for-byte the same on every subsequent
request, so the prefix does not change and the provider's cache still hits.

Rendering at the recall hook instead is ruled out. It would put the block text into the
persisted message, and the extraction worker reads the persisted messages back to build
its extraction window. The extractor would then see remembered facts as the user's own
words and store them again, compounding on every turn.

Byte-stability is therefore a constraint on the renderer, not a nice-to-have: the
structure comes back from Redis as a deserialized value, so rendering a round-tripped
context must produce exactly what rendering the original produces. That has a test.

## Considered options

**Render once at the recall hook and persist the text.** One rendering per turn instead
of one per request. Rejected: it feeds the block back to the memory extractor as user
content, which is a correctness failure, not a cost.

**Keep only the newest turn's block.** Fewer tokens. Rejected: it rewrites the prefix on
every turn, which costs the prompt cache far more than the blocks cost, and it strips
the context that explains the assistant's earlier answers.

**Deduplicate blocks across turns before sending.** Send each distinct memory once.
Rejected: the same reason — any per-request edit to earlier turns invalidates the cached
prefix, and the saving is a handful of lines.

## Consequences

- A long conversation carries several recall blocks. That is intended, and not a defect
  for a later audit to raise.
- The recall block renderer must be a pure function of the memory context, with no
  clock, no ordering by recency of read, and no dependence on whether the context came
  from memory or from JSON.
- The recall hook stores structure, never text. Anything that wants the block calls the
  renderer.
