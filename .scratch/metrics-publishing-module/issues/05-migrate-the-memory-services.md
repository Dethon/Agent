# 05 — Migrate the memory services

**What to build:** Recall, extraction and dreaming publish their metrics through the void call, and the recall hook stops guarding a publish that can no longer fail.

The recall hook carries the one placement subtlety in this whole feature. Its stopwatch starts before a guard clause that returns early when there is nothing to recall, and its latency publish sits much further down, after the work. A measurement scope publishes on disposal, including on that early return, so opening the scope where the stopwatch starts today would report a recall latency for a recall that never happened. Open the scope after the guard instead. The guard is a string emptiness check, so nothing measurable moves.

The recall hook also emits a recall event carrying the same duration as its latency event; take that value from the scope rather than keeping a second stopwatch.

**Blocked by:** 01, 02.

**Status:** ready-for-agent

- [ ] Every publish site in the memory services uses the void call and passes no cancellation token.
- [ ] The recall hook's inline catch block around its latency publish is gone.
- [ ] The recall hook opens its measurement scope after the early-return guard, so a recall that returns before doing work publishes no latency event.
- [ ] The recall event and the latency event carry the same duration, taken from one scope.
- [ ] Methods that became free of awaits are synchronous and have lost the `Async` suffix, along with their callers' awaits.
- [ ] The existing recall, extraction and dreaming tests pass, including the drift tests.
