# 08 — Contract: internalise freshness, assert policy per server

**What to build:** The contract step. With every server migrated, the old form is removed and the new contract is enforced by a test rather than by a comment.

Two things happen here, and neither is possible until all six migrations have landed.

**The freshness window becomes internal to the inbox.** It is currently a shared public constant that every emitter passes in. That is precisely what allowed six near-miss variants to exist and be fixed three separate times. Once no caller supplies it, no caller can substitute its own value. The eighteen-line doc comment that currently warns readers which liveness question is the right one can shrink to whatever still earns its place — the warning label is replaced by the type.

**The conformance theory asserts each server's declared delivery policy.** The existing six-row theory boots each real server's configuration and checks it exposes a conforming channel surface. It gains a per-server policy assertion, so the table becomes the single place where "this is a channel server, and this is the policy it chose" is verified against the real registration. That table is what would have caught all three rounds of the original defect.

After this ticket, a seventh channel cannot be added without declaring a policy, cannot compute liveness a different way, and appears in the conformance table by construction.

**Blocked by:** 03, 04, 05, 06, 07

**Status:** ready-for-agent

- [ ] The freshness window is internal to the inbox; no caller supplies it.
- [ ] No public liveness property remains on any channel server.
- [ ] The conformance theory asserts each of the six servers' declared delivery policy against its real registration.
- [ ] The theory still boots each real server configuration rather than a stub.
- [ ] The doc comment on the freshness constant is reduced to what the type does not already state.
- [ ] Every channel server builds and its tests pass.
- [ ] The two thin channel servers still reference the domain project alone, with no infrastructure dependency acquired anywhere in this feature.
- [ ] A count of the deletions is recorded in the pull request: six transport tools to one, six error filters to one, four emitters to one, six liveness properties to zero, two interfaces removed.
