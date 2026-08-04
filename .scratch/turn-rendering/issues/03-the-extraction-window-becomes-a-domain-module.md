# 03 — The extraction window becomes a domain module

**What to build:** Cutting the extraction window and rendering it with turn markers
become pure domain functions, so asserting where a window starts no longer means
standing up a background worker.

Today the slicing rule is a private method on the extraction worker. Four of that
worker's ten unit tests exist only to check it, and each builds a fake extractor,
embedding service, memory store, thread store, metrics publisher and agent definition
provider to assert the result of a `Take` and a `TakeLast`.

Introduce an extraction window module in the domain owning both halves. One function
takes the persisted history, the anchor, the fallback content and the window size and
returns the window; another takes a window and returns the marked-up text. The existing
conversation window renderer is absorbed into it under the new name rather than left
beside it, so the module is the one place the marker vocabulary is written.

The module does not fetch. The worker keeps its single call to the thread state store
and hands the result over, which is what keeps the functions synchronous and
dependency-free.

Move the tests to match. The five renderer tests come across unchanged in substance under
the new name. The four slicing assertions — window built from history plus fallback,
missing history, anchor beyond the available history, and null thread key — become direct
calls. Add a marker cross-check asserting that the label the renderer emits for the
current turn, and the prefix it emits for context turns, each appear in the extraction
prompt constant. That is the first test in the repo to reference the memory prompts at
all, and it is what makes a one-sided rename go red.

The worker's drift test stays where it is: it asserts that a request anchored earlier
ignores messages that arrive later, which is about the anchor's purpose rather than the
slice arithmetic.

Behaviour is identical. Every rendered string is byte-for-byte what it was.

**Blocked by:** 02 — so the build function is born taking the anchor rather than an
integer that would have to be retyped straight after.

**Status:** ready-for-agent

- [x] A domain module owns both cutting the extraction window and rendering it with turn markers.
- [x] The cutting function is synchronous, takes the already-fetched history, and does no retrieval.
- [x] The extraction worker keeps only its fetch and delegates both cutting and rendering.
- [x] The old conversation window renderer no longer exists as a separate type; its tests live against the new module.
- [x] The four window-shape tests call the module directly and construct no fakes.
- [x] A test asserts the current-turn label and the context-turn prefix each appear in the extraction prompt constant.
- [x] The extraction worker drift test is untouched and passes.
