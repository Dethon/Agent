# 04 — Migrate the agent chat clients and the chat message store

**What to build:** A developer reading the agent's chat clients sees what each one measures, without the guards and null checks that currently surround every measurement.

Four types here publish metrics: the agent itself, the tool approval chat client, the OpenRouter chat client and the chat message store. Between them they carry one named safe-publish helper, three inline catch blocks and five null guards on the publisher. All of it exists to state the same rule the contract now states, and all of it goes.

The tool approval client is the clearest win: it publishes the identical latency block in both branches of a try/catch, because a tool call has to be measured whether it returned or threw. A measurement scope does that once.

This ticket also lands the nullable-publisher cleanup for these types. Coalesce the optional publisher to the shared no-op instance once, where it is stored, and make the stored field non-nullable. Keep the optional parameter with its null default so no test construction site has to change — the point is to delete the guards, not to churn seventy-four call sites in the test project.

Let synchronousness travel outward as in the other migration tickets.

**Blocked by:** 01, 02.

**Status:** ready-for-agent

- [ ] Every publish site in these four types uses the void call and passes no cancellation token.
- [ ] The agent's safe-latency helper is gone.
- [ ] The tool approval client publishes its tool-execution latency once, via a scope, covering both the returned and the thrown path.
- [ ] The three inline catch blocks guarding a publish are gone.
- [ ] The publisher is stored non-nullable in each type, coalesced once from the optional parameter to the no-op instance.
- [ ] The five null guards before a publish are gone.
- [ ] No test construction site in these types needs an argument it did not previously pass.
- [ ] Methods that became free of awaits are synchronous and have lost the `Async` suffix, along with their callers' awaits.
- [ ] The existing metrics, truncation and latency tests for these types pass.
