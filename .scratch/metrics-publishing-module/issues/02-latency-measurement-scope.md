# 02 — Latency measurement scope

**What to build:** A developer measuring how long something took writes one statement instead of three, and cannot get the failure path wrong.

Six sites today start a stopwatch, stop it, and publish a latency event carrying a stage and an elapsed duration. The tool approval chat client publishes the identical block twice, once per branch of a try/catch, because the measurement has to happen whether the tool returned or threw. Four of the six reuse the same elapsed value for a second, domain-specific event.

Replace the pattern with a scope. Opening one names the stage and the conversation and agent it belongs to; disposing it publishes the latency event. Because disposal covers the return path and the throw path alike, one statement replaces the per-branch duplication. The scope exposes its elapsed duration so a site that also emits a domain-specific event can carry the same value without a second stopwatch.

Nothing adopts it in this ticket. It is built beside the existing sites, which keep working unchanged, and later tickets migrate them.

One behaviour to be deliberate about: the scope publishes when it is disposed, including on an early return. That is correct for every site that adopts it, but it means a scope must be opened after any guard clause that can return before the measured work begins, not before it.

**Blocked by:** 01 — needs the void publish method to exist.

**Status:** ready-for-agent

- [ ] A scope type is opened from a metrics publisher, naming the latency stage and the conversation and agent identifiers.
- [ ] Disposing it publishes exactly one latency event carrying that stage and the measured duration.
- [ ] It publishes when the measured block returns normally.
- [ ] It publishes when the measured block throws, and does not suppress the exception.
- [ ] It exposes an elapsed duration readable before disposal, consistent with what the published event carries.
- [ ] Tested at the publisher seam against a recording publisher. No new test seam.
- [ ] Nothing outside the metrics module uses it yet; the solution builds and all existing tests pass.
