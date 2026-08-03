# 02 — Rename the connection service to the live connection

**What to build:** the module that owns the client's link to the chat hub carries
the glossary name. The interface becomes the chat live connection and the class
follows, along with every consumer, the container registration, and the test
fixture that fakes it.

This is prefactoring, not a behavior change. It comes first because every later
ticket in this spec names the module, and because the current name does not
distinguish the thing that survives a rebuild from the transport instance that
does not. Use the "Chat client connection" vocabulary in `CONTEXT.md` for any
comment or name you touch while you are in there.

Do not narrow the interface here. The status members, the reconnected event and
the raw transport accessor all stay exactly as they are; tickets 04 and 06 remove
what goes.

**Blocked by:** None — can start immediately. Runs in parallel with 01.

**Status:** ready-for-agent

- [ ] The interface and implementation carry the live connection name
- [ ] Every consumer, the container registration and the test fixture are updated
- [ ] The interface's member list is unchanged in this ticket
- [ ] No production behavior changes; the full unit suite passes unchanged
