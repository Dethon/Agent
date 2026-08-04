# 02 — The anchor names its precondition

**What to build:** The point an extraction window is cut at stops being a bare integer,
and moving the recall call after the turn is persisted goes red.

Today the anchor is the persisted message count, and it is correct only because the chat
monitor enriches the user message before the turn is persisted. Nothing says so. If that
order ever changed, the extraction window would take the current message out of the
persisted history *and* append the fallback copy, handing the extractor the same turn
twice with the real one labelled as context. The memories that came back would be wrong
and the whole suite would stay green.

Introduce a memory anchor as a named value carried on the extraction request in place of
the integer. Build it through a factory whose name states the precondition — it is made
from the persisted count taken before the current turn is persisted — so the constraint
is visible at the single construction site in the recall hook and in every signature that
consumes it. The worker reads the anchor where it reads the integer today; the slicing
arithmetic is unchanged.

Then pin the precondition at the only seam that can see it. Using the existing chat
monitor unit test harness, a recording memory recall hook captures the persisted message
count it is handed at enrich time, and the test asserts that count excludes the turn
being built. This is the one new test in the whole feature that catches a real
regression rather than documenting one.

Behaviour is identical. No rendered string and no window boundary changes.

This ticket edits the recall hook and the chat monitor, the two files contested with the
metrics publishing and conversation group efforts. Both must have landed first.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [x] A memory anchor type exists, built through a factory whose name states that the count is taken before the current turn is persisted.
- [x] The extraction request carries the anchor instead of a bare integer, and every consumer takes it.
- [x] The recall hook builds the anchor at the one place it builds the count today.
- [x] A chat monitor test asserts that the recall hook is handed a persisted count excluding the turn being built, and fails if the call moves after persistence.
- [x] Window boundaries are unchanged: the existing extraction worker tests and the drift test pass untouched.
