# 01 — Receive verb on the hub connection abstraction

**What to build:** the hub connection abstraction gains the ability to register a
typed handler for a named server push and hand back a disposable registration,
mirroring the transport's own API. The SignalR implementation forwards to the
transport. The test fake records what was registered and can raise a push to
whatever is registered.

Nothing changes behaviorally. The existing binder still reaches past the
abstraction to the raw transport, and the defect is untouched. This is the expand
step: after it, a test can drive a server push through a faked connection, which
is what makes the regression test in ticket 03 writable at all.

Do not add the send or stream verbs. Those belong to candidate 5 and are out of
scope for this spec.

**Blocked by:** None — can start immediately.

**Status:** done

- [x] The hub connection abstraction exposes a receive registration taking a wire
      method name and a typed handler, returning a disposable
- [x] The SignalR implementation forwards the registration to the underlying
      transport
- [x] The existing fake hub connection records registrations by wire name and
      exposes a way to raise a push to the registered handler
- [x] A test proves that raising a push on the fake invokes the handler registered
      for that wire name, and that disposing the registration stops it
- [x] No production behavior changes; the full unit suite passes unchanged
