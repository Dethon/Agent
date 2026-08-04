# 03 — The live connection module and the retried first start

**What to build:** Someone opens the dashboard while the agent is still starting up, and
the dashboard connects by itself once the agent is ready. Today that page is dead on
arrival and the only fix is a reload.

Two things cause that. Automatic reconnection has never applied to the first connection
attempt, so nothing retries it. And the dashboard marks itself started before it finds
out whether starting worked, so the failure leaves it believing it is already running
and a second call does nothing. The layout component that starts it catches the
exception and throws it away.

Introduce a live connection: one module that owns being live. Becoming live is an
ordered sequence inside it — bind the event handlers to the hub connection, start it
retrying until it succeeds, then publish the status. The retry loop uses the same
schedule as the reconnection policy and delays through an injected time provider, so its
tests do not wait in real time. It does not give up. Failures are logged and swallowed
inside the loop, because there is no caller who could do anything with them.

Binding happens once, before the first start attempt, and the retry loop wraps only the
start. A failed start leaves the hub connection and its registrations intact, so
rebinding per attempt would double every handler, and a reconnect reuses the same hub
connection so nothing needs rebinding there either.

The started latch records a start that succeeded rather than one that was attempted.

The live-update effect becomes the binder: it exposes a bind operation taking the hub
connection and an unbind operation releasing the registrations, and the module drives
both. Its event-to-store mapping is unchanged. It stops registering the three lifecycle
handlers, stops owning the connection lifecycle and stops being started from the layout.
The layout calls connect and catches nothing, because there is no longer a failure for
it to catch.

**Blocked by:** 01 — the module is built on the connection seam, and its tests need a
start that can be scripted to fail.

**Status:** ready-for-agent

- [x] A live connection module owns the ordered sequence of becoming live: bind, start with retry, publish status.
- [x] Callers get a connect operation and asynchronous disposal, and nothing else.
- [x] A start that fails is retried on the same schedule as the reconnection policy and never gives up.
- [x] The retry loop's delays come from an injected time provider, and its tests do not wait in real time.
- [x] Handlers are bound once, before the first start attempt, and are not rebound per attempt.
- [x] The started latch records a successful start, so a failure does not lock the module out.
- [x] The live-update effect exposes bind and unbind, keeps its event-to-store mapping, and no longer registers lifecycle handlers or owns the connection.
- [x] The layout component calls connect and catches nothing.
- [x] Disposal releases the registrations, so a push afterwards changes nothing.
- [x] A dashboard opened while the hub is unavailable connects on its own once the hub comes up, with no caller retrying.
