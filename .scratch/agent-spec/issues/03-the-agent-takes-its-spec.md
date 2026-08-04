# 03 — The agent takes its spec

**What to build:** The agent stops taking eighteen positional parameters, twelve of
them optional with null defaults, and takes its **agent spec** plus its collaborators
instead.

That parameter list is what made the subagent metrics defect invisible: "forgot to
pass the publisher" and "deliberately has no publisher" were the same expression. With
the spec already built by ticket 02, the constructor becomes the spec, the chat
client, the history store, the time provider, the logger factory and the shared prompt
cache. The agent reads the fields it needs and ignores the build-time ones, the way
any handler takes a request object.

No behaviour changes. This is the second half of the collapse: ticket 02 removed the
duplication between the two build paths, and this removes the shape that let an
omission hide.

Fifteen test construction sites build the agent directly and move to the new
constructor. Several of them exist to cover behaviour that must keep passing
unchanged: latency emission for both turn stages and for a patched model, conversation
context handling, and session deserialisation.

**Blocked by:** 02.

- [ ] The agent's constructor takes the agent spec plus the chat client, the history store, the time provider, the logger factory and the shared prompt cache.
- [ ] No caller passes the agent's configuration as positional or optional arguments any more.
- [ ] Every existing test that constructs the agent directly is migrated and still passes.
- [ ] The agent's observable turn behaviour is unchanged: both latency stages, the patched-model case, conversation-context handling and deserialisation.
- [ ] The full unit test suite passes.

**Status:** done
