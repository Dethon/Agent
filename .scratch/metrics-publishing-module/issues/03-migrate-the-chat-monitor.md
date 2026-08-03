# 03 — Migrate the chat monitor

**What to build:** An operator can see how long a cancelled turn ran before the user gave up on it.

Today the chat monitor and the reply dispatcher pass the turn's cancellation token to every publish, and the buffered publisher drops any event whose token is already cancelled. A cancelled turn therefore loses its schedule-execution event and its first-reply latency entirely. Nobody decided that; it fell out of a token being available at the call site. Publishing without a token fixes it.

Move the domain layer's publish sites to the void call. The first-reply latency helper exists only to await a publish, so it disappears; its site becomes a latency measurement scope. The schedule-completion callback keeps its shape but stops awaiting.

Let synchronousness travel outward from each site. Any method left with no awaits loses its awaitable signature and its `Async` suffix, and its callers stop awaiting it. Stop at the first method that still awaits real work.

**Blocked by:** 01, 02.

**Status:** done

- [ ] A cancelled turn records its schedule-execution event. Asserted as a test, since this is a behaviour change rather than a refactor.
- [ ] A cancelled turn records its first-reply latency.
- [ ] Every publish site in the domain layer uses the void call and passes no cancellation token.
- [ ] The first-reply latency helper is gone, replaced by a measurement scope at its site.
- [ ] Methods that became free of awaits are synchronous and have lost the `Async` suffix, along with their callers' awaits.
- [ ] The domain layer's existing monitor tests pass unchanged except where the cancelled-turn behaviour is the thing under test.
