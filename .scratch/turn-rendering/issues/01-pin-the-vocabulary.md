# 01 — Pin the vocabulary

**What to build:** The words this feature uses exist in the project glossary before any
code moves, and the two decisions a later reader is most likely to re-open are on the
record.

Four terms go into `CONTEXT.md` under a memory heading, following the existing entry
convention of a definition followed by an `_Avoid_` line:

- **Decoration** — everything prepended to a user turn on its way to the model that the
  user did not type: who sent it, from where, when, what alert they dismissed, and what
  the agent remembers about them. It exists only on the copy sent to the model and is
  never persisted. _Avoid_: prefix, envelope, wrapper.
- **Recall block** — the decoration that carries remembered facts. The model is told to
  look for it by name. _Avoid_: memory context, memory prefix.
- **Extraction window** — the slice of conversation the memory extractor reads, rendered
  with turn markers so the extractor knows which turn is the current one. _Avoid_:
  context window, history slice.
- **Memory anchor** — the point in a conversation's persisted history that an extraction
  window is cut at. It is taken before the current turn is persisted, so it excludes the
  turn that produced it. _Avoid_: anchor index, offset, cursor.

Two ADRs, numbered next in sequence, each with the Context / Decision / Considered
options / Consequences headings the existing ones use.

The first records that all outgoing user-turn decoration lives in one domain function
rather than in whichever client sends it. Context: the block the memory system prompt
promises was produced by a private static inside an HTTP adapter, with no test anywhere.
Considered options include leaving it, moving only the recall block, and moving the whole
transform. Consequence: a client decides when a turn is decorated, never what the
decoration says.

The second records that every user turn keeps carrying its own recall block. Context:
the structured memory context is persisted per message while the rendered text is not,
so each request re-renders a block for every historical user turn that carries one.
Decision: this is intended. The memories shown against an older turn are the ones that
shaped that answer, and re-rendering them identically keeps the prompt prefix cacheable.
Note the constraint it implies — rendering the block at the recall hook instead would put
the text into the persisted message, which the extraction worker reads back, feeding
remembered facts to the extractor as the user's own words. Consequence: a long
conversation carries several blocks, and that is not a defect to be fixed later.

**Blocked by:** None — can start immediately. Nothing else waits on this, but landing it
first means the later tickets name things consistently.

**Status:** ready-for-agent

- [x] `CONTEXT.md` carries the four terms under a memory heading, each with an `_Avoid_` line.
- [x] An ADR records that outgoing user-turn decoration lives in one domain function, with the options considered.
- [x] An ADR records that every user turn carries its own recall block, including why rendering at the recall hook is ruled out.
- [x] Both ADRs are numbered next in sequence and use the Context / Decision / Considered options / Consequences headings.
- [x] No code changes.
