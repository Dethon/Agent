# 04 — The recall block leaves the chat client

**What to build:** The block of remembered facts that the memory system prompt promises
the model is rendered by a domain function next to that prompt, and can be tested without
an HTTP transport.

The agent's system prompt tells the model that remembered facts arrive in a named block
at the start of user messages. Nothing in the memory subsystem produces it — it is built
by a private static inside the OpenRouter chat client. The promise and the thing keeping
it are in different layers, and the block has no test anywhere in the repo.

Move the rendering into a domain module that turns a memory context into the block. It
owns the text only, not when the block is applied: the chat client keeps calling it from
the same place, because the block must land on the copy sent to the model and never on
the copy that gets persisted.

Two tests it could not have before. The first asserts the block carries the exact marker
the memory system prompt names, checked against the prompt constant, so renaming one side
goes red. The second is the cache-stability contract: rendering a memory context that has
been through a JSON round trip produces bytes identical to rendering the original. Every
request re-renders a block for each historical user turn that carries context, and those
contexts come back from storage as deserialized values, so drift there would break the
prompt prefix. Today that is asserted only indirectly, by a chat message serialization
test.

Cover both shapes while you are here: a context with memories and no profile, and one
with both. Their output must be byte-for-byte what the chat client produces today.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [x] A domain module renders a memory context into the recall block, and the chat client calls it.
- [x] The private rendering method no longer exists in the chat client.
- [x] A test asserts the block's marker appears in the memory system prompt constant.
- [x] A test asserts that rendering a context that has been through a JSON round trip is byte-identical to rendering the original.
- [x] Tests cover a context with and without a profile, and the output matches today's exactly.
- [x] No test in this ticket constructs a chat client or an HTTP transport.
