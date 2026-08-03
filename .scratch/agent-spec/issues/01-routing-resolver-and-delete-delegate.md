# 01 — Extract the provider routing resolver and delete the chat-client factory delegate

**What to build:** Provider routing resolution becomes its own piece that can be
called and asserted on directly, and the test-only escape hatch through the middle
of the agent factory is deleted.

Today the factory resolves an agent's effective provider routing inline — an agent's
own declared routing wins wholesale over the global default, an absent one inherits
it, and advisories are logged against the agent or subagent identity that tripped
them. The seven tests that cover those rules reach the resolved value by passing a
chat-client factory delegate into the factory and capturing what went past it. No
production path passes that delegate; it exists only so tests can short-circuit real
chat-client construction, and its parameter list mirrors an internal method's, so it
has to be edited whenever chat-client construction changes.

After this ticket the resolution rules are asserted on directly with no agent, no
chat client and no capture, and the delegate no longer exists in production or in
tests.

Behaviour must not change. In particular "neither the agent nor the global default
declares routing" still resolves to nothing rather than to an empty-but-present
routing object, because balanced routing is the absence of the object; and advisories
still fire per construction with no dedupe, still naming the agent or subagent.

This ticket touches no metrics wiring, so it can run alongside the metrics publishing
module effort.

**Blocked by:** None — can start immediately.

- [ ] Provider routing resolution lives in its own unit, taking the declared routing, the global default and the identity to name in advisories.
- [ ] The routing rules are covered by tests that call the resolver directly: own routing wins wholesale over the global default, an absent one inherits it, neither resolving to nothing, and an advisory naming the agent or subagent that tripped it.
- [ ] A clean routing configuration still logs no advisory, asserted as the absence of an advisory rather than an empty log.
- [ ] The chat-client factory delegate is removed from the agent factory.
- [ ] The one remaining test that passed the delegate no longer does, and constructs a real chat client instead.
- [ ] The existing wire test still drives real chat-client construction through a capturing transport handler and still asserts that the resolved routing and the session id reach the request body.
- [ ] The full unit test suite passes.

**Status:** ready-for-agent
