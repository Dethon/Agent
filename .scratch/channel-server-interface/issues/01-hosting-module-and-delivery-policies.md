# 01 — Channel hosting module and delivery policies

**What to build:** A developer adding a transport can express how their channel buffers, and get the same answer to "was anyone listening?" that every other channel gets. Three delivery policies exist as a named choice, and emitting a notification reports whether a live subscriber received it — the liveness check happens inside the emit, so it cannot be skipped or asked a different way.

Nothing consumes this yet. It is built beside the six existing channel servers, which keep working unchanged.

The three policies must reproduce today's behaviour exactly:

- **Broadcast** — always enqueue; subscribers that are idle but not yet pruned still receive the item.
- **Buffer-always** — enqueue targeted at a known subscriber id, creating that subscriber's queue on demand, so an item arriving before the agent's first poll is buffered rather than fanned out to nobody.
- **Gate-on-live** — enqueue only when a live subscriber exists; otherwise nothing is buffered at all.

Broadcast and gate-on-live differ **only** in the no-live-subscriber case. That is the distinction that previously existed only as a difference between which enqueue method a developer had copied, and getting it wrong is the defect this whole feature exists to prevent.

The new module lives in its own project that references the domain project and the model-context-protocol server package, and nothing else. It must not reference the infrastructure project: two of the six channel servers depend on the domain project alone, and pulling in infrastructure would hand them a browser automation library, a cache client, a printing library, a console UI toolkit and the whole agent stack as transitive dependencies. The domain project cannot host this code either — it deliberately references no dependency-injection or model-context-protocol packages.

**Blocked by:** None — can start immediately.

**Status:** done

- [x] A new project exists referencing only the domain project and the model-context-protocol server package; the solution builds.
- [x] A delivery-policy type names the three policies.
- [x] An emitter takes a built message notification and returns whether a live subscriber was present.
- [x] The emitter has a matching member for cancel notifications.
- [x] The emitter is sealed.
- [x] Under broadcast, an item reaches a subscriber that is idle but not yet pruned.
- [x] Under gate-on-live with no live subscriber, nothing is buffered and the call reports false.
- [x] Under buffer-always with no subscriber yet, the queue is created on demand and the item is retrievable by the first poll that arrives.
- [x] Under every policy, the return value reflects live-subscriber presence, independently of whether anything was buffered.
- [x] Buffer-always requires a subscriber id; the other two policies reject one. Validated at registration, not at first emit.
- [x] All six existing channel servers still build and their tests still pass — nothing consumes the new module yet.
