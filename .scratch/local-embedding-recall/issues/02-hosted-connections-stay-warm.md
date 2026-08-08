# 02 — Connections to the hosted provider stay warm

**What to build:** Every call to the hosted provider currently pays a fresh TCP and TLS
handshake, measured at around 230 ms. The connection pool is shared process-wide and exists
precisely to avoid that, but it caps connection lifetime at two minutes and inherits a
one-minute idle timeout, while real traffic is about 35 turns a day. Every connection is
dead before the next turn needs it.

Set the pooled connection lifetime and the idle timeout deliberately, on both the shared
handler that every hosted chat client uses and on the embedding client, so a connection
survives an ordinary gap between turns. This helps the LLM call more than the embedding
call in absolute terms, because the LLM call happens on every turn regardless of what
memory does.

**Blocked by:** None — can start immediately.

**Status:** done

- [x] The shared handler's connection lifetime and idle timeout are both set deliberately
      rather than left to defaults
- [x] The embedding client gets the same treatment
- [x] The configured values are asserted by test rather than only written down
- [x] Two calls separated by a typical gap between turns reuse a connection
- [x] Nothing about what any call returns changes
