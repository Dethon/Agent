# 03 — Binding becomes part of connecting

**What to build:** the defect fix. A user who backgrounds the WebChat app on a
phone and returns to it keeps receiving the agent's replies, streaming updates,
tool call activity, approval prompts, topic changes and agent list updates. Today
the client rebuilds its hub connection on resume, reports itself Connected, and
hears none of those, because the six server pushes stay bound to the disposed
instance.

The live connection takes over binding. Building, binding and starting become one
sequence it owns: build the hub connection, bind the pushes to that instance,
then start it. Binding before starting also closes the window in which a started
connection has no handlers. Tearing down is the mirror — unbind, then dispose —
so nothing is left bound to a dead instance.

The binder becomes the hub event binder: it takes the connection to bind to, it
can be unbound, and it keeps the six wire-name-to-dispatcher pairs together. Its
already-subscribed guard is deleted. That guard exists to make a repeated bind
safe, and its only real effect was to make the missing unbind silent. The live
connection calls unbind and bind in a known order, so there is nothing left for it
to protect. The initialization effect stops binding entirely.

Write the regression test first and watch it fail: after a rebuild, a server push
raised on the new connection still reaches the store. Keep the binder, the hub
event dispatcher and the stores real, so the assertion is a store change rather
than a record of which methods were called. Faking the binder here would reproduce
the blind spot that let this defect ship.

**Blocked by:** 01, 02.

**Status:** ready-for-agent

- [ ] A red test asserts that a server push reaches the store after a rebuild, and
      fails against the current design before any extraction lands
- [ ] The live connection binds the server pushes to each hub connection it builds,
      before starting it
- [ ] Tearing down a hub connection unbinds before disposing, and a push raised on
      a torn-down instance changes nothing
- [ ] The binder takes the connection to bind to, can be unbound, and no longer
      guards against a repeated bind
- [ ] The initialization effect no longer binds server pushes
- [ ] The binder has tests of its own: binding registers all six pushes against the
      connection it was given, and unbinding releases them
- [ ] A push reaches the store on a first connect as well as after a rebuild
- [ ] The existing rebuild, probe, retry and give-up behavior is unchanged
