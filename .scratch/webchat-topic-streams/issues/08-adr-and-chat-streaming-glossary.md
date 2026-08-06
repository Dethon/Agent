# 08 — ADR-0017 and the Chat streaming glossary

**What to build:** the record of what was decided, so the next reader does not undo it.

Everywhere else in this client the store is the truth and services dispatch into it. This one
slice inverts that: the module owns whether a topic has a reply in flight, and the store's
streaming state is the projection it publishes for rendering. Somebody who meets a component
subscription first will read that as a mistake and try to restore the usual pattern, which is
exactly what the seven-copies problem grew out of. ADR-0017 says why it is that way and what it
buys.

The glossary gains the two terms the six files involved have been using loosely. "Stream",
"buffer" and "resuming" each mean more than one thing across them today.

**Blocked by:** 06 (the shape being described is settled there).

**Status:** resolved

- [x] `docs/adr/0017-*.md` exists, in the same voice and shape as the existing ADRs, recording
      that a topic's stream has one owner and the store is its projection.
- [x] The ADR names the trade-off honestly: uniformity with the rest of the client's state
      handling, given up in exchange for an invariant that cannot be broken by a new caller.
- [x] The ADR names what it makes impossible rather than listing what was refactored: a live
      buffer for a topic with no reply in flight, an ending from one stream clearing another's
      state, two replies in flight on one topic, and a stream nothing is tracking.
- [x] `CONTEXT.md` gains a **Chat streaming** section defining **topic stream** and **stream
      lease**, each with its avoid-list, in the file's existing style and with no implementation
      detail.
- [x] The section sits with the other conversation and client vocabulary rather than at the end,
      and nothing already in the glossary now contradicts it.
- [x] `dotnet format` has run over the staged files.
