# 01 — The connection seam

**What to build:** Nothing changes for someone looking at the dashboard. What changes
is that the dashboard's connection to the metrics hub becomes something a test can
drive. Today a test can push events in, but it cannot make the connection drop,
reconnect or fail to start, so none of the behaviour around those moments has ever been
covered. After this ticket it can.

The dashboard's hub client goes behind an interface. That interface carries one generic
receive verb — a wire method name plus a typed handler, returning a disposable
registration — in place of the eleven named registration methods, along with the three
lifecycle events, the connection state, a start operation and asynchronous disposal.
The wire method names move to the live-update effect, which is where the mapping from a
name to a handler already lives.

With the interface in place, the concrete client no longer needs to be subclassable. Its
fourteen `virtual` members and its `protected` parameterless constructor exist only so a
test can inherit from it; both go. The test fake stops being a subclass of production
code and becomes a plain implementation of the interface, holding one handler registry
keyed by wire method name with a raise helper, in place of eleven handler lists, eleven
overrides and eleven raise helpers.

The existing effect tests keep asserting exactly what they assert today, driven through
the new fake. The one new test is the one that was impossible before: raise a reconnect
and assert the connection status changes. It documents today's behaviour, which is that
a reconnect flips a flag and does nothing else. Ticket 05 is what makes that behaviour
correct.

**Blocked by:** None within this feature. The metric family feature
(`.scratch/metric-family/`) lands first, because it rewrites the live-update effect's
subscription block and its test file, both of which this ticket edits.

**Status:** ready-for-agent

- [ ] The dashboard's metrics hub connection is reached through an interface, not a concrete class.
- [ ] That interface has one generic receive verb taking a wire method name and a typed handler and returning a disposable registration.
- [ ] It also carries the three lifecycle events, the connection state, a start operation and asynchronous disposal, and nothing else.
- [ ] The eleven named registration methods are gone, and the wire method names live in the live-update effect.
- [ ] The concrete client has no `virtual` members and no `protected` parameterless constructor.
- [ ] The test fake implements the interface rather than inheriting from the concrete client, and holds one handler registry rather than eleven lists.
- [ ] The fake can raise all three lifecycle events, and can fail a start a scripted number of times before succeeding.
- [ ] Every assertion in the existing effect suite still passes, driven through the new fake.
- [ ] A new test raises a reconnect and asserts the resulting connection status, which was not expressible before.
- [ ] No user-visible behaviour changes.
