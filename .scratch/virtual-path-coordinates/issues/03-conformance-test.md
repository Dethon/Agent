# 03 — The rule is enforced by one test

**What to build:** a tool that starts answering in backend coordinates fails a test
instead of reaching production.

This defect has been fixed twice already, months apart, on whichever tools were noticed at
the time. Ticket 02 fixes it everywhere it exists today. This ticket is what stops the
third recurrence: one test that drives every filesystem tool against a backend answering
in deliberately hostile coordinates, and fails if any path in any response is not a
virtual path.

Drive it from the one operations list, the way the server conformance test, the
payload-type table, the capability map and the tool feature's key set are all already
derived from it, so an operation added without a matching case is impossible. The two
transfer tools answer with a shape that is not a backend operation's result, so they are
mapped in explicitly.

The stand-in backend answers every operation three ways: the container-absolute spelling,
the leading-slash mount-relative spelling, and the bare mount-relative spelling. The
assertion is that every path-shaped field in the response begins with the mount point, or
appears on the exemption list.

**Blocked by:** 02 — Every tool answers in the coordinates it was asked in.

- [ ] Every filesystem tool the model can call is exercised, and the set of tools covered
      is derived from the one operations list rather than written out by hand, so adding
      an operation without a case is impossible.
- [ ] The stand-in backend answers in all three hostile spellings, and the test fails for
      each of them if a tool passes the backend's answer through.
- [ ] The two transfer tools are covered, mapped in explicitly because their result type
      is not a backend operation's.
- [ ] The exemption list lives in the test file with one line of reason per entry, and has
      exactly two entries: the exec working directory, and anything the test genuinely
      cannot reach.
- [ ] Reverting any single fix from ticket 02 turns this test red. Verify this by
      reverting one, watching it fail, and restoring it.

**Status:** ready-for-agent

## Comments

From the spec at `.scratch/virtual-path-coordinates/spec.md`.

Runs in parallel with ticket 04 — both need only ticket 02. The camelCase field names
survive 04's shape change, so this test keeps passing over the typed transfer result.

Prior art: the server conformance test for the derived-from-one-list table shape, and for
writing expectations out rather than deriving them from the code under test. That test's
own comment records why: a `registered.ShouldBe(overridden.Count)` assertion stayed green
for a server that had dropped its registrar call entirely.
