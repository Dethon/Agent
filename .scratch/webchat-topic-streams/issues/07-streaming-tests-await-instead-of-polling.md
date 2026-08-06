# 07 — Streaming tests await instead of polling

**What to build:** the streaming tests assert a completed transition instead of waiting for one to
show up.

Nine test files poll for streaming state on a five-second loop, because until now no transition
was observably complete: a dispatch is fire-and-forget, so a test that wanted to know a reply had
finished could only keep looking. The lease exposes an awaitable completion, so those tests can
wait on the stream itself.

For a developer: a streaming test that fails says the behaviour is wrong rather than that five
seconds elapsed, and it fails in about as long as the behaviour takes.

The five files that poll for reasons unrelated to streaming are left alone.

**Blocked by:** 06 (every transition the tests wait on is in its final shape).

**Status:** resolved

- [x] Each of the nine streaming test files waits on the stream's completion rather than polling
      for store state, wherever what it is waiting for is a stream transition.
- [x] A test that is waiting for something other than a stream transition — a topic list load, an
      identity, a toast — keeps whatever it uses today; this ticket does not convert those.
- [x] No streaming test's assertions were weakened to make the conversion work; a case that
      genuinely cannot be expressed against the completion signal keeps its loop and is called
      out.
- [x] The streaming portion of the unit suite runs faster than before, and no test in it is
      timing-dependent on a five-second deadline.
- [x] `dotnet test` on `Tests/Unit` is green, run twice to show the conversion did not trade
      polling for a race.
- [x] `dotnet format` has run over the staged files.
