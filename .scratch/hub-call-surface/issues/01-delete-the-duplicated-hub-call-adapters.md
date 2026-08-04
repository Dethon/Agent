# 01 — Delete the duplicated hub-call adapters

**What to build:** nothing user-facing. The integration test project holds a second
copy of the topic, messaging and approval service interfaces, written against a bare
transport instead of against the **live connection**. Nothing in the solution
references any of the three.

This is a prefactor. Those copies implement the very interfaces tickets 05, 06 and 08
retype, so left in place they stop compiling partway through this work and get
rediscovered three times, once per batch. Clearing them first means no later ticket
carries a deletion it did not ask for.

Confirm they are unreferenced before deleting rather than after: a reference from a
fixture or a collection definition would change this ticket into a migration.

**Blocked by:** None — can start immediately.

**Status:** done

- [x] A search for each of the three types across the whole solution returns only
      their own definitions.
- [x] All three files are deleted.
- [x] The integration test project builds and its suites pass unchanged.
