# 01 — Delete the untracked streaming verbs

**What to build:** a chat client where the only way to open a topic stream is a way that tracks
it. Two public streaming verbs exist that no production code calls, and both open a stream
without registering it as the topic's stream in flight — so a caller added later could open one
that nothing knows about, and a second send would open another over the top of it. They go, and
the substantial test surface that drove them re-points onto the entry points production actually
uses: sending a message, and starting a resumed stream.

Nothing a user can see changes. What changes is that the streaming test suite starts describing
the real paths.

**Blocked by:** None — can start immediately.

**Status:** resolved

- [x] The streaming service interface exposes no verb that streams without tracking the topic
      stream; the two unused verbs are gone from both the interface and the implementation.
- [x] Every test that drove those two verbs now drives sending a message or starting a resumed
      stream, and still asserts the same behaviour: chunk accumulation, interleaved message ids,
      turn finalisation, error classification for both thrown exceptions and error chunks,
      transient-versus-real error handling, and resume de-duplicating content it already has.
- [x] No test asserts on a verb that no longer exists, and no test was deleted to make the suite
      pass — a re-pointed test that cannot express its case at the new entry point is reported
      rather than dropped.
- [x] `dotnet test` on `Tests/Unit` is green.
- [x] `dotnet format` has run over the staged files (the pre-commit hook does this; make the
      working tree match the commit).
