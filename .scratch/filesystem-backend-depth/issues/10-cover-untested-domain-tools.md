# 10 — Cover the three untested domain tools

**What to build:** Read, remove and search get the same footing as the seven domain tools that already have tests. All three are tools the model calls on every mount, and none has a test of its own today.

Search is the one with real branching and the reason this is worth its own ticket: it chooses between a file path and a directory path, and must report an invalid argument when neither is given. That branch is uncovered, and it is the branch a model hits when it guesses at the argument shape.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] The read tool has a test file alongside its existing siblings, covering a successful read and a not-found path.
- [ ] The remove tool has one, covering a successful removal and a not-found path.
- [ ] The search tool has one, covering the file-path branch, the directory-path branch, and the invalid-argument case when neither is given.
- [ ] Each test asserts the envelope the tool returns, not the internals of the backend behind it.
