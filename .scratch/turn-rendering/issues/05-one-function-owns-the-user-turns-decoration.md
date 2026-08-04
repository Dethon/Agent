# 05 — One function owns the user turn's decoration

**What to build:** Everything prepended to a user turn on its way to the model is built
by a single domain function, so adding or changing a decoration means editing one place
instead of finding a transform inside an HTTP adapter.

The chat client currently clones each message and builds, inline, the sender segment, the
room and satellite qualifiers, the local timestamp, the dismissed-alert line and — after
the previous ticket — the call that renders the recall block. That is five prompt-facing
strings living in a transport adapter.

Move the whole transform into one domain function that takes a message and a time zone
and returns the decorated copy. It is a plain function rather than an injected service:
the only ambient thing it needs is the local time zone, and taking that as an argument
keeps it pure. The chat client's per-message transform collapses to a single call, and it
keeps deciding when to decorate, since the decoration must land on the copy it sends and
never on the copy that gets persisted.

Preserve the order and the conditions exactly. The recall block comes first, then the
sender and timestamp prefix, then the user's own content. The prefix appears only when the
message is from the user and at least one of sender, timestamp or dismissed alert is
present; the recall block only when the message is from the user and carries a memory
context.

The twelve chat-client prefix tests move to the new function and lose their mocked inner
client. Their expected strings carry over unchanged — they are the specification of this
behaviour and the proof that the move changed nothing. Add one test for the ordering of
the two decorations together, which no test covers today.

**Blocked by:** 04 — the decoration function calls the recall block renderer, so that has
to exist first.

**Status:** ready-for-agent

- [x] One domain function takes a message and a time zone and returns the decorated copy.
- [x] It builds the sender, room, satellite, local timestamp and dismissed-alert prefix, and applies the recall block.
- [x] The chat client's per-message transform is a single call to it; no decoration strings remain in the client.
- [x] The prepend order and the role and null conditions are unchanged.
- [x] The twelve prefix tests assert against the function directly, with their expected strings unchanged and no mocked chat client.
- [x] A test covers a message carrying both a memory context and a sender prefix, pinning their order.
