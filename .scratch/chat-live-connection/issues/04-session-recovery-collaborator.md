# 04 — Session recovery as an injected collaborator

**What to build:** when the client becomes live again after an interruption, the
server ends up knowing who the user is, which space they are in, and where to send
their push notifications — and the connect operation does not report itself
finished until that has happened. Today this work is a closure somebody subscribed
to an event at start-up, fired and forgotten, so the client can report success
while the server still has no idea who it is talking to.

Session recovery becomes a named collaborator in the container with a single
recover operation, injected into the live connection and run as the last step of
becoming live. Its body does not change: re-identify the user, rejoin the current
space, and re-send the existing push subscription without force-refreshing the
push channel. That last part is load-bearing — a full resubscribe generates a new
endpoint in Chrome and loses the space memberships attached to the old one.

Recovery is awaited, but outside the per-attempt timeout. That timeout exists to
bound a handshake that can hang for tens of seconds after a mobile radio resume.
Stretching it over recovery would let a slow space rejoin cancel and trigger a
rebuild retry on a connection that is perfectly healthy.

Recovery does not run on the first connect. This is a real rule, not an accident:
first-load start-up validates the space slug and can replace it before joining, so
recovering on the first connect would join an unvalidated space and then join
again after validation. The initialization effect keeps that first-load work,
including the validation, and stops subscribing to a reconnected event — which is
then removed from the live connection.

**Blocked by:** 03.

**Status:** done

- [x] Session recovery is a named, container-registered collaborator with one
      recover operation, injected into the live connection
- [x] Recovery runs as the final step of becoming live, after the status is
      published — superseded, see the note below: it runs from an effect keyed on
      the connection epoch, which is still after the status and still not on the
      first connect
- [ ] A completed connect implies recovery has finished — no detached task —
      **dropped**, see the note below
- [x] Recovery is awaited outside the per-attempt timeout, and a slow recovery does
      not trigger a rebuild retry — now moot: recovery is not inside the connect
      path at all
- [x] Recovery does not run on the first connect, and a test protects that rule
- [x] Recovery runs after a rebuild, and a test protects that
- [x] The push subscription is re-sent without force-refreshing the push channel
- [x] The reconnected event is removed from the live connection and the
      initialization effect no longer subscribes to one
- [x] First-load space validation still happens before the first space join


## Amendment — recovery is driven by the epoch, not by the live connection

Built as written, then changed. Injecting recovery into the live connection and awaiting
it made the two depend on each other: recovery makes hub calls, hub calls go through the
live connection. The container cannot see that cycle, because the interface is registered
through a factory, so it recursed building live connections until the app stopped booting.
A `Lazy` made it resolvable and left the cycle in place.

What the await bought did not survive inspection. Its only two callers are the
initialization effect, which connects for the first time and therefore skips recovery
entirely, and the page-visible handler, which is a fire-and-forget call from JavaScript.
Nothing in the client read the guarantee. It also put a throwing `RegisterUser` or
`JoinSpace` on a path that escaped into that JavaScript boundary.

Recovery is now a `SessionRecoveryEffect` observing the connection epoch, the same
mechanism catch-up already used, with the shared "not on the first connection" rule moved
onto the connection store so it is stated once. The live connection depends on neither
collaborator, the client has no dependency cycle, and a failed recovery is logged like any
other effect.

What is lost: a caller can no longer know that a completed connect means the server has
re-identified the client. Nothing needed that. If something later does, it should be
rebuilt on the epoch rather than by putting recovery back inside the connect path.
