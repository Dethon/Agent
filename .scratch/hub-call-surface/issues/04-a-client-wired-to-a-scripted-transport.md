# 04 — A client wired to a scripted transport

**What to build:** the fixture the rest of this feature is tested through. One seam:
the **hub connection** is faked, and everything above it is real — the **live
connection**, the six calling services, the effects, the dispatcher and the stores. A
test scripts what the transport does and then asserts on the state a user would see.

That is the highest seam available and it is the same one the chat live connection
work chose. It matters here because the defects being fixed are wiring defects: a
service that forgets to pass **not live** through to its caller is invisible to a
fake at the service level and fails a test at this one.

The fixture exposes the stores for assertion and offers a way to say "this transport
is live and answers X" or "this transport is not live" per call. Building it once
means every behavioural ticket that follows is a few lines on top.

Its own proof is a smoke test rather than user-visible behaviour, which makes this
the one ticket in the set that is not a vertical slice. Keep it small enough to
justify that: composition and scripting, nothing clever.

**Blocked by:** 03.

**Status:** ready-for-agent

- [ ] A fixture composes the real client — live connection, services, effects,
      dispatcher, stores — over the fake hub connection.
- [ ] A test can script the transport's answer to a named hub call, and can put it in
      a state where calls answer not live.
- [ ] The stores are reachable for assertions without reaching into internals.
- [ ] A smoke test proves the wiring: a server push through the scripted transport
      reaches the store.
- [ ] A smoke test proves the other direction: a call made through a live transport
      reaches it with the arguments the caller passed.
- [ ] The fixture reuses the existing fake hub connection and connection factory
      rather than introducing a second set.
