# 02 — The calling services depend on the live connection

**What to build:** nothing user-facing. Five of the six services that make **hub
calls** — topics, messaging, approvals, agents and the session — take the concrete
connection class rather than its interface. That single fact is why none of them has
a unit test: a concrete dependency cannot be faked, so every disconnected branch in
them is unreachable from a test.

This is a prefactor and it is mechanical. Each service takes the **live connection**
interface instead. No signature changes, no behaviour changes, no null handling moves
yet. The push service already takes the interface and is left alone.

Doing it now rather than inside a later batch means the batches that retype these
interfaces are about the retyping and nothing else.

**Blocked by:** None — can start immediately.

**Status:** done

- [x] The topic, messaging, approval, agent and session services take the live
      connection interface.
- [x] No service in the client names the concrete connection class.
- [x] Container registration is unchanged in effect: the same instance is injected.
- [x] The full client suite passes unchanged.
