# 02 — Delete the unreachable stream-ended notifications

**What to build:** a hub event dispatcher that handles only the stream notifications a server in
this repo actually sends. The client maps three kinds of stream-changed notification, but the
only publisher anywhere sends one of them: the agent-initiated conversation tool, announcing
that a stream started. The completed and cancelled cases cannot arrive, and their tests assert a
mapping nothing exercises.

Both cases go, and the notification's change-type narrows to the one case that exists. A reader
of the dispatcher can then tell what can actually happen to a topic stream from the outside: it
can start, and nothing else. Ending a topic stream stays entirely a client-side fact — the loop
finishing, the stop button, or a topic being deleted.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] The stream-changed notification's change type has one case, and the dispatcher has one
      branch for it.
- [ ] The two tests covering the removed branches are gone; the test for the surviving branch
      still asserts that a pushed start becomes a remote-stream-started action and nothing more.
- [ ] Nothing anywhere in the solution references the removed cases — server, client, tests.
- [ ] `dotnet test` on `Tests/Unit` is green.
- [ ] `dotnet format` has run over the staged files.
