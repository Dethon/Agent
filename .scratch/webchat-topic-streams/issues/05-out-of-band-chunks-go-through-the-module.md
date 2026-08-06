# 05 — Out-of-band chunks and finalise go through the module

**What to build:** the two writers that touch a topic stream without having opened it stop writing
state directly and ask the module instead.

A tool call finishing and an approval being resolved both arrive as their own hub pushes, and both
carry text for a topic that may have no reply in flight at all. Today the store's reducer defends
itself against that case; after this ticket the module answers it, so a push for an idle topic
leaves nothing behind and the reducer's guard is deleted because nothing can emit the chunk it was
guarding against.

The other writer is the path where another person's message arrives mid-reply and the agent's
half-written text has to be closed off first. That goes through the module's topic-keyed finalise,
which leaves the reset of the accumulating text with exactly one dispatcher.

For a user: an idle conversation never sprouts an empty streaming bubble, and answering an
approval after the reply has ended cannot revive it.

**Blocked by:** 02 (the dispatcher's dead branches are gone first), 04 (the module must own the
streams it is being asked about).

**Status:** ready-for-agent

- [ ] A tool-call push for a topic with no reply in flight leaves no streaming content and does
      not mark the topic as streaming.
- [ ] A tool-call push for a topic that is streaming appends to that reply, as it does today.
- [ ] An approval-resolved push carrying tool calls behaves the same way in both cases.
- [ ] Another person's message arriving while the agent is answering commits the agent's text so
      far as its own message and clears the live buffer, through the module.
- [ ] The message pipeline no longer dispatches any streaming state action; the reset of the
      accumulating text has one dispatcher, the module.
- [ ] The reducer no longer has a guard dropping a chunk for a topic that is not streaming, and
      the test covering that guard is replaced by one at the whole-client seam asserting the same
      user-visible outcome.
- [ ] `dotnet test` on `Tests/Unit` is green.
- [ ] `dotnet format` has run over the staged files.
