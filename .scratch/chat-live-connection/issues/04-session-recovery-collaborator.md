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

**Status:** ready-for-agent

- [ ] Session recovery is a named, container-registered collaborator with one
      recover operation, injected into the live connection
- [ ] Recovery runs as the final step of becoming live, after the status is
      published
- [ ] A completed connect implies recovery has finished — no detached task
- [ ] Recovery is awaited outside the per-attempt timeout, and a slow recovery does
      not trigger a rebuild retry
- [ ] Recovery does not run on the first connect, and a test protects that rule
- [ ] Recovery runs after a rebuild, and a test protects that
- [ ] The push subscription is re-sent without force-refreshing the push channel
- [ ] The reconnected event is removed from the live connection and the
      initialization effect no longer subscribes to one
- [ ] First-load space validation still happens before the first space join
