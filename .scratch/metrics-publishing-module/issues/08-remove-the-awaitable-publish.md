# 08 — Remove the awaitable publish

**What to build:** A developer publishing a metric has one method available, and it is the one that cannot fail. Nothing in the codebase can await a publish, catch it, or hand it a cancellation token, because there is nothing left to await.

This is the contract half of the expand–contract sequence. Every caller moved across in tickets 03 to 07, so the awaitable method now has no production callers. Remove it from the metrics publisher contract, along with the default implementation that bridged the two during migration. What remains is one void method.

The test project catches up here. Six recording publishers across five files implement the awaitable method, and two more use mock setups on it. They get simpler: a recording publisher records synchronously and needs no task, and the mock setups lose their callback plumbing. No test should gain a wait or a poll.

Rewrite the observability rule file, which describes the current shape and is the file an agent reads before touching metrics. It should state the split: a metrics publisher is fire-and-forget and cannot fail, a metric sink is the transport behind it and may fail, and being a metrics-publishing host is one registration call that also puts the service on the health roster.

**Blocked by:** 02, 03, 04, 05, 06, 07.

**Status:** ready-for-agent

- [ ] The metrics publisher contract exposes one void publish method and nothing else.
- [ ] The default implementation added in ticket 01 is gone.
- [ ] No production or test code references an awaitable publish on the publisher contract; the sink keeps its own.
- [ ] Every recording publisher and mock setup in the test project is updated, and none has gained a wait, a poll or a delay.
- [ ] The host registration theory from ticket 01 still passes.
- [ ] The observability rule file describes the publisher and sink split and the single registration call.
- [ ] The full solution builds and the whole test suite passes.
